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

    /// <summary>ADR 0026's `integration.skill` install/remove verbs -- same routing property as
    /// <see cref="RecoverAsyncRoutesThroughTheResolvedMutations"/>, and the same shared
    /// <see cref="FakeForgeMutations"/> double the CLI's own
    /// `IntegrationSkillWriteVerbsRouteThroughTheResolvedMutations` test uses.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Unit")]
    public async Task IntegrationInstallAsyncRoutesThroughTheResolvedMutationsAndForwardsConfirmed(bool confirmed)
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(), environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.InstallIntegrationAsync(
            environment.ProjectRoot, confirmed, TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.InstallIntegrationCalls);
        Assert.Equal(0, mutations.RemoveIntegrationCalls);
        Assert.Equal(confirmed, mutations.LastIntegrationConfirmed);
    }

    /// <summary>Same shape as <see cref="IntegrationInstallAsyncRoutesThroughTheResolvedMutationsAndForwardsConfirmed"/>
    /// for the remove verb.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Unit")]
    public async Task IntegrationRemoveAsyncRoutesThroughTheResolvedMutationsAndForwardsConfirmed(bool confirmed)
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(), environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.RemoveIntegrationAsync(
            environment.ProjectRoot, confirmed, TestContext.Current.CancellationToken);

        Assert.Equal(0, mutations.InstallIntegrationCalls);
        Assert.Equal(1, mutations.RemoveIntegrationCalls);
        Assert.Equal(confirmed, mutations.LastIntegrationConfirmed);
    }

    /// <summary>ADR 0011/0026: a plain read must never route through a Host connection, matching
    /// the CLI's own `IntegrationSkillGenerateNeverRoutesThroughTheResolvedMutations` test.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateIntegrationPreviewAsyncNeverRoutesThroughTheResolvedMutations()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(), environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel
            .GenerateIntegrationPreviewAsync(environment.ProjectRoot, TestContext.Current.CancellationToken);

        Assert.Equal(0, mutations.InstallIntegrationCalls);
        Assert.Equal(0, mutations.RemoveIntegrationCalls);
        Assert.Contains(Text().Resolve(MessageKeys.IntegrationTitle), message, StringComparison.Ordinal);
    }

    /// <summary>ADR 0027's `sprint.manage` capability -- the `create` verb. Not confirmable, no
    /// sprint id to resolve, matching the CLI's own `IntegrationSkillGenerateNeverRoutesThroughTheResolvedMutations`-style
    /// routing coverage.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateSprintAsyncRoutesThroughTheResolvedMutations()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(), environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.CreateSprintAsync(environment.ProjectRoot, TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.CreateSprintCalls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunSprintAsyncRoutesThroughTheResolvedMutations()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(), environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.RunSprintAsync(
            environment.ProjectRoot, Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.RunSprintCalls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResumeSprintAsyncRoutesThroughTheResolvedMutations()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(), environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.ResumeSprintAsync(
            environment.ProjectRoot, Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.ResumeSprintCalls);
    }

    /// <summary>ADR 0027's `sprint.manage` `cancel` verb: ordinarily bypassable (matching
    /// <see cref="IntegrationInstallAsyncRoutesThroughTheResolvedMutationsAndForwardsConfirmed"/>'s
    /// own shape), not the human-only gate/supersede pair's never-bypassed one.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Unit")]
    public async Task CancelSprintAsyncRoutesThroughTheResolvedMutationsAndForwardsConfirmed(bool confirmed)
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(), environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.CancelSprintAsync(
            environment.ProjectRoot, Guid.NewGuid().ToString(), confirmed, TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.CancelSprintCalls);
        Assert.Equal(confirmed, mutations.LastCancelSprintConfirmed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancelSprintAsyncReportsSprintNotFoundForAnUnparsableSprintIdWithoutCallingMutations()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(), environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.CancelSprintAsync(
            environment.ProjectRoot, "not-a-guid", true, TestContext.Current.CancellationToken);

        Assert.Equal(0, mutations.CancelSprintCalls);
        Assert.Contains(DiagnosticCodes.SprintNotFound, message, StringComparison.Ordinal);
    }

    /// <summary>Regression pattern established by round 2 review of `attempt.supersede`
    /// (<see cref="SupersedeAttemptAsyncWithABlankSprintIdAndMultipleNonTerminalSprintsReportsAmbiguity"/>):
    /// a blank-sprint-id-with-multiple-non-terminal-sprints branch must use ITS OWN capability's
    /// ambiguity message, not another capability's. Applied here proactively rather than waiting
    /// for a review round to catch a copy-pasted key.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CancelSprintAsyncWithABlankSprintIdAndMultipleNonTerminalSprintsReportsAmbiguity()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await orchestrator.CreateSprintAsync(new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        await orchestrator.CreateSprintAsync(new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(), environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.CancelSprintAsync(
            environment.ProjectRoot, null, true, cancellationToken);

        Assert.Equal(0, mutations.CancelSprintCalls);
        Assert.Equal(Text().Resolve(MessageKeys.SprintManageSprintAmbiguous), message);
        Assert.NotEqual(Text().Resolve(MessageKeys.GateSprintAmbiguous), message);
        Assert.NotEqual(Text().Resolve(MessageKeys.AttemptSupersedeSprintAmbiguous), message);
    }

    /// <summary>Round 1 review of PR #67 found <see cref="CancelSprintAsyncWithABlankSprintIdAndMultipleNonTerminalSprintsReportsAmbiguity"/>
    /// only proves the ambiguity branch inside `CancelSprintAsync`'s own copy of the resolution
    /// logic -- `RunSprintAsync`/`ResumeSprintAsync` share a SEPARATE copy inside the private
    /// `TransitionSprintAsync` helper, and swapping ITS message to `GateSprintAmbiguous` left the
    /// full suite green. `run` stands in for both, since `resume` shares the identical helper
    /// call.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunSprintAsyncWithABlankSprintIdAndMultipleNonTerminalSprintsReportsAmbiguity()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await orchestrator.CreateSprintAsync(new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        await orchestrator.CreateSprintAsync(new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(), environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.RunSprintAsync(environment.ProjectRoot, null, cancellationToken);

        Assert.Equal(0, mutations.RunSprintCalls);
        Assert.Equal(Text().Resolve(MessageKeys.SprintManageSprintAmbiguous), message);
        Assert.NotEqual(Text().Resolve(MessageKeys.GateSprintAmbiguous), message);
        Assert.NotEqual(Text().Resolve(MessageKeys.AttemptSupersedeSprintAmbiguous), message);
    }

    /// <summary>Round 2 review of PR #67 found the round-1 fix above only covered the
    /// `Ambiguous == true` half of `TransitionSprintAsync`'s duplicated resolution block -- the
    /// `false` half (unparsable/not-found) was still proven only via `CancelSprintAsync`'s own
    /// separate copy, the exact "one of two call sites" shape round 1 itself named, one branch
    /// lower.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunSprintAsyncReportsSprintNotFoundForAnUnparsableSprintIdWithoutCallingMutations()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(), environment.Application, (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.RunSprintAsync(
            environment.ProjectRoot, "not-a-guid", TestContext.Current.CancellationToken);

        Assert.Equal(0, mutations.RunSprintCalls);
        Assert.Contains(DiagnosticCodes.SprintNotFound, message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SprintCancelPromptNamesTheSprint()
    {
        using TestEnvironment environment = new();
        SurfaceText text = Text();
        MainPageViewModel viewModel = new(text, environment.Application);
        string sprintId = Guid.NewGuid().ToString();

        string prompt = viewModel.SprintCancelPrompt(sprintId);

        Assert.Contains(sprintId, prompt, StringComparison.Ordinal);
    }

    /// <summary>Round 2 review of PR #67 found the blank-sprint placeholder branch had no test,
    /// unlike both prompts this one claims to mirror (<see cref="MainPageViewModel.GatePrompt"/>/
    /// <see cref="MainPageViewModel.AttemptSupersedePrompt"/> each have one) -- and a blank
    /// `SprintIdEntry` is the page's own default state, so this is the dialog text users see most
    /// often.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void SprintCancelPromptRendersThePlaceholderForABlankSprintId()
    {
        using TestEnvironment environment = new();
        SurfaceText text = Text();
        MainPageViewModel viewModel = new(text, environment.Application);

        string prompt = viewModel.SprintCancelPrompt(null);

        string expected = string.Create(
            CultureInfo.InvariantCulture,
            $"{text.Resolve(MessageKeys.SprintIdLabel)} {text.Resolve(MessageKeys.GateActiveSprintPlaceholder)}");
        Assert.Equal(expected, prompt);
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
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new(nodeId, NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.ResolveGateAsync(
            environment.ProjectRoot, "   ", null, true, true, cancellationToken);

        Assert.Equal(1, mutations.ResolveGateCalls);
        Assert.Equal(Text().Resolve(MessageKeys.GateResolved), message);
        // Not just "some sprint" -- the specific one the blank entry should resolve to.
        Assert.Equal(sprintId.Value, mutations.LastGateSprintId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveGateAsyncWithABlankSprintIdAndNoSprintsReportsSprintNotFound()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.ResolveGateAsync(
            environment.ProjectRoot, null, null, true, true, TestContext.Current.CancellationToken);

        Assert.Equal(0, mutations.ResolveGateCalls);
        Assert.Contains(DiagnosticCodes.SprintNotFound, message, StringComparison.Ordinal);
    }

    /// <summary>Regression: `StatusAdvisor.DetermineActiveSprint` returns `null` both when no sprint
    /// is non-terminal and when more than one is (ADR 0005: "Forge never silently chooses among
    /// multiple candidates"). A blank sprint id must not report the same "not found" message for
    /// both -- the sprints exist and are visible in the tree, so the user needs to be told to enter
    /// an id, not that nothing was found.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveGateAsyncWithABlankSprintIdAndMultipleNonTerminalSprintsReportsAmbiguity()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await orchestrator.CreateSprintAsync(new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        await orchestrator.CreateSprintAsync(new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.ResolveGateAsync(
            environment.ProjectRoot, null, null, true, true, cancellationToken);

        Assert.Equal(0, mutations.ResolveGateCalls);
        Assert.Equal(Text().Resolve(MessageKeys.GateSprintAmbiguous), message);
        Assert.DoesNotContain(DiagnosticCodes.SprintNotFound, message, StringComparison.Ordinal);
    }

    /// <summary>Regression: the ambiguity check must count only *non-terminal* sprints. Without that
    /// filter, more than one sprint of any state (including two cancelled ones, where the correct
    /// answer is "genuinely none in progress") would be wrongly reported as ambiguous.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveGateAsyncWithABlankSprintIdAndOnlyTerminalSprintsReportsSprintNotFound()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId first = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        SprintId second = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;
        await CancelAsync(orchestrator, environment.ProjectRoot, first, cancellationToken);
        await CancelAsync(orchestrator, environment.ProjectRoot, second, cancellationToken);
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.ResolveGateAsync(
            environment.ProjectRoot, null, null, true, true, cancellationToken);

        Assert.Equal(0, mutations.ResolveGateCalls);
        Assert.Contains(DiagnosticCodes.SprintNotFound, message, StringComparison.Ordinal);
        Assert.NotEqual(Text().Resolve(MessageKeys.GateSprintAmbiguous), message);
    }

    private static async Task CancelAsync(
        SprintOrchestrator orchestrator, string projectRoot, SprintId sprintId, CancellationToken cancellationToken)
    {
        SprintSnapshot sprint = (await orchestrator.GetSprintAsync(projectRoot, sprintId, cancellationToken))!;
        Assert.True((await orchestrator.CancelSprintAsync(
            new(projectRoot, sprintId, sprint.Version, SprintOrchestrator.CancelSprintKey(sprint)),
            cancellationToken)).Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GatePromptNamesTheBlankSprintPlaceholderAndTheDefaultNode()
    {
        using TestEnvironment environment = new();
        SurfaceText text = Text();
        MainPageViewModel viewModel = new(text, environment.Application);

        string prompt = viewModel.GatePrompt(null, null);

        Assert.Contains(text.Resolve(MessageKeys.GateActiveSprintPlaceholder), prompt, StringComparison.Ordinal);
        Assert.Contains(ImplementationCriticalGraphBuilder.HumanApprovalNodeId, prompt, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GatePromptNamesTheSuppliedSprintAndNodeInsteadOfTheDefaults()
    {
        using TestEnvironment environment = new();
        SurfaceText text = Text();
        MainPageViewModel viewModel = new(text, environment.Application);
        string sprintId = Guid.NewGuid().ToString();

        string prompt = viewModel.GatePrompt(sprintId, "custom-node");

        // Not a `DoesNotContain` for the placeholder/default node id: both `SprintIdLabel` and
        // `GateNodeIdLabel` themselves already mention "active sprint"/`human_approval` as part of
        // their own static wording ("Sprint id (empty: active sprint):"), regardless of the value
        // that follows -- so that assertion would be locale-dependent noise, not a real check. The
        // supplied values actually appearing is what proves they reached the prompt.
        Assert.Contains(sprintId, prompt, StringComparison.Ordinal);
        Assert.Contains("custom-node", prompt, StringComparison.Ordinal);
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncRoutesThroughTheResolvedMutationsWithForwardedValues()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));
        Guid sprintId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        // Deliberately carries surrounding whitespace: the instruction must reach `mutations`
        // verbatim, matching the CLI's own file-or-stdin source forwarded as-is. A `.Trim()` slipped
        // into the production code would still pass every other assertion here.
        const string instruction = "  Try a different approach.  ";

        string message = await viewModel.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId.ToString(), attemptId.ToString(), instruction, true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.SupersedeAttemptCalls);
        Assert.Equal(Text().Resolve(MessageKeys.AttemptSuperseded), message);
        Assert.Equal(sprintId, mutations.LastSupersedeSprintId);
        Assert.Equal(attemptId, mutations.LastSupersedeAttemptId);
        Assert.Equal(instruction, mutations.LastSupersedeInstruction);
        Assert.True(mutations.LastSupersedeConfirmed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncReportsSprintNotFoundForAnUnparsableSprintIdWithoutCallingMutations()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.SupersedeAttemptAsync(
            environment.ProjectRoot, "not-a-guid", Guid.NewGuid().ToString(), "instruction", true,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, mutations.SupersedeAttemptCalls);
        Assert.Contains(DiagnosticCodes.SprintNotFound, message, StringComparison.Ordinal);
    }

    /// <summary>Regression: round 2 review found this branch returned <see
    /// cref="MessageKeys.GateSprintAmbiguous"/> -- text that explicitly says "resolve its gate" --
    /// on the normal blank-sprint-id-with-multiple-non-terminal-sprints path for `attempt.supersede`,
    /// a capability with no gate at all. Fixed with a dedicated <see
    /// cref="MessageKeys.AttemptSupersedeSprintAmbiguous"/> key.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncWithABlankSprintIdAndMultipleNonTerminalSprintsReportsAmbiguity()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await orchestrator.CreateSprintAsync(new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        await orchestrator.CreateSprintAsync(new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.SupersedeAttemptAsync(
            environment.ProjectRoot, null, Guid.NewGuid().ToString(), "instruction", true, cancellationToken);

        Assert.Equal(0, mutations.SupersedeAttemptCalls);
        Assert.Equal(Text().Resolve(MessageKeys.AttemptSupersedeSprintAmbiguous), message);
        Assert.NotEqual(Text().Resolve(MessageKeys.GateSprintAmbiguous), message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncReportsAConflictForAnUnparsableAttemptIdWithoutCallingMutations()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.SupersedeAttemptAsync(
            environment.ProjectRoot, Guid.NewGuid().ToString(), "not-a-guid", "instruction", true,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, mutations.SupersedeAttemptCalls);
        Assert.Contains(DiagnosticCodes.WorkflowEventConflict, message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SupersedeAttemptAsyncReportsAnEmptyInstructionWithoutCallingMutations(string? instruction)
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        string message = await viewModel.SupersedeAttemptAsync(
            environment.ProjectRoot, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), instruction, true,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, mutations.SupersedeAttemptCalls);
        Assert.Contains(DiagnosticCodes.SupersessionInstructionRequired, message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncReportsAnOverLongInstructionWithoutCallingMutations()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));
        string instruction = new('x', SprintScheduler.MaxSupersessionInstructionLength + 1);

        string message = await viewModel.SupersedeAttemptAsync(
            environment.ProjectRoot, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), instruction, true,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, mutations.SupersedeAttemptCalls);
        Assert.Contains(DiagnosticCodes.SupersessionInstructionTooLong, message, StringComparison.Ordinal);
    }

    /// <summary>Regression: the rejecting side alone (`Max + 1`) does not pin the boundary -- an
    /// off-by-one that rejects at exactly `Max` would still pass it. An instruction of exactly the
    /// maximum length must still reach `mutations`, matching `SprintScheduler.SupersedeAttemptAsync`'s
    /// own `instruction.Length > MaxSupersessionInstructionLength` check.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncAcceptsAnInstructionAtExactlyTheMaximumLength()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));
        string instruction = new('x', SprintScheduler.MaxSupersessionInstructionLength);

        string message = await viewModel.SupersedeAttemptAsync(
            environment.ProjectRoot, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), instruction, true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.SupersedeAttemptCalls);
        Assert.Equal(Text().Resolve(MessageKeys.AttemptSuperseded), message);
        Assert.Equal(instruction, mutations.LastSupersedeInstruction);
    }

    /// <summary>Regression: an instruction that is both whitespace-only and over the length bound
    /// must report the same diagnostic `forge attempt supersede` would (`_too_long`), matching
    /// `CliApplication.ReadInstructionAsync`'s own check order (bound before emptiness) exactly --
    /// not `_required`, which the reversed order this fixes would have reported instead.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncReportsTooLongNotRequiredForAWhitespaceOnlyOverLongInstruction()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));
        string instruction = new(' ', SprintScheduler.MaxSupersessionInstructionLength + 1);

        string message = await viewModel.SupersedeAttemptAsync(
            environment.ProjectRoot, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), instruction, true,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, mutations.SupersedeAttemptCalls);
        Assert.Contains(DiagnosticCodes.SupersessionInstructionTooLong, message, StringComparison.Ordinal);
        Assert.DoesNotContain(DiagnosticCodes.SupersessionInstructionRequired, message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncWithABlankSprintIdTargetsTheActiveSprint()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        FakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.SupersedeAttemptAsync(
            environment.ProjectRoot, "   ", Guid.NewGuid().ToString(), "instruction", true, cancellationToken);

        Assert.Equal(1, mutations.SupersedeAttemptCalls);
        Assert.Equal(sprintId.Value, mutations.LastSupersedeSprintId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncDisposesTheResolvedMutationsAfterTheCall()
    {
        using TestEnvironment environment = new();
        DisposableFakeForgeMutations mutations = new();
        MainPageViewModel viewModel = new(
            Text(),
            environment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await viewModel.SupersedeAttemptAsync(
            environment.ProjectRoot, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "instruction", true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.DisposeCalls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncCancelsAndLinksAReplacementThroughTheLocalFallback()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        Assert.True(started.Succeeded, $"diag={started.DiagnosticCode}");
        MainPageViewModel viewModel = new(Text(), environment.Application);

        string message = await viewModel.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId.Value.ToString(), started.AttemptId!.Value.ToString(),
            "Try a different approach.", true, cancellationToken);

        Assert.Equal(Text().Resolve(MessageKeys.AttemptSuperseded), message);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(AttemptState.Cancelled, state.Attempts[started.AttemptId!.Value.ToString("D")].State);
        AttemptSnapshot replacement =
            Assert.Single(state.Attempts.Values, candidate => candidate.Id != started.AttemptId);
        Assert.Equal(started.AttemptId, replacement.SupersedesAttemptId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SupersedeAttemptAsyncRefusesAnUnconfirmedDecisionThroughTheLocalFallbackWithoutChangingTheAttempt()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        Assert.True(started.Succeeded, $"diag={started.DiagnosticCode}");
        MainPageViewModel viewModel = new(Text(), environment.Application);

        string message = await viewModel.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId.Value.ToString(), started.AttemptId!.Value.ToString(),
            "Try a different approach.", false, cancellationToken);

        Assert.Contains(DiagnosticCodes.ConfirmationRequired, message, StringComparison.Ordinal);
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(AttemptState.Created, state.Attempts[started.AttemptId!.Value.ToString("D")].State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AttemptSupersedePromptNamesTheSprintAndAttempt()
    {
        using TestEnvironment environment = new();
        SurfaceText text = Text();
        MainPageViewModel viewModel = new(text, environment.Application);
        string sprintId = Guid.NewGuid().ToString();
        string attemptId = Guid.NewGuid().ToString();

        string prompt = viewModel.AttemptSupersedePrompt(sprintId, attemptId);

        Assert.Contains(sprintId, prompt, StringComparison.Ordinal);
        Assert.Contains(attemptId, prompt, StringComparison.Ordinal);
    }

    /// <summary>Regression: unlike a gate's node id, an attempt has no default -- a missing attempt
    /// id must render an explicit placeholder, never an empty value a confirmation dialog would show
    /// as a blank line next to its own label.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void AttemptSupersedePromptRendersAPlaceholderForAMissingAttemptId()
    {
        using TestEnvironment environment = new();
        SurfaceText text = Text();
        MainPageViewModel viewModel = new(text, environment.Application);

        string prompt = viewModel.AttemptSupersedePrompt(Guid.NewGuid().ToString(), null);

        Assert.Contains(text.Resolve(MessageKeys.AttemptIdMissingPlaceholder), prompt, StringComparison.Ordinal);
    }

    /// <summary>ADR 0025's `control.events` capability. Proves the new logic this slice adds
    /// around the already-tested <see cref="ControlEventsReader"/> (see
    /// <c>ControlEventsReaderTests</c> for the cursor/dedup mechanics themselves): the view
    /// model's own stored cursor genuinely carries forward across calls, so a second poll with
    /// nothing new renders <see cref="MessageKeys.NoEvents"/> instead of replaying the first
    /// poll's event. (CLI/Desktop rendering parity itself is proved separately, by
    /// <c>SurfaceParityTests.DesktopAndCliRenderTheSameEventsForOneSnapshot</c> -- this test's own
    /// `Contains` assertion below is not that proof.)</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task PollEventsAsyncStoresTheCursorSoASecondPollDoesNotReplayTheFirstEvent()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        MainPageViewModel viewModel = new(Text(), environment.Application);

        string first = await viewModel.PollEventsAsync(environment.ProjectRoot, cancellationToken);
        Assert.Contains(sprintId.Value.ToString("D"), first, StringComparison.Ordinal);

        string second = await viewModel.PollEventsAsync(environment.ProjectRoot, cancellationToken);
        Assert.Contains(Text().Resolve(MessageKeys.NoEvents), second, StringComparison.Ordinal);
        Assert.DoesNotContain(sprintId.Value.ToString("D"), second, StringComparison.Ordinal);
    }

    /// <summary>A stored cursor from one project must never carry over to a different one -- its
    /// watermarks describe sprints from the wrong project entirely. Two genuinely separate
    /// initialized project roots, one <see cref="ForgeApplication"/> (root-agnostic by design,
    /// matching every surface's own `--root` support), one view model instance.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task PollEventsAsyncResetsTheStoredCursorWhenTheProjectRootChanges()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        string secondRoot = Path.Combine(environment.Root, "project2");
        Directory.CreateDirectory(secondRoot);
        Assert.True((await environment.InitializeAsync(secondRoot, true, cancellationToken)).Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        MainPageViewModel viewModel = new(Text(), environment.Application);

        string firstProject = await viewModel.PollEventsAsync(environment.ProjectRoot, cancellationToken);
        Assert.Contains(sprintId.Value.ToString("D"), firstProject, StringComparison.Ordinal);

        // A different, event-free project: proves nothing, on its own, about whether the switch
        // reset the stored cursor (an uninitialized/empty project would render the same either
        // way) -- it only sets up the next assertion, which does.
        await viewModel.PollEventsAsync(secondRoot, cancellationToken);

        // Switching back: a genuine reset re-reads the first project from scratch and sees its
        // sprint-creation event again. Without a reset, the still-live first-project cursor would
        // instead render NoEvents here, since it already consumed that event on the first call.
        string backToFirstProject = await viewModel.PollEventsAsync(environment.ProjectRoot, cancellationToken);
        Assert.Contains(sprintId.Value.ToString("D"), backToFirstProject, StringComparison.Ordinal);
    }

    private static SurfaceText Text() => new(new ResourceLocalizationCatalog(), CultureInfo.CurrentUICulture);
}
