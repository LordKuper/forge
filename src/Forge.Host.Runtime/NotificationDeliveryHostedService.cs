using System.Globalization;
using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
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
    private const string EnabledKey = "notifications.enabled";

    private static readonly Action<ILogger, Exception> LogTickFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2020, "NotificationDeliveryTickFailed"),
        "Notification delivery sweep failed; retrying next tick.");

    private static readonly Action<ILogger, Guid, Exception> LogDeliveryFailed = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2021, "NotificationDeliveryFailed"),
        "Delivering a notification failed for sprint {SprintId}; continuing with the rest.");

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
        try
        {
            string? cursorToken = await NotificationDeliveryCursorStore
                .LoadAsync(options.ProjectRoot, cancellationToken)
                .ConfigureAwait(false);
            page = await eventsReader.ReadAsync(options.ProjectRoot, cursorToken, cancellationToken)
                .ConfigureAwait(false);
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
        // partial-write race) cannot be safely retried with the same token forever -- persist the
        // fresh anchor ReadControlEvents already returned and resume cleanly from now, skipping
        // delivery for this tick only rather than replaying the project's full history as "new".
        if (page.DiagnosticCode != DiagnosticCodes.None)
        {
            await SaveCursorAsync(page.Cursor, cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<NotificationProjection> projections = NotificationProjector.Project(page.Events);
        if (projections.Count > 0 && await IsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            StartupStatus status = await application
                .GetStartupStatusAsync(options.ProjectRoot, cancellationToken)
                .ConfigureAwait(false);
            SurfaceText text = SurfaceText.For(catalog, status.Language.Ui);
            foreach (NotificationProjection projection in projections)
            {
                await DeliverAsync(text, projection, cancellationToken).ConfigureAwait(false);
            }
        }

        await SaveCursorAsync(page.Cursor, cancellationToken).ConfigureAwait(false);
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

    private async Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        ConfigurationView user =
            await application.GetUserConfigurationAsync(cancellationToken).ConfigureAwait(false);
        EffectiveConfigurationValue? value =
            user.Values.FirstOrDefault(item => item.Key == EnabledKey);
        return value?.Value.ValueKind != JsonValueKind.False;
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
}
