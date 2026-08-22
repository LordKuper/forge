using System.CommandLine;
using System.Globalization;
using Forge.Application;
using Forge.Cli;
using Forge.Domain;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.AcceptanceTests;

/// <summary>ADR 0044/0047's human-only `workflow.stop_operation` capability, wired to
/// `forge attempt stop`. Mirrors <c>HumanGateAndSupersessionCliTests</c>' own coverage shape for
/// `attempt.supersede`.</summary>
public sealed class StopOperationCliTests
{
    private static readonly IReadOnlyList<NodeDefinition> WorkGraph = [new("a", NodeKind.Work, [])];

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task AttemptStopCommandDurablyRecordsTheStopIntentThroughForgeApplication()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: WorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        SprintWorkflowState running = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", running.Nodes["a"].Version, cancellationToken);
        Assert.True(started.Succeeded);

        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "attempt", "stop", started.AttemptId!.Value.ToString(), "--sprint", sprintId.Value.ToString(),
                "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(Text(catalog).Resolve(MessageKeys.AttemptStopped), output.ToString(), StringComparison.Ordinal);
        SprintWorkflowState afterStop =
            (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.NotNull(
            afterStop.Attempts[started.AttemptId!.Value.ToString("D")].StopRequestedAt);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task AttemptStopCommandRequiresConfirmation()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        RootCommand root = CliApplication.CreateRootCommand(
            Text(new ResourceLocalizationCatalog()), output, environment.Application, diagnostics,
            isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "attempt", "stop", Guid.NewGuid().ToString(), "--sprint", Guid.NewGuid().ToString(),
                "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(DiagnosticCodes.ConfirmationRequired, diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task AttemptStopCommandRefusesANonInteractiveSessionEvenWithYes()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        RootCommand root = CliApplication.CreateRootCommand(
            Text(new ResourceLocalizationCatalog()), output, environment.Application, diagnostics,
            isInteractive: () => false);

        int exitCode = await root
            .Parse([
                "attempt", "stop", Guid.NewGuid().ToString(), "--sprint", Guid.NewGuid().ToString(), "--yes",
                "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(DiagnosticCodes.PermissionDenied, diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task AttemptStopCommandRoutesThroughTheResolvedMutations()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        FakeForgeMutations mutations = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        Guid sprintId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(new ResourceLocalizationCatalog()),
            output,
            environment.Application,
            resolveMutations: (_, _) => Task.FromResult<IForgeMutations>(mutations),
            isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "attempt", "stop", attemptId.ToString(), "--sprint", sprintId.ToString(), "--yes",
                "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, mutations.StopCurrentOperationCalls);
        Assert.Equal(sprintId, mutations.LastStopSprintId);
        Assert.Equal(attemptId, mutations.LastStopAttemptId);
        Assert.True(mutations.LastStopConfirmed);
    }

    private static SurfaceText Text(ResourceLocalizationCatalog catalog) => new(catalog, CultureInfo.InvariantCulture);

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
}
