using System.Globalization;
using Forge.Application;
using Forge.Compiler;
using Forge.Configuration;
using Forge.Desktop.Presentation;
using Forge.Domain;
using Forge.Localization;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class MainPageViewModelTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RefreshAsyncRendersEveryProviderIncludingARegisteredButDisabledOne()
    {
        ProviderToolchainStatus enabledOnly = new(
        [
            ProviderStatus.Ready(new ProviderId("codex"), "0.146.0") with
            {
                Authentication = ProviderAuthenticationStatus.Ready,
            },
        ]);
        using TestEnvironment environment = new(
            providers: new FakeProviderToolchainManager(enabledOnly),
            llmProviders:
            [
                new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "0.146.0"),
                new FakeLlmProvider(new ProviderId("claude_code"), ProviderState.Ready, "2.1.221"),
            ]);
        MainPageViewModel viewModel = new(Text(), environment.Application);

        MainPageSnapshot snapshot = await viewModel.RefreshAsync(null, null, TestContext.Current.CancellationToken);

        Assert.Contains(
            "codex enabled ready 0.146.0 - ready none",
            snapshot.ProvidersText,
            StringComparison.Ordinal);
        Assert.Contains(
            "claude_code disabled - - - - provider_disabled",
            snapshot.ProvidersText,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RefreshAsyncNestsAnAttemptUnderItsOwningNodeAndRendersTheSprintDetailSections()
    {
        // The Desktop counterpart of `forge tree`/`forge sprint inspect`: ADR 0005's
        // "project -> sprint -> node -> attempt" hierarchy, projected locally from the same
        // snapshot the CLI reads. Two independent nodes so an OwnerId filter that matched every
        // attempt would print the one attempt under "b" too.
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                Graph: [new("a", NodeKind.Work, []), new("b", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        MainPageViewModel viewModel = new(Text(), environment.Application);

        MainPageSnapshot snapshot = await viewModel.RefreshAsync(environment.ProjectRoot, null, cancellationToken);

        string attemptId = started.AttemptId!.Value.ToString("D", CultureInfo.InvariantCulture);
        // The attempt line must be the very next line after its own node's line, at a deeper
        // indent — a flat list would satisfy a mere "appears somewhere after" check.
        Assert.Contains(
            string.Create(
                CultureInfo.InvariantCulture,
                $"      a running{Environment.NewLine}        {attemptId} created"),
            snapshot.SprintsText,
            StringComparison.Ordinal);
        Assert.Contains("      b ready", snapshot.SprintsText, StringComparison.Ordinal);
        Assert.Equal(1, snapshot.SprintsText.Split(attemptId, StringSplitOptions.None).Length - 1);
        // The flat per-sprint sections are the second view, equivalent to `forge sprint inspect`.
        Assert.Contains(
            Text().Resolve(MessageKeys.AttemptsLabel),
            snapshot.SprintDetailsText,
            StringComparison.Ordinal);
        Assert.Contains("retry_remaining=", snapshot.SprintDetailsText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RefreshAsyncWithAnExplicitSprintIdExpandsThatSprintNotTheOthers()
    {
        // Two sprints, with the *other* one cancelled so exactly one non-terminal sprint remains
        // and DetermineActiveSprint really resolves it as active. Without that step the fixture
        // has no active sprint at all, and the test could not tell "expanded the requested sprint"
        // apart from "expanded the active one".
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId activeSprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("alpha", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        SprintId requestedSprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("beta", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        SprintSnapshot requested =
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, requestedSprintId, cancellationToken))!;
        Assert.True((await orchestrator.CancelSprintAsync(
            new(
                environment.ProjectRoot,
                requestedSprintId,
                requested.Version,
                SprintOrchestrator.CancelSprintKey(requested)),
            cancellationToken)).Succeeded);
        MainPageViewModel viewModel = new(Text(), environment.Application);

        MainPageSnapshot snapshot = await viewModel.RefreshAsync(
            environment.ProjectRoot,
            requestedSprintId.Value.ToString(),
            cancellationToken);

        // Pin the precondition itself: the `*` marker proves the other sprint really did resolve as
        // active, so this cannot silently decay back into a fixture with no active sprint at all —
        // which is what made the pre-fix version of this test unable to prove anything.
        Assert.Contains(
            string.Create(
                CultureInfo.InvariantCulture,
                $"* 1. {activeSprintId.Value.ToString("D", CultureInfo.InvariantCulture)} "),
            snapshot.SprintsText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", snapshot.SprintsText, StringComparison.Ordinal);
        Assert.Contains("      beta ", snapshot.SprintsText, StringComparison.Ordinal);
        Assert.Contains("beta", snapshot.SprintDetailsText, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("not-a-guid")]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    public async Task RefreshAsyncReportsSprintNotFoundInsteadOfFallingBackToTheActiveSprint(string sprintId)
    {
        // Same edge case `forge status --detail full`/`tree` report: an unusable --sprint value
        // must never silently resolve the active sprint's detail instead.
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId existingSprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("alpha", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        MainPageViewModel viewModel = new(Text(), environment.Application);

        MainPageSnapshot snapshot = await viewModel.RefreshAsync(
            environment.ProjectRoot,
            sprintId,
            cancellationToken);

        // Reported on the diagnostics section — the Desktop equivalent of the CLI's diagnostics
        // channel — while the sprint body stays empty instead of showing another sprint's detail.
        Assert.Contains(DiagnosticCodes.SprintNotFound, snapshot.DiagnosticsText, StringComparison.Ordinal);
        Assert.Empty(snapshot.SprintDetailsText);
        // The sprint list itself still renders; only the expansion is withheld — the active
        // sprint's own node must not leak in as a substitute for the requested one.
        Assert.Contains(
            existingSprintId.Value.ToString("D", CultureInfo.InvariantCulture),
            snapshot.SprintsText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", snapshot.SprintsText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoverAsyncRoutesThroughTheResolvedMutations()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.RecoverAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.RecoverStartupCalls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoverAsyncResolvesMutationsUsingTheSuppliedProjectRoot()
    {
        // Regression coverage for the same class of bug PR #37 fixed on the CLI side: the
        // resolver must see the exact root this call was given, not one fixed elsewhere.
        using TestEnvironment environment = new();
        string otherRoot = Path.Combine(Path.GetTempPath(), $"forge-other-{Guid.NewGuid():N}");
        FakeForgeMutations mutations = new();
        string? capturedRoot = "unset";
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (root, _) =>
            {
                capturedRoot = root;
                return Task.FromResult<IForgeMutations>(mutations);
            });

        await viewModel.RecoverAsync(otherRoot, true, TestContext.Current.CancellationToken);

        Assert.Equal(otherRoot, capturedRoot);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetConfigurationAsyncRoutesProjectScopeThroughTheResolvedMutations()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "artifacts.language.user_facing",
            "ru",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.SetConfigurationCalls);
        Assert.Equal(ConfigurationScope.Project, mutations.LastScope);
        // Never actually written locally — the fake never touches durable state, and a real
        // ForgeApplication call would have overwritten it to "ru" (proving the write really left
        // this view model instead of landing here).
        ConfigurationView project = await environment.Application.GetProjectConfigurationAsync(
            environment.ProjectRoot,
            TestContext.Current.CancellationToken);
        EffectiveConfigurationValue value = Assert.Single(
            project.Values,
            item => item.Key == "artifacts.language.user_facing");
        Assert.Equal("\"en\"", value.Value.GetRawText());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetConfigurationAsyncNeverRoutesUserScopeThroughTheResolvedMutations()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "interaction.confirm_destructive",
            "false",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, mutations.SetConfigurationCalls);
        ConfigurationView user = await environment.Application.GetUserConfigurationAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains(
            user.Values,
            value => value.Key == "interaction.confirm_destructive" && value.Value.GetBoolean() == false);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoverAsyncReturnsTheSameMessageOnTheLocalFallbackPathAsBeforeThisChange()
    {
        // No resolver supplied — the default fallback routes straight to the local
        // ForgeApplication, exactly as it always did. The Host-routing addition must not change
        // this path's returned message.
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        MainPageViewModel viewModel = new(Text(), environment.Application);

        string message = await viewModel
            .RecoverAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);

        Assert.Equal(Text().Resolve(MessageKeys.RecoveryNotNeeded), message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetConfigurationAsyncReturnsTheSameMessageOnTheLocalFallbackPathAsBeforeThisChange()
    {
        using TestEnvironment environment = new();
        MainPageViewModel viewModel = new(Text(), environment.Application);

        string message = await viewModel.SetConfigurationAsync(
            ConfigurationScope.User,
            null,
            "interaction.confirm_destructive",
            "false",
            TestContext.Current.CancellationToken);

        Assert.Equal(Text().Resolve(MessageKeys.ConfigurationUpdated), message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RecoverAsyncDisposesTheResolvedMutationsAfterTheCall()
    {
        using TestEnvironment environment = new();
        DisposableFakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.RecoverAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.DisposeCalls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetConfigurationAsyncDisposesTheResolvedMutationsAfterTheCall()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        DisposableFakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "artifacts.language.user_facing",
            "ru",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.DisposeCalls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveGateAsyncRoutesThroughTheResolvedMutationsAndDefaultsTheNodeId()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.ResolveGateAsync(
            environment.ProjectRoot, Guid.NewGuid().ToString(), null, true, true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.ResolveGateCalls);
        Assert.Equal(Text().Resolve(MessageKeys.GateResolved), message);
        // Forwarded, not merely called: a hardcoded `true`/`true`/canonical-node-id on the ViewModel
        // side would still reach this point without these three assertions.
        Assert.True(mutations.LastGateApproved);
        Assert.True(mutations.LastGateConfirmed);
        Assert.Equal(ImplementationCriticalGraphBuilder.HumanApprovalNodeId, mutations.LastGateNodeId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveGateAsyncForwardsARejectedDecision()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.ResolveGateAsync(
            environment.ProjectRoot, Guid.NewGuid().ToString(), null, false, true,
            TestContext.Current.CancellationToken);

        Assert.False(mutations.LastGateApproved);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveGateAsyncForwardsAnUnconfirmedDecision()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.ResolveGateAsync(
            environment.ProjectRoot, Guid.NewGuid().ToString(), null, true, false,
            TestContext.Current.CancellationToken);

        Assert.False(mutations.LastGateConfirmed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveGateAsyncReportsSprintNotFoundForAnUnparsableSprintIdWithoutCallingMutations()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.ResolveGateAsync(
            environment.ProjectRoot, "not-a-guid", null, true, true, TestContext.Current.CancellationToken);

        Assert.Equal(0, mutations.ResolveGateCalls);
        Assert.Contains(DiagnosticCodes.SprintNotFound, message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveGateAsyncWithABlankSprintIdTargetsTheActiveSprint()
    {
        // Regression: the page's default state (SprintIdEntry blank, active sprint's tree already
        // rendered with its awaiting_human node visible) must not be exactly the state in which
        // approve/reject cannot work -- matching RefreshAsync's own "blank means active sprint" rule.
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string nodeId = ImplementationCriticalGraphBuilder.HumanApprovalNodeId;
        await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new(nodeId, NodeKind.HumanGate, [])]),
            cancellationToken);
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.ResolveGateAsync(
            environment.ProjectRoot, "   ", null, true, true, cancellationToken);

        Assert.Equal(1, mutations.ResolveGateCalls);
        Assert.Equal(Text().Resolve(MessageKeys.GateResolved), message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveGateAsyncDisposesTheResolvedMutationsAfterTheCall()
    {
        using TestEnvironment environment = new();
        DisposableFakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.ResolveGateAsync(
            environment.ProjectRoot, Guid.NewGuid().ToString(), null, true, true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.DisposeCalls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveGateAsyncResolvesARealAwaitingHumanGateThroughTheLocalFallback()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // Named after the canonical human-approval node id so the `nodeId: null` call below
        // genuinely exercises the default-substitution path, matching `forge gate approve|reject`
        // with no `--node`.
        string nodeId = ImplementationCriticalGraphBuilder.HumanApprovalNodeId;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new(nodeId, NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        MainPageViewModel viewModel = new(Text(), environment.Application);

        string message = await viewModel.ResolveGateAsync(
            environment.ProjectRoot, sprintId.Value.ToString(), null, true, true, cancellationToken);

        Assert.Equal(Text().Resolve(MessageKeys.GateResolved), message);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes[nodeId].State);
    }

    /// <summary>Regression: nothing in the earlier local-fallback test above distinguishes approve
    /// from reject -- a `Reject` button that silently approved the gate would still pass it. This is
    /// its mirror: the durable node state after a rejection must actually be `Failed`, not
    /// `Succeeded`.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveGateAsyncRejectsARealAwaitingHumanGateThroughTheLocalFallback()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string nodeId = ImplementationCriticalGraphBuilder.HumanApprovalNodeId;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new(nodeId, NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        MainPageViewModel viewModel = new(Text(), environment.Application);

        string message = await viewModel.ResolveGateAsync(
            environment.ProjectRoot, sprintId.Value.ToString(), null, false, true, cancellationToken);

        Assert.Equal(Text().Resolve(MessageKeys.GateResolved), message);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Failed, state.Nodes[nodeId].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveGateAsyncRefusesAnUnconfirmedDecisionThroughTheLocalFallbackWithoutChangingTheNode()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string nodeId = ImplementationCriticalGraphBuilder.HumanApprovalNodeId;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new(nodeId, NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        MainPageViewModel viewModel = new(Text(), environment.Application);

        string message = await viewModel.ResolveGateAsync(
            environment.ProjectRoot, sprintId.Value.ToString(), null, true, false, cancellationToken);

        Assert.Contains(DiagnosticCodes.ConfirmationRequired, message, StringComparison.Ordinal);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.AwaitingHuman, state.Nodes[nodeId].State);
    }

    private static SurfaceText Text() => new(new ResourceLocalizationCatalog(), CultureInfo.CurrentUICulture);
}
