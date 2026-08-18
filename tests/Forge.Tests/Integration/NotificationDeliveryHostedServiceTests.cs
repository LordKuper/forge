using Forge.Application;
using Forge.Configuration;
using Forge.Domain;
using Forge.Host;
using Forge.Localization;
using Forge.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.IntegrationTests;

/// <summary>ADR 0024: the delivery pipeline that projects durable sprint transitions onto
/// best-effort local notifications. <c>Forge.UnitTests.NotificationProjectorTests</c> already
/// fully proves kind-mapping correctness; these tests instead cover this service's own
/// responsibilities — cursor-based dedup across ticks (including stale-cursor recovery),
/// `notifications.enabled` gating, per-notification failure isolation, and body composition/
/// redaction — using a fake <see cref="INotificationService"/> so no real OS call is ever made.
/// </summary>
public sealed class NotificationDeliveryHostedServiceTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheServiceDeliversANotificationForAnAwaitingHumanGateExactlyOnceAcrossMultipleTicks()
    {
        using TestEnvironment environment = await InitializedAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        FakeNotificationService fake = new();
        using NotificationDeliveryHostedService service = CreateService(environment, fake);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForDeliveryAsync(fake, cancellationToken);
            // A second tick over the same durable state must not re-deliver: the cursor already
            // advanced past this event on the first tick.
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            Assert.Single(fake.Delivered);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        Assert.Null(service.ExecuteTask!.Exception);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheServiceComposesARedactedTitleAndBodyNamingTheSprint()
    {
        using TestEnvironment environment = await InitializedAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        FakeNotificationService fake = new();
        using NotificationDeliveryHostedService service = CreateService(environment, fake);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForDeliveryAsync(fake, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        (string title, string body) = Assert.Single(fake.Delivered);
        SurfaceText text = SurfaceText.For(new ResourceLocalizationCatalog(), "en");
        Assert.Equal(text.Resolve(MessageKeys.NotificationAwaitingHumanTitle), title);
        Assert.Contains(sprintId.Value.ToString("D"), body, StringComparison.Ordinal);
        Assert.Contains(text.Resolve(MessageKeys.NotificationSprintLabel), body, StringComparison.Ordinal);
    }

    /// <summary>ADR 0024: "while disabled the cursor still advances, so re-enabling later never
    /// delivers a backlog of stale historical notifications" — the central design decision behind
    /// gating delivery, not the cursor read itself, so this proves it directly rather than trusting
    /// the ADR's own claim.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DisablingNotificationsSkipsDeliveryButStillAdvancesTheCursor()
    {
        using TestEnvironment environment = await InitializedAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ConfigurationWriteResult disabled = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User, environment.ProjectRoot, "notifications.enabled", "false", cancellationToken);
        Assert.True(disabled.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        FakeNotificationService fake = new();
        using NotificationDeliveryHostedService service = CreateService(environment, fake);
        await service.StartAsync(cancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            Assert.Empty(fake.Delivered);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        // Not just "a cursor file exists" -- the watermark for THIS sprint must have genuinely
        // advanced past its awaiting_human event, proving the disabled-delivery tick still moved
        // the cursor forward rather than merely writing an empty/unrelated one.
        ControlEventsCursor cursorWhileDisabled =
            await ReadCursorAsync(environment.ProjectRoot, cancellationToken);
        Assert.True(cursorWhileDisabled.Watermarks.TryGetValue(
            sprintId.Value.ToString("D"), out long watermarkWhileDisabled) && watermarkWhileDisabled >= 0);

        ConfigurationWriteResult enabled = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User, environment.ProjectRoot, "notifications.enabled", "true", cancellationToken);
        Assert.True(enabled.Succeeded);
        using NotificationDeliveryHostedService second = CreateService(environment, fake);
        await second.StartAsync(cancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }
        finally
        {
            await second.StopAsync(cancellationToken);
        }

        Assert.Empty(fake.Delivered);
    }

    /// <summary>ADR 0005: "A notification is never the authoritative record and a delivery failure
    /// never changes workflow state" — an OS adapter's own exception for ONE event must never fault
    /// this service's BackgroundService.ExecuteTask or prevent a DIFFERENT event from being
    /// delivered, matching ResumeSchedulerHostedService's own per-item isolation. Two sprints, not
    /// one, so isolation is actually exercised rather than merely "the one call didn't crash the
    /// test."</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ADeliveryFailureIsIsolatedAndTheCursorStillAdvancesPastBothEvents()
    {
        using TestEnvironment environment = await InitializedAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId failingSprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, failingSprintId, cancellationToken);
        SprintId succeedingSprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, succeedingSprintId, cancellationToken);

        string failingIdText = failingSprintId.Value.ToString("D");
        string succeedingIdText = succeedingSprintId.Value.ToString("D");
        FakeNotificationService fake = new()
        {
            ShouldThrow = (_, body) => body.Contains(failingIdText, StringComparison.Ordinal),
        };
        using NotificationDeliveryHostedService service = CreateService(environment, fake);
        await service.StartAsync(cancellationToken);
        try
        {
            await WaitForDeliveryAsync(fake, cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        Assert.Null(service.ExecuteTask!.Exception);
        (string Title, string Body) delivered = Assert.Single(fake.Delivered);
        Assert.Contains(succeedingIdText, delivered.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(fake.Delivered, item => item.Body.Contains(failingIdText, StringComparison.Ordinal));

        // The cursor must have advanced past BOTH events, not just the one that delivered
        // successfully -- a delivery failure isolates that one notification, not the sweep's own
        // progress through the journal.
        ControlEventsCursor cursor = await ReadCursorAsync(environment.ProjectRoot, cancellationToken);
        Assert.True(cursor.Watermarks.TryGetValue(failingIdText, out long failingWatermark) && failingWatermark >= 0);
        Assert.True(
            cursor.Watermarks.TryGetValue(succeedingIdText, out long succeedingWatermark) &&
            succeedingWatermark >= 0);
    }

    /// <summary>Round 1 review of PR #64 found the ORIGINAL stale-cursor recovery replayed the
    /// project's full history: <see cref="ControlEventsPage.Empty"/>'s own fresh-anchor token has
    /// empty watermarks, which does not itself skip already-delivered events, only makes them look
    /// unseen again. This test proves the actual fix (<c>CatchUpToNowAsync</c>) rather than only
    /// the weaker "the file changed" claim the original version of this test made: an event
    /// genuinely delivered once, followed by cursor corruption and a restart, must never be
    /// delivered again.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AStaleCursorRecoversWithoutReplayingAnAlreadyDeliveredEvent()
    {
        using TestEnvironment environment = await InitializedAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        FakeNotificationService first = new();
        using (NotificationDeliveryHostedService firstService = CreateService(environment, first))
        {
            await firstService.StartAsync(cancellationToken);
            try
            {
                await WaitForDeliveryAsync(first, cancellationToken);
            }
            finally
            {
                await firstService.StopAsync(cancellationToken);
            }
        }

        string cursorPath = NotificationDeliveryCursorStore.CursorFilePath(environment.ProjectRoot);
        await File.WriteAllTextAsync(cursorPath, "not a valid cursor token", cancellationToken);

        FakeNotificationService second = new();
        using NotificationDeliveryHostedService secondService = CreateService(environment, second);
        await secondService.StartAsync(cancellationToken);
        try
        {
            // No new notification-worthy event exists after recovery, so nothing should ever be
            // delivered -- several ticks' worth of delay proves it stays empty, not merely that
            // redelivery "hasn't happened yet."
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }
        finally
        {
            await secondService.StopAsync(cancellationToken);
        }

        Assert.Null(secondService.ExecuteTask!.Exception);
        Assert.Empty(second.Delivered);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheServiceTicksOverAProjectWithNoNotificationWorthyEventsWithoutFailing()
    {
        using TestEnvironment environment = await InitializedAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeNotificationService fake = new();
        using NotificationDeliveryHostedService service = CreateService(environment, fake);

        await service.StartAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        await service.StopAsync(cancellationToken);

        Assert.Empty(fake.Delivered);
        Assert.Null(service.ExecuteTask!.Exception);
    }

    private static NotificationDeliveryHostedService CreateService(
        TestEnvironment environment, INotificationService notifications) =>
        new(
            new(environment.ProjectRoot, TimeSpan.FromMilliseconds(50)),
            environment.Application,
            environment.Resolve<ControlEventsReader>(),
            notifications,
            new ResourceLocalizationCatalog(),
            NullLogger<NotificationDeliveryHostedService>.Instance);

    private static async Task<ControlEventsCursor> ReadCursorAsync(string projectRoot, CancellationToken cancellationToken)
    {
        string content = await File.ReadAllTextAsync(
            NotificationDeliveryCursorStore.CursorFilePath(projectRoot), cancellationToken);
        Assert.True(ControlEventsCursorCodec.TryDecode(content, out ControlEventsCursor cursor));
        return cursor;
    }

    private static async Task WaitForDeliveryAsync(FakeNotificationService fake, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 40 && fake.Delivered.Count == 0; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        Assert.NotEmpty(fake.Delivered);
    }

    private static async Task RunToRunningAsync(
        SprintOrchestrator orchestrator,
        string root,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(root, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(root, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(root, sprintId, toReady.Sprint!.Version, SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
    }

    private static async Task<TestEnvironment> InitializedAsync()
    {
        TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        return environment;
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public List<(string Title, string Body)> Delivered { get; } = [];

        public Func<string, string, bool>? ShouldThrow { get; init; }

        public Task NotifyAsync(string title, string body, CancellationToken cancellationToken)
        {
            if (ShouldThrow?.Invoke(title, body) == true)
            {
                throw new InvalidOperationException("Simulated OS notification delivery failure.");
            }

            Delivered.Add((title, body));
            return Task.CompletedTask;
        }
    }
}
