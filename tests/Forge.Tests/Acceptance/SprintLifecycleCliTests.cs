using System.CommandLine;
using System.Globalization;
using Forge.Application;
using Forge.Cli;
using Forge.Configuration;
using Forge.Domain;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.AcceptanceTests;

/// <summary>Stage 11, P11.56-P11.66 (second slice): `forge sprint create|run|resume|cancel`, wired
/// to the four <see cref="SprintOrchestrator"/> verbs ADR 0019 named as this slice's next
/// deferred item.</summary>
public sealed class SprintLifecycleCliTests
{
    private static readonly IReadOnlyList<NodeDefinition> GateGraph = [new("gate", NodeKind.HumanGate, [])];
    private static readonly IReadOnlyList<NodeDefinition> WorkGraph = [new("a", NodeKind.Work, [])];

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SprintCreateCommandCreatesASprintFromTheCanonicalGraph()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application);

        int exitCode = await root
            .Parse(["sprint", "create", "--project-root", environment.ProjectRoot])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(Text(catalog).Resolve(MessageKeys.SprintCreated), output.ToString(), StringComparison.Ordinal);
        IReadOnlyList<SprintId> sprints = await store.ListAsync(environment.ProjectRoot, cancellationToken);
        SprintId sprintId = Assert.Single(sprints);
        Assert.Contains(sprintId.Value.ToString("D"), output.ToString(), StringComparison.Ordinal);
        SprintDefinition? definition = await store.LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.NotEmpty(definition!.Graph);
    }

    /// <summary>ADR 0057: `--title` is parsed by the real CLI pipeline and reaches the sprint's own
    /// durable, frozen definition -- not merely the command's option table.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SprintCreateCommandFreezesTheSuppliedTitle()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        StringWriter output = new(CultureInfo.InvariantCulture);
        RootCommand root = CliApplication.CreateRootCommand(Text(new ResourceLocalizationCatalog()), output, environment.Application);

        int exitCode = await root
            .Parse(["sprint", "create", "--project-root", environment.ProjectRoot, "--title", "Close the parity gap"])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        SprintId sprintId = Assert.Single(await store.ListAsync(environment.ProjectRoot, cancellationToken));
        SprintDefinition? definition =
            await store.LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Equal("Close the parity gap", definition!.Title);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SprintRunCommandAdvancesOneLegalHopPerCall()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: WorkGraph), cancellationToken)).SprintId!;
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application);
        string[] args =
        [
            "sprint", "run", "--sprint", sprintId.Value.ToString(), "--project-root", environment.ProjectRoot,
        ];

        int firstExitCode = await root.Parse(args).InvokeAsync(new InvocationConfiguration(), cancellationToken);
        Assert.Equal(0, firstExitCode);
        Assert.Contains("ready", output.ToString(), StringComparison.Ordinal);
        SprintSnapshot afterFirst = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Ready, afterFirst.State);

        StringWriter secondOutput = new(CultureInfo.InvariantCulture);
        RootCommand secondRoot = CliApplication.CreateRootCommand(Text(catalog), secondOutput, environment.Application);
        int secondExitCode =
            await secondRoot.Parse(args).InvokeAsync(new InvocationConfiguration(), cancellationToken);
        Assert.Equal(0, secondExitCode);
        Assert.Contains("running", secondOutput.ToString(), StringComparison.Ordinal);
        SprintSnapshot afterSecond = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Running, afterSecond.State);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SprintResumeCommandUnblocksASprintBlockedByARejectedGate()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: GateGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        NodeSnapshot gate = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];
        await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", false, gate.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate), cancellationToken);
        Assert.Equal(SprintState.Blocked, (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!.State);
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application);

        int exitCode = await root
            .Parse([
                "sprint", "resume", "--sprint", sprintId.Value.ToString(), "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(Text(catalog).Resolve(MessageKeys.SprintResumed), output.ToString(), StringComparison.Ordinal);
        SprintSnapshot resumed = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Ready, resumed.State);
        // Known limitation, documented in ADR 0020's "Deliberately deferred": `resume` only un-blocks
        // the *sprint*, not the rejected gate node itself -- nothing in this slice exposes
        // `SprintScheduler.RetryNodeAsync`, so the gate stays `Failed` and a subsequent `run` cannot
        // make further progress. Encoded here rather than left implied.
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Failed, state.Nodes["gate"].State);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SprintCancelCommandCancelsAConfirmedSprint()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: GateGraph), cancellationToken)).SprintId!;
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application);

        int exitCode = await root
            .Parse([
                "sprint", "cancel", "--sprint", sprintId.Value.ToString(), "--yes", "--project-root",
                environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(Text(catalog).Resolve(MessageKeys.SprintCancelled), output.ToString(), StringComparison.Ordinal);
        SprintSnapshot cancelled = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Cancelled, cancelled.State);
    }

    /// <summary>ADR 0019: cancellation is an ordinary destructive mutation, not one of the
    /// human-only capabilities -- unlike <c>forge gate approve|reject</c>/<c>forge attempt
    /// supersede</c>, confirmation here falls back to `interaction.confirm_destructive`.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SprintCancelCommandRequiresConfirmationByDefault()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: GateGraph), cancellationToken)).SprintId!;
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root =
            CliApplication.CreateRootCommand(Text(catalog), output, environment.Application, diagnostics);

        int exitCode = await root
            .Parse([
                "sprint", "cancel", "--sprint", sprintId.Value.ToString(), "--project-root",
                environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(DiagnosticCodes.ConfirmationRequired, diagnostics.ToString(), StringComparison.Ordinal);
        SprintSnapshot untouched = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.NotEqual(SprintState.Cancelled, untouched.State);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SprintCancelCommandBypassesConfirmationWhenConfirmDestructiveIsDisabled()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: GateGraph), cancellationToken)).SprintId!;
        ConfigurationWriteResult configured = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User, environment.ProjectRoot, "interaction.confirm_destructive", "false",
            cancellationToken);
        Assert.True(configured.Succeeded);
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application);

        int exitCode = await root
            .Parse([
                "sprint", "cancel", "--sprint", sprintId.Value.ToString(), "--project-root",
                environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        SprintSnapshot cancelled = (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Cancelled, cancelled.State);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SprintRunCommandReportsSprintNotFoundForAnUnknownId()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root =
            CliApplication.CreateRootCommand(Text(catalog), output, environment.Application, diagnostics);

        int exitCode = await root
            .Parse([
                "sprint", "run", "--sprint", Guid.NewGuid().ToString(), "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(DiagnosticCodes.SprintNotFound, diagnostics.ToString(), StringComparison.Ordinal);
    }

    /// <summary>ADR 0005: every `.forge/` mutation routes through the project's Host once one is
    /// reachable -- proves all four verbs actually call through the resolved <see cref="IForgeMutations"/>
    /// rather than some other path, matching the same style of proof the gate/attempt commands have.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task AllFourSprintCommandsRouteThroughTheResolvedMutations()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        FakeForgeMutations mutations = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            resolveMutations: (_, _) => Task.FromResult<IForgeMutations>(mutations));
        string sprintId = Guid.NewGuid().ToString();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(
            0,
            await root.Parse(["sprint", "create", "--project-root", environment.ProjectRoot])
                .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        Assert.Equal(
            0,
            await root.Parse(["sprint", "run", "--sprint", sprintId, "--project-root", environment.ProjectRoot])
                .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        Assert.Equal(
            0,
            await root.Parse(["sprint", "resume", "--sprint", sprintId, "--project-root", environment.ProjectRoot])
                .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        Assert.Equal(
            0,
            await root
                .Parse([
                    "sprint", "cancel", "--sprint", sprintId, "--yes", "--project-root", environment.ProjectRoot,
                ])
                .InvokeAsync(new InvocationConfiguration(), cancellationToken));

        Assert.Equal(1, mutations.CreateSprintCalls);
        Assert.Equal(1, mutations.RunSprintCalls);
        Assert.Equal(1, mutations.ResumeSprintCalls);
        Assert.Equal(1, mutations.CancelSprintCalls);
        Assert.True(mutations.LastCancelSprintConfirmed);
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
