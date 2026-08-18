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
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, isInteractive: () => true);

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
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, isInteractive: () => true);

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
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, diagnostics, isInteractive: () => true);

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

    /// <summary>ADR 0023: the first real technical control behind ADR 0005/0019's "human-only"
    /// requirement. Refused before argument validation, before any mutation call, and unconditionally
    /// -- `--yes` cannot substitute for an interactive session the same way it cannot substitute for
    /// a missing sprint/node id.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task GateApproveCommandRefusesANonInteractiveSessionEvenWithYes()
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
                "gate", "approve", "--sprint", sprintId.Value.ToString(), "--node", "gate", "--yes",
                "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(ExitCodes.Authorization, exitCode);
        Assert.Contains(DiagnosticCodes.PermissionDenied, diagnostics.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, mutations.ResolveGateCalls);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.AwaitingHuman, state.Nodes["gate"].State);
    }

    /// <summary>ADR 0023: covers the production default (`isInteractive` omitted entirely, unlike
    /// every other test in this file, which passes an explicit override) — every other test here
    /// only proves the *parameter* works, never that `CreateRootCommand`'s own default lambda
    /// (`() => !Console.IsOutputRedirected`) is wired to it at all. `dotnet test` redirects this
    /// process's own standard output to capture it, so the default deterministically evaluates to
    /// non-interactive under `dotnet test` (both this suite's CI and local runs), but this test does
    /// not hard-assert that: xunit v3 builds a directly runnable executable, so a developer running
    /// `Forge.Tests.exe` from an actual interactive terminal would have `Console.IsOutputRedirected`
    /// read `false` there, and a hard-coded "always refused" assertion would then be the one that's
    /// wrong, not the production code. Instead this computes the expected outcome from the SAME real
    /// property the production default consults, so the test passes under either launch environment
    /// while still failing if the default is ever replaced with a constant (e.g. `() => true`) --
    /// the exact mutation this test exists to catch.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task GateApproveCommandUsesTheRealAmbientConsoleStateByDefault()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        bool interactive = !Console.IsOutputRedirected;
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        FakeForgeMutations mutations = new();
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            diagnostics,
            resolveMutations: (_, _) => Task.FromResult<IForgeMutations>(mutations));

        int exitCode = await root
            .Parse([
                "gate", "approve", "--sprint", Guid.NewGuid().ToString(), "--node", "gate", "--yes",
                "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        if (interactive)
        {
            Assert.Equal(0, exitCode);
            Assert.Equal(1, mutations.ResolveGateCalls);
        }
        else
        {
            Assert.Equal(ExitCodes.Authorization, exitCode);
            Assert.Contains(DiagnosticCodes.PermissionDenied, diagnostics.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, mutations.ResolveGateCalls);
        }
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
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, diagnostics, isInteractive: () => true);

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
            resolveMutations: (_, _) => Task.FromResult<IForgeMutations>(mutations),
            isInteractive: () => true);

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
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, input: input, isInteractive: () => true);

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
            RootCommand root = CliApplication.CreateRootCommand(
                Text(catalog), output, environment.Application, isInteractive: () => true);

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
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, diagnostics, isInteractive: () => true);
        string missingPath = Path.Combine(Path.GetTempPath(), $"forge-missing-{Guid.NewGuid():N}.txt");

        int exitCode = await root
            .Parse([
                "attempt", "supersede", Guid.NewGuid().ToString(), "--sprint", Guid.NewGuid().ToString(),
                "--instruction-file", missingPath, "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
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
            Text(catalog), output, environment.Application, diagnostics, input: input, isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "attempt", "supersede", Guid.NewGuid().ToString(), "--sprint", Guid.NewGuid().ToString(),
                "--instruction-file", "-", "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains(
            DiagnosticCodes.SupersessionInstructionRequired, diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task AttemptSupersedeCommandRejectsAnOverLongInstructionWithTheDocumentedExitCode()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        StringReader input = new(new string('x', SprintScheduler.MaxSupersessionInstructionLength + 1));
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog), output, environment.Application, diagnostics, input: input, isInteractive: () => true);

        int exitCode = await root
            .Parse([
                "attempt", "supersede", Guid.NewGuid().ToString(), "--sprint", Guid.NewGuid().ToString(),
                "--instruction-file", "-", "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains(
            DiagnosticCodes.SupersessionInstructionTooLong, diagnostics.ToString(), StringComparison.Ordinal);
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
            Text(catalog), output, environment.Application, diagnostics, input: input, isInteractive: () => true);

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

    /// <summary>ADR 0023: same technical control as <see
    /// cref="GateApproveCommandRefusesANonInteractiveSessionEvenWithYes"/>. The instruction source
    /// is a <see cref="ThrowingTextReader"/> that fails the test if ever read, proving the refusal
    /// happens strictly before <c>ReadInstructionAsync</c> -- not merely before the mutation call.
    /// </summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task AttemptSupersedeCommandRefusesANonInteractiveSessionEvenWithYes()
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
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        FakeForgeMutations mutations = new();
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            diagnostics,
            resolveMutations: (_, _) => Task.FromResult<IForgeMutations>(mutations),
            input: new ThrowingTextReader(),
            isInteractive: () => false);

        int exitCode = await root
            .Parse([
                "attempt", "supersede", started.AttemptId!.Value.ToString(), "--sprint", sprintId.Value.ToString(),
                "--instruction-file", "-", "--yes", "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(ExitCodes.Authorization, exitCode);
        Assert.Contains(DiagnosticCodes.PermissionDenied, diagnostics.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, mutations.SupersedeAttemptCalls);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(AttemptState.Created, state.Attempts[started.AttemptId!.Value.ToString("D")].State);
    }

    /// <summary>Fails the test immediately if the instruction source is ever read, rather than
    /// silently returning empty text -- a refusal that happened to read nothing first would pass a
    /// weaker assertion, but this reader makes "never read at all" the only way to pass.
    /// <c>ReadInstructionAsync</c>'s bounded reader calls <see cref="ReadAsync(Memory{char},
    /// CancellationToken)"/> specifically, so that overload -- not just the synchronous ones -- must
    /// throw.</summary>
    private sealed class ThrowingTextReader : TextReader
    {
        public override int Read() => throw Failure();

        public override int Read(char[] buffer, int index, int count) => throw Failure();

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken) =>
            throw Failure();

        public override Task<string?> ReadLineAsync() => throw Failure();

        private static InvalidOperationException Failure() => new(
            "The instruction source must not be read when the session is refused as non-interactive.");
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
            input: input,
            isInteractive: () => true);

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
