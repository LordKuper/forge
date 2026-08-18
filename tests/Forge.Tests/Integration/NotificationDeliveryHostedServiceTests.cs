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

        // Not just "a cursor file exists," and not just "the watermark is present" (which proves
        // nothing about how FAR it advanced): compare against the ground truth of reading this
        // sprint's own full event history directly, independent of the service under test, so the
        // assertion proves the disabled tick's cursor is genuinely caught up to it -- not merely
        // present at some smaller, stale value.
        ControlEventsCursor cursorWhileDisabled =
            await ReadCursorAsync(environment.ProjectRoot, cancellationToken);
        Assert.True(cursorWhileDisabled.Watermarks.TryGetValue(
            sprintId.Value.ToString("D"), out long watermarkWhileDisabled));
        long expectedWatermark = await ReadGroundTruthWatermarkAsync(environment, sprintId, cancellationToken);
        Assert.Equal(expectedWatermark, watermarkWhileDisabled);

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
        // progress through the journal. Compared against each sprint's own independently-computed
        // ground-truth watermark (round 3 review found `>= 0` alone proves nothing about how far a
        // watermark actually advanced, the same gap round 2 already fixed in the sibling
        // "disabled" test but left unfixed here).
        ControlEventsCursor cursor = await ReadCursorAsync(environment.ProjectRoot, cancellationToken);
        Assert.True(cursor.Watermarks.TryGetValue(failingIdText, out long failingWatermark));
        Assert.True(cursor.Watermarks.TryGetValue(succeedingIdText, out long succeedingWatermark));
        Assert.Equal(
            await ReadGroundTruthWatermarkAsync(environment, failingSprintId, cancellationToken), failingWatermark);
        Assert.Equal(
            await ReadGroundTruthWatermarkAsync(environment, succeedingSprintId, cancellationToken),
            succeedingWatermark);
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

    /// <summary>Round 3 review found this exact defect for the third time in this PR's own
    /// history: an exception type <see cref="ResumeSchedulerHostedService"/> already catches
    /// (<c>FileSprintEventLog.LoadValidatedEventsAsync</c> throws <see cref="InvalidDataException"/>
    /// for a corrupt journal, reached via <see cref="ControlEventsReader.ReadAsync"/> the same way)
    /// was missing from this service's own tick-level catch filter despite its doc comment already
    /// claiming parity with that shape.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ACorruptJournalDoesNotPermanentlyFaultTheService()
    {
        using TestEnvironment environment = await InitializedAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        string eventsPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "events.jsonl");
        await File.AppendAllTextAsync(eventsPath, "{ not valid json\n", cancellationToken);

        FakeNotificationService fake = new();
        using NotificationDeliveryHostedService service = CreateService(environment, fake);
        await service.StartAsync(cancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        Assert.Null(service.ExecuteTask!.Exception);
        Assert.Empty(fake.Delivered);
    }

    /// <summary>Round 3 review: an unreadable user configuration must never be treated as
    /// "enabled" (see the config-gating fix in <c>ReadNotificationSettingsAsync</c>) -- proven end
    /// to end here rather than only at the unit level, including that the cursor still advances
    /// exactly as far as a healthy read would, matching the "disabled" test's own ground-truth
    /// comparison.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnUnreadableConfigurationFailsClosedAndStillAdvancesTheCursor()
    {
        using TestEnvironment environment = await InitializedAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        string userConfigPath = ConfigurationStoreFactory.UserPath(environment);
        Directory.CreateDirectory(Path.GetDirectoryName(userConfigPath)!);
        await File.WriteAllTextAsync(userConfigPath, "{ not valid json", cancellationToken);

        FakeNotificationService fake = new();
        using NotificationDeliveryHostedService service = CreateService(environment, fake);
        await service.StartAsync(cancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        Assert.Null(service.ExecuteTask!.Exception);
        Assert.Empty(fake.Delivered);
        ControlEventsCursor cursor = await ReadCursorAsync(environment.ProjectRoot, cancellationToken);
        Assert.True(cursor.Watermarks.TryGetValue(sprintId.Value.ToString("D"), out long watermark));
        Assert.Equal(await ReadGroundTruthWatermarkAsync(environment, sprintId, cancellationToken), watermark);
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

    /// <summary>The independently-computed "how far COULD this sprint's own watermark have
    /// advanced" ground truth: reads its full event history directly via
    /// <see cref="ControlEventsReader"/>, bypassing the service under test entirely, so a caller
    /// can assert a persisted cursor is genuinely caught up rather than merely non-negative.
    /// </summary>
    private static async Task<long> ReadGroundTruthWatermarkAsync(
        TestEnvironment environment, SprintId sprintId, CancellationToken cancellationToken)
    {
        ControlEventsPage groundTruth = await environment.Resolve<ControlEventsReader>()
            .ReadAsync(environment.ProjectRoot, null, cancellationToken);
        Assert.True(ControlEventsCursorCodec.TryDecode(groundTruth.Cursor, out ControlEventsCursor groundTruthCursor));
        Assert.True(groundTruthCursor.Watermarks.TryGetValue(
            sprintId.Value.ToString("D"), out long expectedWatermark));
        return expectedWatermark;
    }

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
