using System.CommandLine;
using System.Globalization;
using Forge.Application;
using Forge.Cli;
using Forge.Domain;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.AcceptanceTests;

/// <summary>Stage 11, P11.56-P11.66: the human-only `workflow.test_work` capability, wired to
/// `forge test-work added|no-new-tests`. Deliberately mirrors
/// <see cref="ConfirmationCliTests"/>'s own shape and coverage — the same ADR 0023
/// interactive-session control and mandatory, never-bypassed confirmation apply here too.</summary>
public sealed class TestWorkCliTests
{
    private static readonly IReadOnlyList<NodeDefinition> TestWorkGraph =
        [new("test_work", NodeKind.Work, [], NodeRole.TestWork)];

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task TestWorkAddedCommandRecordsATestsAddedOutcomeAndSucceedsTheNode()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TestWorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "test-work", "added", "--sprint", sprintId.Value.ToString(), "--node", "test_work",
                "--justification", "Added a regression test for the reported off-by-one.",
                "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(Text(catalog).Resolve(MessageKeys.TestWorkRecorded), output.ToString(), StringComparison.Ordinal);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["test_work"].State);
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        TestWorkArtifact artifact = Assert.Single(
            await scheduler.GetTestWorkAsync(environment.ProjectRoot, sprintId, cancellationToken));
        Assert.Equal(TestWorkOutcome.TestsAdded, artifact.Outcome);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task TestWorkNoNewTestsCommandRecordsAJustifiedOutcomeAndSucceedsTheNodeWithoutBlockingTheSprint()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TestWorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "test-work", "no-new-tests", "--sprint", sprintId.Value.ToString(), "--node", "test_work",
                "--justification", "Pure documentation change; existing checks cover every material risk.",
                "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        // Unlike confirmation's NotConfirmed outcome, neither test-work outcome blocks the sprint --
        // no downstream eligibility gate reads this artifact's content.
        Assert.Equal(0, exitCode);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["test_work"].State);
        Assert.Equal(SprintState.ReadyToFinalize, state.Sprint.State);
    }

    /// <summary>ADR 0005: mandatory confirmation even though the subcommand itself
    /// (<c>added</c> vs <c>no-new-tests</c>) is already an explicit choice.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task TestWorkCommandRequiresConfirmation()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TestWorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, diagnostics, isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "test-work", "added", "--sprint", sprintId.Value.ToString(), "--node", "test_work",
                "--justification", "Added a test.", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(DiagnosticCodes.ConfirmationRequired, diagnostics.ToString(), StringComparison.Ordinal);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes["test_work"].State);
    }

    /// <summary>ADR 0023: the same technical control every other human-only command shares. Refused
    /// before argument validation, before any mutation call, and unconditionally.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task TestWorkCommandRefusesANonInteractiveSessionEvenWithYes()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TestWorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        FakeForgeMutations mutations = new();
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            diagnostics,
            resolveMutations: (_, _) => Task.FromResult<IForgeMutations>(mutations),
            isInteractive: () => false);

        int exitCode = await root
            .Parse([
                "test-work", "added", "--sprint", sprintId.Value.ToString(), "--node", "test_work",
                "--justification", "Added a test.", "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(ExitCodes.Authorization, exitCode);
        Assert.Contains(DiagnosticCodes.PermissionDenied, diagnostics.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, mutations.RecordTestWorkCalls);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes["test_work"].State);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task TestWorkCommandRejectsAWhitespaceOnlyJustificationBeforeStartingTheAttempt()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: TestWorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, diagnostics, isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "test-work", "added", "--sprint", sprintId.Value.ToString(), "--node", "test_work",
                "--justification", "   ", "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains(
            DiagnosticCodes.TestWorkJustificationRequired, diagnostics.ToString(), StringComparison.Ordinal);
        // The node must never have been started at all -- not just left recoverable.
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes["test_work"].State);
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

    private static SurfaceText Text(ILocalizationCatalog catalog) =>
        new(catalog, CultureInfo.CurrentUICulture);
}
