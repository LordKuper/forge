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
/// responsibilities —
/// cursor-based dedup across ticks, `notifications.enabled` gating, per-notification failure
/// isolation, and body composition/redaction — using a fake <see cref="INotificationService"/>
/// so no real OS call is ever made.</summary>
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

        Assert.True(File.Exists(NotificationDeliveryCursorStore.CursorFilePath(environment.ProjectRoot)));

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
    /// never changes workflow state" — an OS adapter's own exception must never fault this service's
    /// BackgroundService.ExecuteTask, matching ResumeSchedulerHostedService's own per-item isolation.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ADeliveryFailureDoesNotCrashTheServiceOrBlockTheCursorFromAdvancing()
    {
        using TestEnvironment environment = await InitializedAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);

        FakeNotificationService throwing = new() { ThrowOnNotify = true };
        using NotificationDeliveryHostedService service = CreateService(environment, throwing);
        await service.StartAsync(cancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }
        finally
        {
            await service.StopAsync(cancellationToken);
        }

        Assert.Empty(throwing.Delivered);
        Assert.Null(service.ExecuteTask!.Exception);
        Assert.True(File.Exists(NotificationDeliveryCursorStore.CursorFilePath(environment.ProjectRoot)));
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

    /// <summary>A stale/corrupted cursor (this service's own write, so only reachable via direct
    /// tampering in a test) must not permanently wedge delivery -- see ADR 0024's "resume cleanly
    /// from now" recovery choice.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AStaleCursorFileIsReplacedRatherThanRetriedForever()
    {
        using TestEnvironment environment = await InitializedAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string cursorPath = NotificationDeliveryCursorStore.CursorFilePath(environment.ProjectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(cursorPath)!);
        await File.WriteAllTextAsync(cursorPath, "not a valid cursor token", cancellationToken);

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
        string replaced = await File.ReadAllTextAsync(cursorPath, cancellationToken);
        Assert.NotEqual("not a valid cursor token", replaced);
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

        public bool ThrowOnNotify { get; init; }

        public Task NotifyAsync(string title, string body, CancellationToken cancellationToken)
        {
            if (ThrowOnNotify)
            {
                throw new InvalidOperationException("Simulated OS notification delivery failure.");
            }

            Delivered.Add((title, body));
            return Task.CompletedTask;
        }
    }
}
