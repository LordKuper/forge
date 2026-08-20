using System.CommandLine;
using System.Globalization;
using Forge.Application;
using Forge.Cli;
using Forge.Domain;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.AcceptanceTests;

/// <summary>Stage 11, P11.56-P11.66: the human-only `workflow.confirm` capability, wired to
/// `forge confirm confirmed|not-confirmed`. Deliberately mirrors
/// <see cref="HumanGateAndSupersessionCliTests"/>'s own shape and coverage — the same ADR 0023
/// interactive-session control and mandatory, never-bypassed confirmation apply here too.</summary>
public sealed class ConfirmationCliTests
{
    private static readonly IReadOnlyList<NodeDefinition> ConfirmGraph =
        [new("confirm", NodeKind.Work, [], NodeRole.Confirmation)];

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ConfirmConfirmedCommandRecordsAConfirmedOutcomeAndSucceedsTheNode()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "confirm", "confirmed", "--sprint", sprintId.Value.ToString(), "--node", "confirm",
                "--definition-of-done", "Feature X matches its agreed definition of done.",
                "--evidence-kind", "execution", "--evidence", "Ran the full test suite locally; all green.",
                "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(Text(catalog).Resolve(MessageKeys.ConfirmRecorded), output.ToString(), StringComparison.Ordinal);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["confirm"].State);
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ConfirmationArtifact artifact = Assert.Single(
            await scheduler.GetConfirmationsAsync(environment.ProjectRoot, sprintId, cancellationToken));
        Assert.Equal(ConfirmationOutcome.Confirmed, artifact.Outcome);
        Assert.Equal(ConfirmationEvidenceKind.Execution, Assert.Single(artifact.Evidence).Kind);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ConfirmNotConfirmedCommandRecordsANotConfirmedOutcomeAndBlocksTheSprint()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "confirm", "not-confirmed", "--sprint", sprintId.Value.ToString(), "--node", "confirm",
                "--definition-of-done", "Feature X does not yet match its agreed definition of done.",
                "--evidence-kind", "inspection", "--evidence", "Acceptance criterion 2 is not met.",
                "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        // The node's own attempt still succeeds -- rendering the judgment is its whole job -- but the
        // sprint itself blocks, which is this design's actual stopping point for a human.
        Assert.Equal(0, exitCode);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["confirm"].State);
        Assert.Equal(SprintState.Blocked, state.Sprint.State);
        Assert.Equal("confirmation", state.Sprint.BlockedReason);
    }

    /// <summary>ADR 0005: "no destructive preselected decision and no agent/plugin permission to
    /// self-approve." Confirmation is required even though the subcommand itself
    /// (<c>confirmed</c> vs <c>not-confirmed</c>) is already an explicit choice.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ConfirmCommandRequiresConfirmation()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, diagnostics, isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "confirm", "confirmed", "--sprint", sprintId.Value.ToString(), "--node", "confirm",
                "--definition-of-done", "Met the DoD.", "--evidence-kind", "execution", "--evidence", "Ran it.",
                "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(DiagnosticCodes.ConfirmationRequired, diagnostics.ToString(), StringComparison.Ordinal);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes["confirm"].State);
    }

    /// <summary>ADR 0023: the same technical control every other human-only command shares. Refused
    /// before argument validation, before any mutation call, and unconditionally.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ConfirmCommandRefusesANonInteractiveSessionEvenWithYes()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmGraph), cancellationToken)).SprintId!;
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
                "confirm", "confirmed", "--sprint", sprintId.Value.ToString(), "--node", "confirm",
                "--definition-of-done", "Met the DoD.", "--evidence-kind", "execution", "--evidence", "Ran it.",
                "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(ExitCodes.Authorization, exitCode);
        Assert.Contains(DiagnosticCodes.PermissionDenied, diagnostics.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, mutations.ConfirmNodeCalls);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes["confirm"].State);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ConfirmCommandRejectsAnInvalidEvidenceKind()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmGraph), cancellationToken)).SprintId!;
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
            isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "confirm", "confirmed", "--sprint", sprintId.Value.ToString(), "--node", "confirm",
                "--definition-of-done", "Met the DoD.", "--evidence-kind", "not-a-real-kind",
                "--evidence", "Ran it.", "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains(
            DiagnosticCodes.ConfirmationEvidenceKindInvalid, diagnostics.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, mutations.ConfirmNodeCalls);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes["confirm"].State);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ConfirmCommandRejectsAWhitespaceOnlyDefinitionOfDoneBeforeStartingTheAttempt()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: ConfirmGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, diagnostics, isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "confirm", "confirmed", "--sprint", sprintId.Value.ToString(), "--node", "confirm",
                "--definition-of-done", "   ", "--evidence-kind", "execution", "--evidence", "Ran it.",
                "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains(DiagnosticCodes.ConfirmationTextRequired, diagnostics.ToString(), StringComparison.Ordinal);
        // The node must never have been started at all -- not just left recoverable.
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes["confirm"].State);
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
