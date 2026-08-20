using System.CommandLine;
using System.Globalization;
using Forge.Application;
using Forge.Cli;
using Forge.Domain;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.AcceptanceTests;

/// <summary>Stage 11, ADR 0036: the human-only `workflow.finalize` capability, wired to
/// `forge finalize`. Deliberately mirrors <see cref="ConfirmationCliTests"/>/<see cref="TestWorkCliTests"/>'s
/// own shape and coverage — the same ADR 0023 interactive-session control and mandatory,
/// never-bypassed confirmation apply here too.</summary>
public sealed class FinalizeSprintCliTests
{
    private static readonly IReadOnlyList<NodeDefinition> FinalizationGraph =
        [new("finalization", NodeKind.Work, [], NodeRole.Finalization)];

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task FinalizeCommandMergesAndCompletesTheSprint()
    {
        FakeRepository repository = new(defaultBranch: "main");
        using TestEnvironment environment = new(repository: repository);
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: FinalizationGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "finalize", "--sprint", sprintId.Value.ToString(), "--node", "finalization",
                "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(Text(catalog).Resolve(MessageKeys.SprintFinalized), output.ToString(), StringComparison.Ordinal);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["finalization"].State);
        Assert.Equal(SprintState.Completed, state.Sprint.State);
        Assert.Single(repository.MergeCalls);
    }

    /// <summary>ADR 0005: mandatory confirmation even though there is only one possible action.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task FinalizeCommandRequiresConfirmation()
    {
        FakeRepository repository = new(defaultBranch: "main");
        using TestEnvironment environment = new(repository: repository);
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: FinalizationGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, diagnostics, isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "finalize", "--sprint", sprintId.Value.ToString(), "--node", "finalization",
                "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(DiagnosticCodes.ConfirmationRequired, diagnostics.ToString(), StringComparison.Ordinal);
        Assert.Empty(repository.MergeCalls);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes["finalization"].State);
    }

    /// <summary>ADR 0023: the same technical control every other human-only command shares. Refused
    /// before argument validation, before any mutation call, and unconditionally.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task FinalizeCommandRefusesANonInteractiveSessionEvenWithYes()
    {
        FakeRepository repository = new(defaultBranch: "main");
        using TestEnvironment environment = new(repository: repository);
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: FinalizationGraph), cancellationToken)).SprintId!;
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
                "finalize", "--sprint", sprintId.Value.ToString(), "--node", "finalization",
                "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(ExitCodes.Authorization, exitCode);
        Assert.Contains(DiagnosticCodes.PermissionDenied, diagnostics.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, mutations.FinalizeSprintCalls);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Ready, state.Nodes["finalization"].State);
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
