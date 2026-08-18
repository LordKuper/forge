using System.Globalization;
using System.Text.Json;
using Forge.Application;
using Forge.Infrastructure;
using Forge.Localization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Forge.Host;

/// <summary>How often <see cref="NotificationDeliveryHostedService"/> sweeps for newly durable
/// attention states. Configurable only so a test can prove the loop itself fires without waiting
/// out the real interval.</summary>
public sealed record NotificationDeliveryOptions(string ProjectRoot, TimeSpan? PollInterval = null)
{
    public TimeSpan Interval => PollInterval ?? TimeSpan.FromSeconds(30);
}

/// <summary>
/// ADR 0005/0024: projects durable `awaiting_human`/`blocked`/`failed`/`completed` sprint
/// transitions onto best-effort local OS notifications. Deduplicated by a durable resume cursor
/// (<see cref="NotificationDeliveryCursorStore"/>) built on the same <see cref="ControlEventsReader"/>
/// mechanism `forge events --cursor` already uses — an event already advanced past is never
/// re-delivered, even across a Host restart. Config-gated by `notifications.enabled` (default on);
/// while disabled the cursor still advances, so re-enabling later never delivers a backlog of
/// stale historical notifications. Every notification body is redacted (<see cref="SecretRedactor"/>)
/// before it ever reaches <see cref="INotificationService"/>.
/// </summary>
/// <remarks>
/// Holds no per-tick memory beyond the persisted cursor: a Host restart resumes exactly where it
/// left off, matching <see cref="ResumeSchedulerHostedService"/>'s own "nothing lost on restart"
/// property. <see cref="ControlPlaneHostedService"/> owns this service's lifetime directly —
/// starting it only after winning the project lease and stopping it before releasing that lease —
/// so a Host that loses the lease race never runs a tick against durable state it doesn't own.
/// </remarks>
public sealed class NotificationDeliveryHostedService(
    NotificationDeliveryOptions options,
    ForgeApplication application,
    ControlEventsReader eventsReader,
    INotificationService notifications,
    ILocalizationCatalog catalog,
    ILogger<NotificationDeliveryHostedService> logger) : BackgroundService
{
    /// <summary>Bounds <see cref="CatchUpToNowAsync"/>'s loop -- 500,000 events (500 per read) is
    /// far past any realistic project, so hitting it only ever happens against a pathologically
    /// large one; logged and left for later ticks to keep advancing through rather than blocking
    /// this tick indefinitely.</summary>
    private const int MaxCatchUpReads = 1_000;

    private const string EnabledKey = "notifications.enabled";
    private const string LanguageKey = "language.ui";

    private static readonly Action<ILogger, Exception> LogTickFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2020, "NotificationDeliveryTickFailed"),
        "Notification delivery sweep failed; retrying next tick.");

    private static readonly Action<ILogger, Guid, Exception> LogDeliveryFailed = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2021, "NotificationDeliveryFailed"),
        "Delivering a notification failed for sprint {SprintId}; continuing with the rest.");

    private static readonly Action<ILogger, Exception?> LogCatchUpBoundReached = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2022, "NotificationDeliveryCatchUpBoundReached"),
        "Stale-cursor recovery reached its read bound before catching up to the current journal " +
            "tip; the remainder will be caught up on by later ticks.");

    /// <summary>Unlike <see cref="ResumeSchedulerHostedService"/>'s own immediate-first-tick
    /// design — where promptly promoting a stuck node is a real correctness concern — a "best-
    /// effort" notification arriving one interval late on Host startup is a non-issue, so this
    /// waits for the first interval to elapse before its first sweep rather than ticking
    /// immediately. Deliberately reduces Host-startup work (one fewer synchronous
    /// <see cref="ControlEventsReader"/> read plus cursor file I/O per Host start) rather than
    /// only being a test-suite convenience.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await TickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        ControlEventsPage page;
        IReadOnlyList<NotificationProjection> projections;
        NotificationSettings? settings;
        try
        {
            string? cursorToken = await NotificationDeliveryCursorStore
                .LoadAsync(options.ProjectRoot, cancellationToken)
                .ConfigureAwait(false);
            page = await eventsReader.ReadAsync(options.ProjectRoot, cursorToken, cancellationToken)
                .ConfigureAwait(false);
            projections = page.DiagnosticCode == DiagnosticCodes.None
                ? NotificationProjector.Project(page.Events)
                : [];
            // Reading settings shares this tick's own failure isolation: an escaping exception here
            // must never permanently fault ExecuteTask the way it would for a plain BackgroundService
            // not registered via AddHostedService (nothing else observes that fault).
            settings = projections.Count > 0
                ? await ReadNotificationSettingsAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogTickFailed(logger, exception);
            return;
        }

        // A stale/corrupted cursor (this service's own write, so expected only in a genuine
        // partial-write race) cannot be safely retried with the same token forever. It also cannot
        // simply persist ReadControlEvents' own fresh-anchor token as-is: that token's watermarks
        // are empty, which does not itself skip the project's existing events -- it only makes them
        // look unseen again, replaying the full history as "new" on the very next tick. Catching up
        // to the journal's current tip first, then persisting THAT cursor, is what actually resumes
        // "from now."
        if (page.DiagnosticCode != DiagnosticCodes.None)
        {
            string caughtUpCursor = await CatchUpToNowAsync(cancellationToken).ConfigureAwait(false);
            await SaveCursorAsync(caughtUpCursor, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (settings is { Enabled: true })
        {
            SurfaceText text = SurfaceText.For(catalog, settings.LanguageTag);
            foreach (NotificationProjection projection in projections)
            {
                await DeliverAsync(text, projection, cancellationToken).ConfigureAwait(false);
            }
        }

        await SaveCursorAsync(page.Cursor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Advances through every currently-durable event without delivering any of them, so
    /// stale-cursor recovery resumes cleanly "from now" instead of the naive fresh-anchor token
    /// replaying the project's full history as new on the next tick.</summary>
    private async Task<string> CatchUpToNowAsync(CancellationToken cancellationToken)
    {
        string cursor = ControlEventsCursorCodec.Encode(ControlEventsCursor.Empty);
        for (int read = 0; read < MaxCatchUpReads; read++)
        {
            ControlEventsPage page = await eventsReader
                .ReadAsync(options.ProjectRoot, cursor, cancellationToken)
                .ConfigureAwait(false);
            cursor = page.Cursor;
            if (page.Events.Count < ControlEventsReader.MaxEventsPerRead)
            {
                return cursor;
            }
        }

        LogCatchUpBoundReached(logger, null);
        return cursor;
    }

    private async Task DeliverAsync(
        SurfaceText text,
        NotificationProjection projection,
        CancellationToken cancellationToken)
    {
        try
        {
            string title = text.Resolve(TitleKey(projection.Kind));
            string body = SecretRedactor.Redact(string.Create(
                CultureInfo.InvariantCulture,
                $"{text.Resolve(MessageKeys.NotificationSprintLabel)} {projection.SprintId:D}"));
            await notifications.NotifyAsync(title, body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // ADR 0005: "A notification is never the authoritative record and a delivery failure
            // never changes workflow state" -- any exception an adapter's own OS call raises is
            // isolated here exactly like ResumeSchedulerHostedService isolates one sprint's
            // failure from the rest, never allowed to stop the sweep.
            LogDeliveryFailed(logger, projection.SprintId, exception);
        }
    }

    private async Task<NotificationSettings> ReadNotificationSettingsAsync(CancellationToken cancellationToken)
    {
        ConfigurationView user =
            await application.GetUserConfigurationAsync(cancellationToken).ConfigureAwait(false);
        bool enabled = user.Values.FirstOrDefault(item => item.Key == EnabledKey)?.Value.ValueKind !=
            JsonValueKind.False;
        string? languageTag = user.Values.FirstOrDefault(item => item.Key == LanguageKey) is { } language &&
            language.Value.ValueKind == JsonValueKind.String
            ? language.Value.GetString()
            : null;
        return new(enabled, string.IsNullOrEmpty(languageTag) ? "en" : languageTag);
    }

    private async Task SaveCursorAsync(string cursor, CancellationToken cancellationToken)
    {
        try
        {
            await NotificationDeliveryCursorStore
                .SaveAsync(options.ProjectRoot, cursor, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogTickFailed(logger, exception);
        }
    }

    private static string TitleKey(NotificationKind kind) => kind switch
    {
        NotificationKind.AwaitingHuman => MessageKeys.NotificationAwaitingHumanTitle,
        NotificationKind.Blocked => MessageKeys.NotificationBlockedTitle,
        NotificationKind.Failed => MessageKeys.NotificationFailedTitle,
        NotificationKind.Completed => MessageKeys.NotificationCompletedTitle,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Read once per tick, only when there is at least one projection to potentially
    /// deliver -- avoids <see cref="ForgeApplication.GetUserConfigurationAsync"/> entirely on the
    /// (typical) tick with nothing new.</summary>
    private sealed record NotificationSettings(bool Enabled, string LanguageTag);
}
