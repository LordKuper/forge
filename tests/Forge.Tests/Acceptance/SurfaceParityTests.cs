using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Application;
using Forge.Cli;
using Forge.Compiler;
using Forge.Desktop.Presentation;
using Forge.Domain;
using Forge.Localization;
using Forge.Presentation;
using Forge.Providers;
using Forge.Tests.Support;
using Forge.UnitTests;

namespace Forge.AcceptanceTests;

public sealed class SurfaceParityTests
{
    /// <summary>The Desktop control that exposes each implemented capability.</summary>
    private static readonly Dictionary<string, string[]> DesktopControls = new(StringComparer.Ordinal)
    {
        [CapabilityIds.ProjectSnapshot] =
        [
            "StartupChecksLabel",
            "StatusLabel",
            "ProjectStateLabel",
            "SuggestedActionsLabel",
            "SprintsLabel",
            "SprintDetailsLabel",
            "SprintIdEntry",
        ],
        [CapabilityIds.ProjectInitialize] = ["InitializeButton", "ProjectRootEntry"],
        [CapabilityIds.ConfigurationManage] =
            ["ConfigurationScopePicker", "ConfigurationKeyEntry", "ConfigurationSetButton"],
        [CapabilityIds.ProviderHealth] = ["ProvidersLabel"],
        [CapabilityIds.WorkflowReview] =
            ["SprintIdEntry", "GateNodeIdEntry", "GateApproveButton", "GateRejectButton", "GateResultLabel"],
        [CapabilityIds.AttemptSupersede] =
        [
            "SprintIdEntry",
            "AttemptIdEntry",
            "AttemptInstructionEntry",
            "AttemptSupersedeButton",
            "AttemptSupersedeResultLabel",
        ],
        [CapabilityIds.ControlEvents] = ["EventsPollButton", "EventsLabel"],
        [CapabilityIds.IntegrationSkill] =
        [
            "IntegrationGenerateButton",
            "IntegrationLabel",
            "IntegrationInstallButton",
            "IntegrationRemoveButton",
            "IntegrationWriteResultLabel",
        ],
        [CapabilityIds.SprintManage] =
        [
            "SprintIdEntry",
            "SprintCreateButton",
            "SprintRunButton",
            "SprintResumeButton",
            "SprintCancelButton",
            "SprintManageResultLabel",
        ],
    };

    [Fact]
    [Trait("Category", "Acceptance")]
    public void CliExposesEveryDocumentedCapabilityCommand()
    {
        using TestEnvironment environment = new();
        RootCommand root = CliApplication.CreateRootCommand(
            new SurfaceText(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture),
            new StringWriter(CultureInfo.InvariantCulture),
            environment.Application);

        Assert.All(
            CapabilityIds.Implemented,
            id =>
            {
                string[] tokens = DocumentedCli(id);
                Command command = Assert.Single(
                    root.Subcommands,
                    subcommand => subcommand.Name == tokens[0]);
                // Every literal token after tokens[0] (neither an option nor `<...>`-shaped) must
                // itself be a subcommand at its documented depth -- e.g. "supersede" in "attempt
                // supersede <attempt-id> --instruction-file <path|->". Without this, renaming a CLI
                // subcommand would leave this test green as long as some *option* with the matching
                // name still existed anywhere in the tree (HasOption below searches recursively).
                Command current = command;
                foreach (string token in tokens.Skip(1))
                {
                    if (token.StartsWith("--", StringComparison.Ordinal) || token.StartsWith('<'))
                    {
                        break;
                    }

                    current = Assert.Single(current.Subcommands, subcommand => subcommand.Name == token);
                }

                foreach (string option in tokens.Where(token => token.StartsWith("--", StringComparison.Ordinal)))
                {
                    Assert.True(
                        HasOption(current, option),
                        $"'{current.Name}' does not expose '{option}'.");
                }

                foreach (string subcommand in Alternatives(tokens))
                {
                    Assert.Contains(current.Subcommands, item => item.Name == subcommand);
                }
            });
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public void DesktopExposesEveryImplementedCapability()
    {
        string page = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Forge.Desktop",
            "MainPage.xaml"));

        Assert.All(
            CapabilityIds.Implemented,
            id => Assert.All(
                DesktopControls[id],
                control => Assert.Contains($"x:Name=\"{control}\"", page, StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public void DesktopControlsAreWiredInCodeBehind()
    {
        // A declared control (checked above) that the code-behind never assigns is dead XAML — the
        // exact shape of the round-1 P8.83-88 bug, where ProvidersLabel existed nowhere at all and
        // a later fix could just as easily add the label without wiring it.
        string codeBehind = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Forge.Desktop",
            "MainPage.xaml.cs"));

        Assert.All(
            CapabilityIds.Implemented,
            id => Assert.All(
                DesktopControls[id],
                control => Assert.Contains(control, codeBehind, StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public void EveryDesktopFreeTextEntryCarriesAScreenReaderNameAndPlaceholder()
    {
        // ADR 0005 requires every action to be screen-reader named. No Entry on this page has an
        // adjacent visible label, so every one of them must be described — and the list is derived
        // from the XAML rather than hand-maintained, so a newly added Entry fails here instead of
        // shipping unlabeled. DesktopControlsAreWiredInCodeBehind cannot cover this: it only proves
        // the control name appears somewhere in the code-behind. A static check fully covers the
        // risk, since no MAUI control can be instantiated headlessly in this suite.
        string desktop = Path.Combine(RepositoryRoot.Find(), "src", "Forge.Desktop");
        string codeBehind = File.ReadAllText(Path.Combine(desktop, "MainPage.xaml.cs"));
        string[] entries = [.. Regex
            .Matches(File.ReadAllText(Path.Combine(desktop, "MainPage.xaml")), "<Entry[^>]*?x:Name=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)];

        Assert.NotEmpty(entries);
        // Both halves of the fix, not just the screen-reader name: the visible placeholder is what
        // a sighted user reads, and the CHANGELOG claims both.
        Assert.Contains("SemanticProperties.SetDescription(entry, label)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("entry.Placeholder = label", codeBehind, StringComparison.Ordinal);
        Assert.All(
            entries,
            entry => Assert.Contains($"Describe({entry}, ", codeBehind, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public void GateConfirmationDialogNamesItsTargetInsteadOfRepeatingTheActionName()
    {
        // ADR 0021: a confirmation dialog for an irreversible human decision must name the sprint
        // and node it acts on, not repeat the action name as title/message/accept. No MAUI control
        // can be instantiated headlessly in this suite (matching the reasoning above), so this pins
        // the code-behind actually sources the dialog's message from MainPageViewModel.GatePrompt
        // rather than, e.g., the button's own action text.
        string codeBehind = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "src", "Forge.Desktop", "MainPage.xaml.cs"));

        Assert.Contains("viewModel.GatePrompt(", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public void AttemptSupersedeConfirmationDialogNamesItsTargetInsteadOfRepeatingTheActionName()
    {
        // Same reasoning as the gate check above, for attempt.supersede.
        string codeBehind = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "src", "Forge.Desktop", "MainPage.xaml.cs"));

        Assert.Contains("viewModel.AttemptSupersedePrompt(", codeBehind, StringComparison.Ordinal);
    }

    /// <summary>Round 1 review of PR #66: the original `integration.skill` install/remove dialogs
    /// repeated the action name as their own message instead of naming a target -- the same defect
    /// the two checks above already forbid for `workflow.review`/`attempt.supersede`. `remove`
    /// deletes a file from the project's own working tree, so this needs the same "name what will
    /// actually be acted on" rigor, reusing `MainPageViewModel.InitializePrompt`'s existing shape
    /// rather than introducing a third, independent prompt-naming scheme.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void IntegrationWriteConfirmationDialogsNameTheirTargetInsteadOfRepeatingTheActionName()
    {
        Assert.Contains(
            "viewModel.InitializePrompt(", MethodBody("private async Task InstallIntegrationAsync()"),
            StringComparison.Ordinal);
        Assert.Contains(
            "viewModel.InitializePrompt(", MethodBody("private async Task RemoveIntegrationAsync()"),
            StringComparison.Ordinal);
    }

    /// <summary>Same reasoning as the checks above, applied proactively for `sprint.manage`'s
    /// `cancel` verb (ADR 0027) rather than waiting for a review round to catch it: cancelling a
    /// sprint is destructive, so its dialog must name the sprint it targets, not repeat the action
    /// name.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void SprintCancelConfirmationDialogNamesItsTargetInsteadOfRepeatingTheActionName()
    {
        Assert.Contains(
            "viewModel.SprintCancelPrompt(", MethodBody("private async Task CancelSprintAsync()"),
            StringComparison.Ordinal);
    }

    /// <summary>Round 3 review: the blank-attempt-id guard had no test proving it runs at all, let
    /// alone before the dialog -- deleting it left every other test green. No MAUI control can be
    /// instantiated headlessly (see the dialog-naming checks above), so this pins both the guard's
    /// presence and its ordering relative to <c>DisplayAlertAsync</c> directly in the code-behind
    /// text.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void SupersedeAttemptRefusesABlankAttemptIdBeforeShowingTheConfirmationDialog()
    {
        string method = SupersedeAttemptMethodBody();

        int guardIndex = method.IndexOf("AttemptId is null", StringComparison.Ordinal);
        int dialogIndex = method.IndexOf("DisplayAlertAsync(", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "SupersedeAttemptAsync no longer refuses a blank attempt id.");
        Assert.True(
            guardIndex < dialogIndex, "The blank-attempt-id guard must run before the confirmation dialog.");
    }

    /// <summary>Round 3 review: a blank replacement instruction was refused only after the user
    /// confirmed the irreversible action, asymmetric with the attempt-id guard immediately above and
    /// its own "ask before, not after" rationale.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void SupersedeAttemptRefusesABlankInstructionBeforeShowingTheConfirmationDialog()
    {
        string method = SupersedeAttemptMethodBody();

        int guardIndex = method.IndexOf("AttemptInstructionEntry.Text", StringComparison.Ordinal);
        int dialogIndex = method.IndexOf("DisplayAlertAsync(", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "SupersedeAttemptAsync no longer refuses a blank instruction.");
        Assert.True(
            guardIndex < dialogIndex, "The blank-instruction guard must run before the confirmation dialog.");
    }

    private static string SupersedeAttemptMethodBody() => MethodBody("private async Task SupersedeAttemptAsync()");

    /// <summary>Round 1 review of PR #65 found <c>RefreshAsync</c> cleared <c>EventsLabel</c>
    /// unconditionally on every refresh -- including a routine `Refresh` click or the implicit
    /// refresh after an unrelated action -- discarding a still-valid poll's rendered page for no
    /// reason. Unlike <c>GateResultLabel</c>/<c>AttemptSupersedeResultLabel</c> (a one-shot
    /// mutation's own outcome, safe to always clear), `EventsLabel` is a live view of the view
    /// model's own stored cursor and must only reset on the same condition that invalidates that
    /// cursor: a project-root switch. No MAUI control can be instantiated headlessly, so this pins
    /// the guard directly in the code-behind text, the same way the supersede guards above do.
    /// Round 2 review found the original version of this test only checked text ORDER (guard
    /// before clear), which a mutation moving the clear back outside the guard's own `{ }` block --
    /// reintroducing round 1's exact defect -- would not fail: the guard text would still appear
    /// earlier in the method than the now-unconditional clear. This checks CONTAINMENT instead: the
    /// clear must be textually inside the guard `if` statement's own block.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void RefreshAsyncOnlyClearsEventsLabelWhenTheProjectRootChanged()
    {
        string method = MethodBody("public async Task RefreshAsync()");

        int guardIndex = method.IndexOf(
            "!string.Equals(requestedRoot, lastPolledEventsProjectRoot", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "RefreshAsync no longer guards EventsLabel's reset by project root.");
        int ifIndex = method.LastIndexOf("if (", guardIndex, StringComparison.Ordinal);
        Assert.True(ifIndex >= 0, "The project-root guard is no longer an `if` condition.");
        string guardBlock = BracedBlock(method, ifIndex);

        Assert.Contains("EventsLabel.Text = string.Empty;", guardBlock, StringComparison.Ordinal);
        // Round 3 review found the block-containment check above alone still let a mutation
        // deleting this assignment pass: without it, lastPolledEventsProjectRoot never advances
        // past its initial null, so the guard is true on every refresh against any typed project
        // root -- round 1's exact defect, verified empirically to leave the whole suite green.
        Assert.Contains(
            "lastPolledEventsProjectRoot = requestedRoot;", guardBlock, StringComparison.Ordinal);
    }

    private static string MethodBody(string signature)
    {
        string codeBehind = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "src", "Forge.Desktop", "MainPage.xaml.cs"));
        int start = codeBehind.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"MainPage.xaml.cs no longer declares '{signature}'.");
        return BracedBlock(codeBehind, start);
    }

    /// <summary>Returns the `{ ... }` block starting at the first `{` at or after
    /// <paramref name="searchStart"/>, matched by brace depth rather than by indentation or a fixed
    /// line count, so it is correct regardless of how the block is formatted.</summary>
    private static string BracedBlock(string source, int searchStart)
    {
        int bodyStart = source.IndexOf('{', searchStart);
        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[bodyStart..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException("Block's closing brace was not found.");
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameSprintTreeAndDetailForOneSnapshot()
    {
        // Sharing SurfaceFormatting is not by itself the no-drift guarantee this refactor claims:
        // either surface can still wrap, reorder, or filter the shared lines on its way to the
        // screen (the Desktop path already wraps them in Render(...) and trims). This compares the
        // two rendered projections of one project directly, so any such divergence fails here.
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
                Graph: [new("a", NodeKind.Work, []), new("b", NodeKind.Work, ["a"])]),
            cancellationToken)).SprintId!;
        // Drive the sprint far enough to own a real attempt and a real finding, so the fixture
        // exercises AppendNodeTree's OwnerId nesting loop and its findings block. Comparing two
        // renderings of a sprint that has neither would leave both branches dead, and a surface
        // that silently dropped attempt or finding lines would still pass.
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
        Assert.True(started.Succeeded);
        Assert.True((await scheduler.RecordFindingAsync(
            environment.ProjectRoot,
            sprintId,
            FindingSeverity.High,
            "finding.example",
            new Dictionary<string, string?>(),
            ["src/Foo.cs:1"],
            null,
            cancellationToken)).Succeeded);
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter tree = new(CultureInfo.InvariantCulture);
        StringWriter inspect = new(CultureInfo.InvariantCulture);
        // Separate diagnostics writers: the CLI defaults them to `output`, which would fold the
        // diagnostics channel into the text being compared and make this assertion depend on the
        // fixture happening to produce no diagnostic.
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        string id = sprintId.Value.ToString("D", CultureInfo.InvariantCulture);

        Assert.Equal(0, await CliApplication
            .CreateRootCommand(text, tree, environment.Application, diagnostics)
            .Parse(["tree", "--project-root", environment.ProjectRoot, "--sprint", id])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        Assert.Equal(0, await CliApplication
            .CreateRootCommand(text, inspect, environment.Application, diagnostics)
            .Parse(["sprint", "inspect", id, "--project-root", environment.ProjectRoot])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        MainPageSnapshot desktop = await new MainPageViewModel(text, environment.Application)
            .RefreshAsync(environment.ProjectRoot, id, cancellationToken);

        // `forge tree` prefixes the project line WriteProject writes; the sprint sections after it
        // are what both surfaces share, so compare from the sprint title onwards.
        Assert.Equal(SprintSection(tree.ToString(), text.Resolve(MessageKeys.SprintsTitle)), desktop.SprintsText);
        Assert.Equal(
            SprintSection(inspect.ToString(), text.Resolve(MessageKeys.SprintDetailsTitle)),
            desktop.SprintDetailsText);
        // The comparison above is only as strong as its fixture, so pin that the compared text
        // really carries an attempt nested under its node and a finding.
        string attemptId = started.AttemptId!.Value.ToString("D", CultureInfo.InvariantCulture);
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"        {attemptId} "),
            desktop.SprintsText,
            StringComparison.Ordinal);
        Assert.Contains(text.Resolve(MessageKeys.FindingsLabel), desktop.SprintsText, StringComparison.Ordinal);
    }

    private static string SprintSection(string cliOutput, string title) =>
        cliOutput[cliOutput.IndexOf(title, StringComparison.Ordinal)..].TrimEnd();

    /// <summary>Round 1 review of PR #65 found `SurfaceFormatting.EventLines`'s extraction had no
    /// test proving the CLI and Desktop actually render identically -- sharing the helper is not
    /// itself the no-drift guarantee, matching the caveat
    /// <see cref="DesktopAndCliRenderTheSameSprintTreeAndDetailForOneSnapshot"/> already states for
    /// its own capability. Same shape here: drive one real event, render through both surfaces with
    /// diagnostics on a separate channel (so a diagnostic present on one side never folds into the
    /// text being compared), and diff the text directly.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameEventsForOneSnapshot()
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
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter cliOutput = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);

        Assert.Equal(0, await CliApplication
            .CreateRootCommand(text, cliOutput, environment.Application, diagnostics)
            .Parse(["events", "--project-root", environment.ProjectRoot])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        string desktop = await new MainPageViewModel(text, environment.Application)
            .PollEventsAsync(environment.ProjectRoot, cancellationToken);

        Assert.Equal(cliOutput.ToString().TrimEnd(), desktop);
        Assert.Empty(diagnostics.ToString());
        // The comparison above is only as strong as its fixture, so pin that the compared text
        // really carries this sprint's own event -- an empty page on both sides would pass too.
        Assert.Contains(
            sprintId.Value.ToString("D", CultureInfo.InvariantCulture), desktop, StringComparison.Ordinal);
    }

    /// <summary>Same no-drift proof as <see cref="DesktopAndCliRenderTheSameEventsForOneSnapshot"/>,
    /// for `SurfaceFormatting.IntegrationInspectionLines` (ADR 0026).</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameIntegrationPreviewForOneSnapshot()
    {
        // A real integration generator for TestEnvironment's own default enabled provider
        // ("fake") -- without one, no generator claims the enabled set, every artifact row stays
        // empty on both sides, and the comparison below would pass even if
        // IntegrationInspectionRow itself were broken. The real Claude/Codex generators live in
        // Windows-only OS-adapter projects this neutral test never references (ADR 0007), so
        // TestEnvironment's own `generators` parameter exists specifically for this.
        using TestEnvironment environment = new(generators: [new FakeIntegrationGenerator()]);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken);
        Assert.True(init.Succeeded);
        // Round 2 review found this fixture closed only half of round 1 finding 2: the artifact
        // row was exercised, but AppendIntegrationDocumentErrors -- the other drift-capable part
        // of the shared projection, and the one this PR newly exposed to Desktop -- was never
        // reached by any test on either surface. A malformed rule document (missing frontmatter,
        // matching IntegrationGenerationTests' own fixture shape) forces one.
        string rulesDirectory = Path.Combine(ProjectRootResolver.ForgeDirectory(environment.ProjectRoot), "rules");
        Directory.CreateDirectory(rulesDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(rulesDirectory, "broken.md"), "No frontmatter here.", cancellationToken);
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter cliOutput = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);

        Assert.Equal(0, await CliApplication
            .CreateRootCommand(text, cliOutput, environment.Application, diagnostics)
            .Parse(["integration", "skill", "generate", "--project-root", environment.ProjectRoot])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        string desktop = await new MainPageViewModel(text, environment.Application)
            .GenerateIntegrationPreviewAsync(environment.ProjectRoot, cancellationToken);

        Assert.Equal(cliOutput.ToString().TrimEnd(), desktop);
        Assert.Empty(diagnostics.ToString());
        // The comparison above is only as strong as its fixture, so pin that the compared text
        // really carries a real artifact row and the malformed document's own error row -- either
        // one silently empty on both sides would pass too.
        Assert.Contains("fake", desktop, StringComparison.Ordinal);
        Assert.Contains("rules/broken.md", desktop, StringComparison.Ordinal);
    }

    /// <summary>Same no-drift proof as <see cref="DesktopAndCliRenderTheSameIntegrationPreviewForOneSnapshot"/>,
    /// for the mutating half, `SurfaceFormatting.IntegrationWriteLines` (ADR 0026 round 1 review:
    /// the original PR's parity test covered only the read verb, leaving the write formatting
    /// helper -- the one actually reachable from a destructive action -- with zero coverage). Two
    /// separate projects, not one install-then-compare-a-second-install: a second install against
    /// the same target renders `Unchanged`, not `Written`, which would make the two sides diverge
    /// for a reason unrelated to what this test exists to prove.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameIntegrationWriteForOneSnapshot()
    {
        using TestEnvironment cliEnvironment = new(generators: [new FakeIntegrationGenerator()]);
        using TestEnvironment desktopEnvironment = new(generators: [new FakeIntegrationGenerator()]);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.True((await cliEnvironment.InitializeAsync(
            cliEnvironment.ProjectRoot, true, cancellationToken)).Succeeded);
        Assert.True((await desktopEnvironment.InitializeAsync(
            desktopEnvironment.ProjectRoot, true, cancellationToken)).Succeeded);
        // Round 3 review found this test's own document-error coverage was missing: round 2 added
        // a malformed rule document only to the preview (generate) parity test, but
        // IntegrationInstallationService.InstallAsync/RemoveAsync each run their own InspectAsync
        // and propagate its DocumentErrors verbatim, so the write path renders the identical error
        // row -- on the verb actually reachable from a destructive action. Written into both
        // projects identically so the comparison stays about rendering, not fixture divergence.
        foreach (TestEnvironment environment in new[] { cliEnvironment, desktopEnvironment })
        {
            string rulesDirectory = Path.Combine(
                ProjectRootResolver.ForgeDirectory(environment.ProjectRoot), "rules");
            Directory.CreateDirectory(rulesDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(rulesDirectory, "broken.md"), "No frontmatter here.", cancellationToken);
        }

        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter cliOutput = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);

        Assert.Equal(0, await CliApplication
            .CreateRootCommand(text, cliOutput, cliEnvironment.Application, diagnostics)
            .Parse(["integration", "skill", "install", "--yes", "--project-root", cliEnvironment.ProjectRoot])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        string desktop = await new MainPageViewModel(text, desktopEnvironment.Application)
            .InstallIntegrationAsync(desktopEnvironment.ProjectRoot, true, cancellationToken);

        Assert.Equal(cliOutput.ToString().TrimEnd(), desktop);
        Assert.Empty(diagnostics.ToString());
        // The comparison above is only as strong as its fixture, so pin that a real artifact row
        // is present, that it genuinely reflects a write (not a no-op), and that the malformed
        // document's own error row is present too -- any one of them silently empty on both sides
        // would pass regardless.
        Assert.Contains("fake", desktop, StringComparison.Ordinal);
        Assert.Contains("written", desktop, StringComparison.Ordinal);
        Assert.Contains("rules/broken.md", desktop, StringComparison.Ordinal);
    }

    /// <summary>Same no-drift proof as the notification/integration parity tests above, for
    /// `SurfaceFormatting.SprintCreatedMessage` (ADR 0027). Unlike those, `create` cannot use
    /// literal text equality: each call mints a fresh <see cref="Guid"/>, so the two sides'
    /// messages can never be byte-identical even when the formatting is correct. Compares the
    /// format instead -- the fixed prefix and a well-formed `"D"`-format id after it -- which is
    /// the only property that can actually drift between the two call sites.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameSprintCreatedMessageFormat()
    {
        using TestEnvironment cliEnvironment = new();
        using TestEnvironment desktopEnvironment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.True((await cliEnvironment.InitializeAsync(
            cliEnvironment.ProjectRoot, true, cancellationToken)).Succeeded);
        Assert.True((await desktopEnvironment.InitializeAsync(
            desktopEnvironment.ProjectRoot, true, cancellationToken)).Succeeded);
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter cliOutput = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);

        Assert.Equal(0, await CliApplication
            .CreateRootCommand(text, cliOutput, cliEnvironment.Application, diagnostics)
            .Parse(["sprint", "create", "--project-root", cliEnvironment.ProjectRoot])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        string desktop = await new MainPageViewModel(text, desktopEnvironment.Application)
            .CreateSprintAsync(desktopEnvironment.ProjectRoot, cancellationToken);

        Assert.Empty(diagnostics.ToString());
        string cli = cliOutput.ToString().TrimEnd();
        // Round 1 review found the original check ("starts with the prefix" plus "the tail parses
        // as a Guid") independently on each side never actually compared the two surfaces against
        // each other -- and skipped exactly one unasserted separator character, so a drifted
        // separator (e.g. "prefix:id" instead of "prefix id") would still slip through on both
        // sides. A "D"-format Guid is always exactly 36 characters, so everything before the last
        // 36 characters -- prefix AND separator together -- is compared directly for equality
        // between the two surfaces, and the tail is independently confirmed to actually be a Guid.
        Assert.Equal(cli[..^36], desktop[..^36]);
        Assert.True(Guid.TryParse(cli[^36..], out _), $"CLI message did not end in a well-formed id: {cli}");
        Assert.True(
            Guid.TryParse(desktop[^36..], out _), $"Desktop message did not end in a well-formed id: {desktop}");
    }

    /// <summary>Same no-drift proof as <see cref="DesktopAndCliRenderTheSameSprintCreatedMessageFormat"/>,
    /// for `SurfaceFormatting.SprintTransitionMessage`'s `run` shape (`includeResultingState:
    /// true`). Two separate projects, matching the write-parity test's own established shape --
    /// each side's freshly created sprint deterministically reaches `ready` on its first `run`, so
    /// (unlike `create`) the rendered text is genuinely comparable by full equality.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameSprintRunMessageForOneSnapshot()
    {
        using TestEnvironment cliEnvironment = new();
        using TestEnvironment desktopEnvironment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId cliSprintId = await CreateDraftSprintAsync(cliEnvironment, cancellationToken);
        SprintId desktopSprintId = await CreateDraftSprintAsync(desktopEnvironment, cancellationToken);
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter cliOutput = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);

        Assert.Equal(0, await CliApplication
            .CreateRootCommand(text, cliOutput, cliEnvironment.Application, diagnostics)
            .Parse([
                "sprint", "run", "--sprint", cliSprintId.Value.ToString(), "--project-root",
                cliEnvironment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        string desktop = await new MainPageViewModel(text, desktopEnvironment.Application)
            .RunSprintAsync(desktopEnvironment.ProjectRoot, desktopSprintId.Value.ToString(), cancellationToken);

        Assert.Equal(cliOutput.ToString().TrimEnd(), desktop);
        Assert.Empty(diagnostics.ToString());
        // The comparison above is only as strong as its fixture, so pin that this is genuinely the
        // "advanced with a known resulting state" branch, not the unknown-state fallback.
        Assert.Contains(SurfaceFormatting.Machine(SprintState.Ready), desktop, StringComparison.Ordinal);
    }

    /// <summary>Same no-drift proof as <see cref="DesktopAndCliRenderTheSameSprintRunMessageForOneSnapshot"/>,
    /// for `resume`'s fixed-text shape (`includeResultingState: false`). A genuinely blocked sprint
    /// (a rejected human gate, matching `SprintLifecycleCliTests`'s own fixture shape), not a fresh
    /// one: `resume` against a non-blocked sprint fails, and a failure renders nothing but the
    /// diagnostic on both sides -- exactly the "an empty state on both sides would pass too"
    /// pattern round 1 review of PR #65 rejected for the events parity test.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameSprintResumeMessageForOneSnapshot()
    {
        using TestEnvironment cliEnvironment = new();
        using TestEnvironment desktopEnvironment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId cliSprintId = await CreateBlockedSprintAsync(cliEnvironment, cancellationToken);
        SprintId desktopSprintId = await CreateBlockedSprintAsync(desktopEnvironment, cancellationToken);
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter cliOutput = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);

        Assert.Equal(0, await CliApplication
            .CreateRootCommand(text, cliOutput, cliEnvironment.Application, diagnostics)
            .Parse([
                "sprint", "resume", "--sprint", cliSprintId.Value.ToString(), "--project-root",
                cliEnvironment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        string desktop = await new MainPageViewModel(text, desktopEnvironment.Application)
            .ResumeSprintAsync(desktopEnvironment.ProjectRoot, desktopSprintId.Value.ToString(), cancellationToken);

        Assert.Equal(cliOutput.ToString().TrimEnd(), desktop);
        Assert.Empty(diagnostics.ToString());
        Assert.Equal(text.Resolve(MessageKeys.SprintResumed), desktop);
    }

    /// <summary>Same no-drift proof as <see cref="DesktopAndCliRenderTheSameSprintRunMessageForOneSnapshot"/>,
    /// for `cancel`'s fixed-text shape. Confirmation is passed through directly (`true`), matching
    /// `install`/`remove`'s own ordinarily-bypassable shape rather than the human-only pair's.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameSprintCancelMessageForOneSnapshot()
    {
        using TestEnvironment cliEnvironment = new();
        using TestEnvironment desktopEnvironment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId cliSprintId = await CreateDraftSprintAsync(cliEnvironment, cancellationToken);
        SprintId desktopSprintId = await CreateDraftSprintAsync(desktopEnvironment, cancellationToken);
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter cliOutput = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);

        Assert.Equal(0, await CliApplication
            .CreateRootCommand(text, cliOutput, cliEnvironment.Application, diagnostics)
            .Parse([
                "sprint", "cancel", "--yes", "--sprint", cliSprintId.Value.ToString(), "--project-root",
                cliEnvironment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        string desktop = await new MainPageViewModel(text, desktopEnvironment.Application)
            .CancelSprintAsync(
                desktopEnvironment.ProjectRoot, desktopSprintId.Value.ToString(), true, cancellationToken);

        Assert.Equal(cliOutput.ToString().TrimEnd(), desktop);
        Assert.Empty(diagnostics.ToString());
        Assert.Equal(text.Resolve(MessageKeys.SprintCancelled), desktop);
    }

    private static readonly IReadOnlyList<NodeDefinition> SprintManageParityGraph = [new("a", NodeKind.Work, [])];

    private static readonly IReadOnlyList<NodeDefinition> SprintManageGateGraph =
        [new("gate", NodeKind.HumanGate, [])];

    private static async Task<SprintId> CreateDraftSprintAsync(
        TestEnvironment environment, CancellationToken cancellationToken)
    {
        Assert.True((await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken)).Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        return (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: SprintManageParityGraph),
            cancellationToken)).SprintId!;
    }

    /// <summary>Matches `SprintLifecycleCliTests.SprintResumeCommandUnblocksASprintBlockedByARejectedGate`'s
    /// own fixture shape: run a single-gate sprint to `running`, then reject its gate directly
    /// through <see cref="SprintScheduler"/> (bypassing the CLI/Desktop capability this ADR itself
    /// covers, so the fixture setup can never accidentally exercise the thing under test).</summary>
    private static async Task<SprintId> CreateBlockedSprintAsync(
        TestEnvironment environment, CancellationToken cancellationToken)
    {
        Assert.True((await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken)).Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: SprintManageGateGraph),
            cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        NodeSnapshot gate = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["gate"];
        await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", false, gate.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate), cancellationToken);
        SprintSnapshot blocked =
            (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Blocked, blocked.State);
        return sprintId;
    }

    /// <summary>A minimal single-provider generator so this test stays in the portable
    /// `Acceptance/` group instead of depending on the real, Windows-only Claude/Codex adapters --
    /// same shape as `IntegrationInstallationServiceTests`'s own private fake.</summary>
    private sealed class FakeIntegrationGenerator : IProviderIntegrationGenerator
    {
        public ProviderId ProviderId { get; } = new("fake");

        public GeneratedArtifact Generate(CanonicalIntegrationSource source) =>
            new(
                ProviderId,
                "TEST_INTEGRATION.md",
                source.Content,
                "text/markdown",
                "agent_facing",
                source.Language,
                source.SourceDigest,
                source.PolicySnapshotHash,
                source.GeneratorVersion);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public void ImplementedCapabilitiesDeclareBothSurfaces()
    {
        using JsonDocument contract = ReadCapabilities();
        Dictionary<string, JsonElement> capabilities = Index(contract);

        Assert.All(
            CapabilityIds.Implemented,
            id =>
            {
                JsonElement capability = capabilities[id];
                Assert.False(string.IsNullOrWhiteSpace(capability.GetProperty("cli").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(capability.GetProperty("desktop").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(capability.GetProperty("permission").GetString()));
            });
    }

    private static string[] DocumentedCli(string capabilityId)
    {
        using JsonDocument contract = ReadCapabilities();
        return Index(contract)[capabilityId]
            .GetProperty("cli")
            .GetString()!
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(token => token.Trim('[', ']'))
            .ToArray();
    }

    private static IEnumerable<string> Alternatives(IEnumerable<string> tokens) =>
        // Only tokens naming alternative *subcommands* (e.g. `<approve|reject>`) qualify -- stopping
        // at the first `--option` excludes a token with the same `<a|b>` shape that instead describes
        // an option's own value grammar (e.g. `attempt.supersede`'s `--instruction-file <path|->`,
        // where "-" is a literal accepted value, not a sibling subcommand named "-").
        tokens
            .TakeWhile(token => !token.StartsWith("--", StringComparison.Ordinal))
            .Where(token => token.StartsWith('<') && token.EndsWith('>') && token.Contains('|', StringComparison.Ordinal))
            .SelectMany(token => token.Trim('<', '>').Split('|'));

    private static bool HasOption(Command command, string option) =>
        command.Options.Any(item => item.Name == option) ||
        command.Subcommands.Any(subcommand => HasOption(subcommand, option));

    private static JsonDocument ReadCapabilities() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "docs",
            "contracts",
            "v1",
            "capabilities.json")));

    private static Dictionary<string, JsonElement> Index(JsonDocument contract) =>
        contract.RootElement
            .GetProperty("capabilities")
            .EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);
}
