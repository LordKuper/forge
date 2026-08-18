using System.CommandLine;
using System.Globalization;
using Forge.Application;
using Forge.Cli;
using Forge.Configuration;
using Forge.Domain;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.AcceptanceTests;

/// <summary>Stage 11, P11.56-P11.66 (first slice): ADR 0018's human-only `workflow.review` and
/// `attempt.supersede` capabilities, wired to `forge gate approve|reject` and
/// `forge attempt supersede`.</summary>
public sealed class HumanGateAndSupersessionCliTests
{
    private static readonly IReadOnlyList<NodeDefinition> GateGraph = [new("gate", NodeKind.HumanGate, [])];
    private static readonly IReadOnlyList<NodeDefinition> WorkGraph = [new("a", NodeKind.Work, [])];

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task GateApproveCommandResolvesAnAwaitingHumanGateThroughForgeApplication()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: GateGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application);

        int exitCode = await root
            .Parse([
                "gate", "approve", "--sprint", sprintId.Value.ToString(), "--node", "gate", "--yes",
                "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(Text(catalog).Resolve(MessageKeys.GateResolved), output.ToString(), StringComparison.Ordinal);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["gate"].State);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task GateRejectCommandFailsTheNode()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: GateGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application);

        int exitCode = await root
            .Parse([
                "gate", "reject", "--sprint", sprintId.Value.ToString(), "--node", "gate", "--yes",
                "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Failed, state.Nodes["gate"].State);
    }

    /// <summary>ADR 0005: "no destructive preselected decision and no agent/plugin permission to
    /// self-approve." Confirmation is required even though the subcommand itself
    /// (<c>approve</c> vs <c>reject</c>) is already an explicit choice.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task GateApproveCommandRequiresConfirmation()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: GateGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root =
            CliApplication.CreateRootCommand(Text(catalog), output, environment.Application, diagnostics);

        int exitCode = await root
            .Parse([
                "gate", "approve", "--sprint", sprintId.Value.ToString(), "--node", "gate",
                "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(DiagnosticCodes.ConfirmationRequired, diagnostics.ToString(), StringComparison.Ordinal);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.AwaitingHuman, state.Nodes["gate"].State);
    }

    /// <summary>ADR 0019's central decision: unlike every other confirmable mutation on
    /// <c>IForgeMutations</c>, this command accepts no config-driven confirmation bypass — omitting
    /// <c>--yes</c> must still be refused even when <c>interaction.confirm_destructive</c> is
    /// disabled.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task GateApproveCommandRequiresConfirmationEvenWhenConfirmDestructiveIsDisabled()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        ConfigurationWriteResult configured = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User, environment.ProjectRoot, "interaction.confirm_destructive", "false",
            TestContext.Current.CancellationToken);
        Assert.True(configured.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: GateGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root =
            CliApplication.CreateRootCommand(Text(catalog), output, environment.Application, diagnostics);

        int exitCode = await root
            .Parse([
                "gate", "approve", "--sprint", sprintId.Value.ToString(), "--node", "gate",
                "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(DiagnosticCodes.ConfirmationRequired, diagnostics.ToString(), StringComparison.Ordinal);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.AwaitingHuman, state.Nodes["gate"].State);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task GateApproveCommandRoutesThroughTheResolvedMutations()
    {
        // ADR 0005: every `.forge/` mutation routes through the project's Host once one is
        // reachable — `mutations` here stands in for a real Host connection.
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

        int exitCode = await root
            .Parse([
                "gate", "approve", "--sprint", Guid.NewGuid().ToString(), "--yes",
                "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, mutations.ResolveGateCalls);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task AttemptSupersedeCommandCancelsAndCreatesALinkedReplacementReadingFromStandardInput()
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
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        Assert.True(started.Succeeded, $"diag={started.DiagnosticCode}");
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringReader input = new("Try a different approach.");
        ResourceLocalizationCatalog catalog = new();
        RootCommand root =
            CliApplication.CreateRootCommand(Text(catalog), output, environment.Application, input: input);

        int exitCode = await root
            .Parse([
                "attempt", "supersede", started.AttemptId!.Value.ToString(), "--sprint", sprintId.Value.ToString(),
                "--instruction-file", "-", "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(Text(catalog).Resolve(MessageKeys.AttemptSuperseded), output.ToString(), StringComparison.Ordinal);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        AttemptSnapshot original = state.Attempts[started.AttemptId!.Value.ToString("D")];
        Assert.Equal(AttemptState.Cancelled, original.State);
        AttemptSnapshot replacement =
            Assert.Single(state.Attempts.Values, candidate => candidate.Id != started.AttemptId);
        Assert.Equal(started.AttemptId, replacement.SupersedesAttemptId);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task AttemptSupersedeCommandReadsInstructionFromAFile()
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
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        Assert.True(started.Succeeded, $"diag={started.DiagnosticCode}");
        string instructionPath = Path.Combine(Path.GetTempPath(), $"forge-instruction-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(instructionPath, "Use a different library.", cancellationToken);
        try
        {
            StringWriter output = new(CultureInfo.InvariantCulture);
            ResourceLocalizationCatalog catalog = new();
            RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application);

            int exitCode = await root
                .Parse([
                    "attempt", "supersede", started.AttemptId!.Value.ToString(), "--sprint",
                    sprintId.Value.ToString(), "--instruction-file", instructionPath, "--yes", "--project-root",
                    environment.ProjectRoot,
                ])
                .InvokeAsync(new InvocationConfiguration(), cancellationToken);

            Assert.Equal(0, exitCode);
            SprintWorkflowState state =
                (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
            Assert.Equal(
                AttemptState.Cancelled, state.Attempts[started.AttemptId!.Value.ToString("D")].State);
        }
        finally
        {
            File.Delete(instructionPath);
        }
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task AttemptSupersedeCommandReportsAConflictForAnUnreadableInstructionFile()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root =
            CliApplication.CreateRootCommand(Text(catalog), output, environment.Application, diagnostics);
        string missingPath = Path.Combine(Path.GetTempPath(), $"forge-missing-{Guid.NewGuid():N}.txt");

        int exitCode = await root
            .Parse([
                "attempt", "supersede", Guid.NewGuid().ToString(), "--sprint", Guid.NewGuid().ToString(),
                "--instruction-file", missingPath, "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(
            DiagnosticCodes.SupersessionInstructionUnreadable, diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task AttemptSupersedeCommandRejectsAnEmptyInstruction()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        StringReader input = new("   \n  ");
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, diagnostics, input: input);

        int exitCode = await root
            .Parse([
                "attempt", "supersede", Guid.NewGuid().ToString(), "--sprint", Guid.NewGuid().ToString(),
                "--instruction-file", "-", "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(
            DiagnosticCodes.SupersessionInstructionRequired, diagnostics.ToString(), StringComparison.Ordinal);
    }

    /// <summary>ADR 0019: unlike every other confirmable mutation on <c>IForgeMutations</c>
    /// (<c>InstallIntegrationAsync</c>/<c>RemoveIntegrationAsync</c>), this command accepts no
    /// config-driven confirmation bypass — omitting <c>--yes</c> must always be refused, regardless
    /// of <c>interaction.confirm_destructive</c>.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task AttemptSupersedeCommandRequiresConfirmationEvenWhenConfirmDestructiveIsDisabled()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        ConfigurationWriteResult configured = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.User, environment.ProjectRoot, "interaction.confirm_destructive", "false",
            TestContext.Current.CancellationToken);
        Assert.True(configured.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: WorkGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        Assert.True(started.Succeeded, $"diag={started.DiagnosticCode}");
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        StringReader input = new("Try a different approach.");
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, diagnostics, input: input);

        int exitCode = await root
            .Parse([
                "attempt", "supersede", started.AttemptId!.Value.ToString(), "--sprint", sprintId.Value.ToString(),
                "--instruction-file", "-", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(DiagnosticCodes.ConfirmationRequired, diagnostics.ToString(), StringComparison.Ordinal);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(AttemptState.Created, state.Attempts[started.AttemptId!.Value.ToString("D")].State);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task AttemptSupersedeCommandRoutesThroughTheResolvedMutations()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        FakeForgeMutations mutations = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringReader input = new("Try a different approach.");
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            resolveMutations: (_, _) => Task.FromResult<IForgeMutations>(mutations),
            input: input);

        int exitCode = await root
            .Parse([
                "attempt", "supersede", Guid.NewGuid().ToString(), "--sprint", Guid.NewGuid().ToString(),
                "--instruction-file", "-", "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, mutations.SupersedeAttemptCalls);
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
