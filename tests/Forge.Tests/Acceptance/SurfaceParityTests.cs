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
    /// <summary>
    /// Slice 5 replaced the monolithic, statically-named `MainPage.xaml` with
    /// <c>WorkspaceShellPage</c>'s dynamically built controls (plan section 9.3/9.4) -- there is no
    /// longer a fixed <c>x:Name</c> per control for a capability check to look up. The equivalent
    /// guarantee this dictionary now encodes is "the composition root's code actually calls the
    /// neutral view-model method that performs this capability," proven by scanning the shell's own
    /// source text (see <see cref="DesktopSourceText"/>) -- still reliable outside a live MAUI
    /// process, matching plan section 11 Slice 5 item 5.
    /// </summary>
    private static readonly Dictionary<string, string[]> DesktopCapabilityCalls = new(StringComparer.Ordinal)
    {
        [CapabilityIds.ProjectSnapshot] = [".RefreshHeaderAsync(", "projectOverview.LoadAsync("],
        [CapabilityIds.ProjectInitialize] = [".InitializeAsync(", ".InitializePrompt("],
        [CapabilityIds.ConfigurationManage] = [".SaveAsync("],
        [CapabilityIds.ProviderHealth] = ["snapshot.KnownProviders", "snapshot.Providers"],
        [CapabilityIds.WorkflowReview] = [".ResolveGateAsync(", ".GatePrompt("],
        [CapabilityIds.AttemptSupersede] = [".SupersedeAttemptAsync(", ".AttemptSupersedePrompt("],
        [CapabilityIds.ControlEvents] = [".PollEventsAsync("],
        [CapabilityIds.IntegrationSkill] =
            [".GenerateIntegrationPreviewAsync(", ".InstallIntegrationAsync(", ".RemoveIntegrationAsync("],
        [CapabilityIds.SprintManage] =
            [".CreateSprintAsync(", ".RunSprintAsync(", ".ResumeSprintAsync(", ".CancelSprintAsync("],
        [CapabilityIds.WorkflowConfirm] = [".ConfirmNodeAsync(", ".ConfirmPrompt("],
        [CapabilityIds.WorkflowTestWork] = [".RecordTestWorkAsync(", ".TestWorkPrompt("],
        [CapabilityIds.WorkflowFinalize] = [".FinalizeSprintAsync(", ".FinalizePrompt("],
    };

    /// <summary>Every <c>WorkspaceShellPage</c> partial-class file, concatenated -- the capability
    /// and screen-reader-naming checks below all read this same combined text rather than one fixed
    /// file, since Slice 5 splits the shell across several partial-class files by concern (routing/
    /// sidebar, Forge settings, project overview, project settings, sprint workspace).</summary>
    private static string DesktopSourceText() =>
        string.Concat(Directory.GetFiles(
                Path.Combine(RepositoryRoot.Find(), "src", "Forge.Desktop"), "WorkspaceShellPage*.cs")
            .Select(File.ReadAllText));

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

    /// <summary>
    /// Round 4 review of PR #95 (finding 4): `workflow.stop_operation` documented `--sprint` as
    /// optional (`[--sprint &lt;id&gt;]`) while `CreateAttemptStopCommand` defines it with
    /// `Required = true` -- a drift <see cref="CliExposesEveryDocumentedCapabilityCommand"/> could
    /// never catch, since that test only walks <see cref="CapabilityIds.Implemented"/> and
    /// `workflow.stop_operation` is deliberately excluded from it (ADR 0047: reserved until Desktop
    /// parity ships, `capabilities.json`'s own `public_requires_both_surfaces` rule) even though its
    /// CLI half has shipped since ADR 0047. This test closes exactly that gap: it compares this one
    /// capability's documented `cli` string's bracket-implied option requiredness against the actual
    /// command tree, so a future edit that reintroduces the same drift (either direction -- widening
    /// an option to optional in code without updating the doc, or vice versa) fails here instead of
    /// silently shipping. Deliberately scoped to this one capability rather than every reserved
    /// entry: most of the others (`workspace.summary`, `sprint.timeline`, ...) have no CLI command at
    /// all yet, and a handful of already-implemented commands elsewhere in this contract have their
    /// own pre-existing, unrelated documentation gaps around `--project-root`/`--bundle` that are not
    /// this PR's concern to fix.
    /// </summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void StopOperationDocumentedCliOptionsMatchTheirActualRequiredness()
    {
        using TestEnvironment environment = new();
        RootCommand root = CliApplication.CreateRootCommand(
            new SurfaceText(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture),
            new StringWriter(CultureInfo.InvariantCulture),
            environment.Application);

        string cli = ReadCli("workflow.stop_operation");
        // tokens[0] is always the fixed "forge" executable name (see DocumentedCli's own Skip(1));
        // tokens[1] is the top-level subcommand.
        string[] tokens = cli.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Command attempt = Assert.Single(root.Subcommands, subcommand => subcommand.Name == tokens[1]);
        Command stop = Assert.Single(attempt.Subcommands, subcommand => subcommand.Name == "stop");

        Assert.All(
            ParseDocumentedOptionRequiredness(cli),
            entry =>
            {
                Option? actual = FindOptionRecursively(stop, entry.Option);
                Assert.True(actual is not null, $"'stop' does not expose documented option '{entry.Option}'.");
                Assert.True(
                    actual!.Required == entry.Required,
                    $"'{entry.Option}' is documented as {(entry.Required ? "required" : "optional")} " +
                        $"but the CLI defines it as {(actual.Required ? "required" : "optional")}.");
            });
    }

    /// <summary>Same gap-closing purpose as <see cref="StopOperationDocumentedCliOptionsMatchTheirActualRequiredness"/>,
    /// for `workflow.assess_stage_transition` (Slice 3): reserved-but-CLI-shipped, so
    /// <see cref="CliExposesEveryDocumentedCapabilityCommand"/> never walks it either.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void AssessStageTransitionDocumentedCliOptionsMatchTheirActualRequiredness()
    {
        using TestEnvironment environment = new();
        RootCommand root = CliApplication.CreateRootCommand(
            new SurfaceText(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture),
            new StringWriter(CultureInfo.InvariantCulture),
            environment.Application);

        string cli = ReadCli("workflow.assess_stage_transition");
        string[] tokens = cli.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Command sprint = Assert.Single(root.Subcommands, subcommand => subcommand.Name == tokens[1]);
        Command assessStage = Assert.Single(sprint.Subcommands, subcommand => subcommand.Name == "assess-stage");

        Assert.All(
            ParseDocumentedOptionRequiredness(cli),
            entry =>
            {
                Option? actual = FindOptionRecursively(assessStage, entry.Option);
                Assert.True(actual is not null, $"'assess-stage' does not expose documented option '{entry.Option}'.");
                Assert.True(
                    actual!.Required == entry.Required,
                    $"'{entry.Option}' is documented as {(entry.Required ? "required" : "optional")} " +
                        $"but the CLI defines it as {(actual.Required ? "required" : "optional")}.");
            });
    }

    /// <summary>Same gap-closing purpose as <see cref="StopOperationDocumentedCliOptionsMatchTheirActualRequiredness"/>,
    /// for `sprint.move_stage` (Slice 3): reserved-but-CLI-shipped, so
    /// <see cref="CliExposesEveryDocumentedCapabilityCommand"/> never walks it either.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void MoveSprintToStageDocumentedCliOptionsMatchTheirActualRequiredness()
    {
        using TestEnvironment environment = new();
        RootCommand root = CliApplication.CreateRootCommand(
            new SurfaceText(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture),
            new StringWriter(CultureInfo.InvariantCulture),
            environment.Application);

        string cli = ReadCli("sprint.move_stage");
        string[] tokens = cli.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Command sprint = Assert.Single(root.Subcommands, subcommand => subcommand.Name == tokens[1]);
        Command moveStage = Assert.Single(sprint.Subcommands, subcommand => subcommand.Name == "move-stage");

        Assert.All(
            ParseDocumentedOptionRequiredness(cli),
            entry =>
            {
                Option? actual = FindOptionRecursively(moveStage, entry.Option);
                Assert.True(actual is not null, $"'move-stage' does not expose documented option '{entry.Option}'.");
                Assert.True(
                    actual!.Required == entry.Required,
                    $"'{entry.Option}' is documented as {(entry.Required ? "required" : "optional")} " +
                        $"but the CLI defines it as {(actual.Required ? "required" : "optional")}.");
            });
    }

    /// <summary>Slice 4 (ADR 0043/0049): `workspace.summary` stays reserved (no Desktop control) but
    /// ships a real CLI command, closing the same gap
    /// <see cref="StopOperationDocumentedCliOptionsMatchTheirActualRequiredness"/> already closes for
    /// its own capability.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void WorkspaceSummaryDocumentedCliOptionsMatchTheirActualRequiredness()
    {
        using TestEnvironment environment = new();
        RootCommand root = CliApplication.CreateRootCommand(
            new SurfaceText(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture),
            new StringWriter(CultureInfo.InvariantCulture),
            environment.Application);

        string cli = ReadCli("workspace.summary");
        string[] tokens = cli.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Command workspace = Assert.Single(root.Subcommands, subcommand => subcommand.Name == tokens[1]);
        Command summary = Assert.Single(workspace.Subcommands, subcommand => subcommand.Name == "summary");

        Assert.All(
            ParseDocumentedOptionRequiredness(cli),
            entry =>
            {
                Option? actual = FindOptionRecursively(summary, entry.Option);
                Assert.True(actual is not null, $"'summary' does not expose documented option '{entry.Option}'.");
                Assert.True(actual!.Required == entry.Required);
            });
    }

    /// <summary>Same gap-closing purpose as <see cref="WorkspaceSummaryDocumentedCliOptionsMatchTheirActualRequiredness"/>,
    /// for `sprint.timeline` (Slice 4).</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void SprintTimelineDocumentedCliOptionsMatchTheirActualRequiredness()
    {
        using TestEnvironment environment = new();
        RootCommand root = CliApplication.CreateRootCommand(
            new SurfaceText(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture),
            new StringWriter(CultureInfo.InvariantCulture),
            environment.Application);

        string cli = ReadCli("sprint.timeline");
        string[] tokens = cli.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Command sprint = Assert.Single(root.Subcommands, subcommand => subcommand.Name == tokens[1]);
        Command timeline = Assert.Single(sprint.Subcommands, subcommand => subcommand.Name == "timeline");

        Assert.All(
            ParseDocumentedOptionRequiredness(cli),
            entry =>
            {
                Option? actual = FindOptionRecursively(timeline, entry.Option);
                Assert.True(actual is not null, $"'timeline' does not expose documented option '{entry.Option}'.");
                Assert.True(actual!.Required == entry.Required);
            });
    }

    /// <summary>Same gap-closing purpose as <see cref="WorkspaceSummaryDocumentedCliOptionsMatchTheirActualRequiredness"/>,
    /// for `workspace.available_actions` (Slice 4).</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void WorkspaceAvailableActionsDocumentedCliOptionsMatchTheirActualRequiredness()
    {
        using TestEnvironment environment = new();
        RootCommand root = CliApplication.CreateRootCommand(
            new SurfaceText(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture),
            new StringWriter(CultureInfo.InvariantCulture),
            environment.Application);

        string cli = ReadCli("workspace.available_actions");
        string[] tokens = cli.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Command workspace = Assert.Single(root.Subcommands, subcommand => subcommand.Name == tokens[1]);
        Command actions = Assert.Single(workspace.Subcommands, subcommand => subcommand.Name == "actions");

        Assert.All(
            ParseDocumentedOptionRequiredness(cli),
            entry =>
            {
                Option? actual = FindOptionRecursively(actions, entry.Option);
                Assert.True(actual is not null, $"'actions' does not expose documented option '{entry.Option}'.");
                Assert.True(actual!.Required == entry.Required);
            });
    }

    /// <summary>Every `--option` token a documented `cli` string mentions, paired with whether it is
    /// wrapped in a `[...]` bracket group there -- scanned on the raw, unsplit string since a bracket
    /// group can span multiple space-separated tokens (e.g. `[--sprint &lt;id&gt;]`), unlike
    /// <see cref="DocumentedCli"/>'s own per-token bracket trim, which discards that distinction.</summary>
    private static IEnumerable<(string Option, bool Required)> ParseDocumentedOptionRequiredness(string cli) =>
        Regex.Matches(cli, "--[a-zA-Z][a-zA-Z-]*")
            .Select(match => (match.Value, Required: !IsInsideBracketGroup(cli, match.Index)));

    private static bool IsInsideBracketGroup(string text, int index)
    {
        int depth = 0;
        for (int i = 0; i < index; i++)
        {
            if (text[i] == '[')
            {
                depth++;
            }
            else if (text[i] == ']')
            {
                depth--;
            }
        }

        return depth > 0;
    }

    private static Option? FindOptionRecursively(Command command, string name) =>
        command.Options.FirstOrDefault(item => item.Name == name) ??
        command.Subcommands
            .Select(subcommand => FindOptionRecursively(subcommand, name))
            .FirstOrDefault(item => item is not null);

    private static string ReadCli(string capabilityId)
    {
        using JsonDocument contract = ReadCapabilities();
        return Index(contract)[capabilityId].GetProperty("cli").GetString()!;
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public void DesktopWiresEveryImplementedCapability()
    {
        string source = DesktopSourceText();

        Assert.All(
            CapabilityIds.Implemented,
            id => Assert.All(
                DesktopCapabilityCalls[id],
                call => Assert.Contains(call, source, StringComparison.Ordinal)));
    }

    /// <summary>
    /// ADR 0005 requires every action to be screen-reader named. Slice 5's dynamically built
    /// controls have no XAML to enumerate `&lt;Entry&gt;`/`&lt;Picker&gt;` elements from (unlike the
    /// previous monolithic page), so this instead proves the aggregate discipline: every
    /// <c>new Entry</c>/<c>new Picker</c> construction in the shell is matched by a screen-reader
    /// naming call, either directly (<c>SemanticProperties.SetDescription</c>) or through the
    /// <c>Describe</c> helper both <c>WorkspaceShellPage.xaml.cs</c> and the settings pages share.
    /// Weaker than a per-instance pairing check, but still fails the moment a newly added free-text
    /// field ships with no naming call anywhere in the shell at all -- the P8.83-88 defect class this
    /// guards against -- and stays correct without hand-maintaining a control list. No MAUI control
    /// can be instantiated headlessly in this suite, so a static text check is what actually covers
    /// the risk.
    /// </summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void EveryDesktopFreeTextEntryAndPickerCarriesAScreenReaderNamingCall()
    {
        string source = DesktopSourceText();
        // Matches both `new Entry(...)`/`new Picker{...}` and the target-typed `Entry x = new(...)`
        // form the shell actually uses in most places.
        int fields = Regex.Count(
            source, @"new\s+(?:Entry|Picker)\s*[({]|\b(?:Entry|Picker)\s+\w+\s*=\s*new\s*\(");
        int namingCalls = Regex.Count(source, "SemanticProperties.SetDescription\\(|Describe\\(");

        Assert.True(fields > 0, "No Entry/Picker construction found in the workspace shell.");
        Assert.True(
            namingCalls >= fields,
            $"{fields} Entry/Picker constructions but only {namingCalls} screen-reader naming calls.");
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public void GateConfirmationDialogNamesItsTargetInsteadOfRepeatingTheActionName()
    {
        // ADR 0021: a confirmation dialog for an irreversible human decision must name the sprint
        // and node it acts on, not repeat the action name as title/message/accept. No MAUI control
        // can be instantiated headlessly in this suite (matching the reasoning above), so this pins
        // the shell actually sources the dialog's message from SprintWorkspaceViewModel.GatePrompt
        // rather than, e.g., the button's own action text.
        Assert.Contains("sprintWorkspace.GatePrompt(", DesktopSourceText(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public void AttemptSupersedeConfirmationDialogNamesItsTargetInsteadOfRepeatingTheActionName()
    {
        // Same reasoning as the gate check above, for attempt.supersede.
        Assert.Contains("sprintWorkspace.AttemptSupersedePrompt(", DesktopSourceText(), StringComparison.Ordinal);
    }

    /// <summary>Same reasoning as the gate/attempt-supersede checks above, for `workflow.confirm`
    /// (ADR 0037).</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void ConfirmConfirmationDialogNamesItsTargetInsteadOfRepeatingTheActionName() =>
        Assert.Contains("sprintWorkspace.ConfirmPrompt(", DesktopSourceText(), StringComparison.Ordinal);

    /// <summary>Same reasoning, for `workflow.test_work` (ADR 0037).</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void TestWorkConfirmationDialogNamesItsTargetInsteadOfRepeatingTheActionName() =>
        Assert.Contains("sprintWorkspace.TestWorkPrompt(", DesktopSourceText(), StringComparison.Ordinal);

    /// <summary>Same reasoning, for `workflow.finalize` (ADR 0037).</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void FinalizeConfirmationDialogNamesItsTargetInsteadOfRepeatingTheActionName() =>
        Assert.Contains("sprintWorkspace.FinalizePrompt(", DesktopSourceText(), StringComparison.Ordinal);

    /// <summary>Same reasoning as
    /// <see cref="SupersedeAttemptRefusesABlankInstructionBeforeShowingTheConfirmationDialog"/>,
    /// for `workflow.confirm`'s own required free-text fields (ADR 0037): neither has a default to
    /// fall back to, so both must be refused before the dialog shows, not after. Unlike the previous
    /// monolithic page, this guard lives in a local function (<c>ConfirmAsync</c>) rather than a
    /// named method, but the same anchor-text-then-brace-match technique locates its body.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void ConfirmNodeRefusesBlankRequiredFieldsBeforeShowingTheConfirmationDialog()
    {
        string method = SprintWorkspaceBody("async Task ConfirmAsync(ConfirmationOutcome outcome)");

        int definitionGuardIndex = method.IndexOf("definitionOfDoneEntry.Text", StringComparison.Ordinal);
        int evidenceGuardIndex = method.IndexOf("evidenceEntry.Text", StringComparison.Ordinal);
        int dialogIndex = method.IndexOf("DisplayAlertAsync(", StringComparison.Ordinal);
        Assert.True(definitionGuardIndex >= 0, "ConfirmAsync no longer refuses a blank definition of done.");
        Assert.True(evidenceGuardIndex >= 0, "ConfirmAsync no longer refuses blank evidence.");
        Assert.True(dialogIndex >= 0, "ConfirmAsync no longer shows a confirmation dialog.");
        Assert.True(
            definitionGuardIndex < dialogIndex,
            "The blank-definition-of-done guard must run before the confirmation dialog.");
        Assert.True(
            evidenceGuardIndex < dialogIndex, "The blank-evidence guard must run before the confirmation dialog.");
    }

    /// <summary>Same reasoning, for `workflow.test_work`'s own required justification (ADR
    /// 0037).</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void RecordTestWorkRefusesABlankJustificationBeforeShowingTheConfirmationDialog()
    {
        string method = SprintWorkspaceBody("async Task TestWorkAsync(TestWorkOutcome outcome)");

        int guardIndex = method.IndexOf("justificationEntry.Text", StringComparison.Ordinal);
        int dialogIndex = method.IndexOf("DisplayAlertAsync(", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "TestWorkAsync no longer refuses a blank justification.");
        Assert.True(dialogIndex >= 0, "TestWorkAsync no longer shows a confirmation dialog.");
        Assert.True(guardIndex < dialogIndex, "The blank-justification guard must run before the confirmation dialog.");
    }

    /// <summary>Round 1 review of PR #66: the original `integration.skill` install/remove dialogs
    /// repeated the action name as their own message instead of naming a target -- the same defect
    /// the two checks above already forbid for `workflow.review`/`attempt.supersede`. `remove`
    /// deletes a file from the project's own working tree, so this needs the same "name what will
    /// actually be acted on" rigor. Slice 5's <c>ProjectSettingsViewModel</c> has no project snapshot
    /// handy to reuse <c>MainPageViewModel.InitializePrompt</c>'s exact shape, so
    /// <c>WorkspaceShellPage.ProjectSettings.cs</c> uses its own equally-target-naming
    /// <c>RootPrompt</c> helper instead -- still the project root, never the action name repeated.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void IntegrationWriteConfirmationDialogsNameTheirTargetInsteadOfRepeatingTheActionName()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "src", "Forge.Desktop", "WorkspaceShellPage.ProjectSettings.cs"));

        Assert.Contains(
            "RootPrompt(root)",
            BracedBlockAfter(source, "install.Clicked += (_, _) => _ = RunAsync(async () =>"),
            StringComparison.Ordinal);
        Assert.Contains(
            "RootPrompt(root)",
            BracedBlockAfter(source, "remove.Clicked += (_, _) => _ = RunAsync(async () =>"),
            StringComparison.Ordinal);
    }

    /// <summary>Same reasoning as the checks above, applied proactively for `sprint.manage`'s
    /// `cancel` verb (ADR 0027) rather than waiting for a review round to catch it: cancelling a
    /// sprint is destructive, so its dialog must name the sprint it targets, not repeat the action
    /// name. Both cancel entry points (the sprint workspace and the project overview's own sprint
    /// card) must satisfy this.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void SprintCancelConfirmationDialogNamesItsTargetInsteadOfRepeatingTheActionName()
    {
        string source = DesktopSourceText();

        Assert.Contains("sprintWorkspace.SprintCancelPrompt(", source, StringComparison.Ordinal);
        Assert.Contains("projectOverview.SprintCancelPrompt(", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// PR #98 review round 1 finding 9: all five human-only gates (gate/supersede/confirm/test-work/
    /// finalize) passed a literal <c>true</c> for <c>confirmed</c> at their mutation call site
    /// instead of the dialog's own answer. Every one of them already returns before reaching that
    /// line when the dialog is declined, so there was no live bypass -- but the literal removed all
    /// local evidence the argument came from a real dialog, exactly the bug class this PR already
    /// had to fix once (see the ADR 0050 remarks on this file's own dialog-per-action discipline). No
    /// MAUI control can be instantiated headlessly in this suite (see the dialog-naming checks
    /// above), so this pins the fix directly in the source text: each call must pass its own local
    /// <c>confirmed</c>/<c>dialogConfirmed</c> variable, not the literal <c>true</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void HumanOnlyGatesPassTheDialogsOwnAnswerInsteadOfALiteralTrue()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "src", "Forge.Desktop", "WorkspaceShellPage.SprintWorkspace.cs"));

        Assert.Contains(
            ".ResolveGateAsync(root, sprintId, null, approved, confirmed, CancellationToken.None)",
            source, StringComparison.Ordinal);
        Assert.Contains(
            "attemptId.ToString(\"D\"), instructionEntry.Text, confirmed, CancellationToken.None)",
            source, StringComparison.Ordinal);
        Assert.Contains(
            "evidenceEntry.Text, dialogConfirmed, CancellationToken.None)",
            source, StringComparison.Ordinal);
        Assert.Contains(
            "outcome, justificationEntry.Text, dialogConfirmed,",
            source, StringComparison.Ordinal);
        Assert.Contains(
            ".FinalizeSprintAsync(root, sprintId, null, dialogConfirmed, CancellationToken.None)",
            source, StringComparison.Ordinal);
        // Slice 6's own two new destructive actions (stop, stage move) must pass the same real
        // dialog answer, never a literal true -- the exact bug class this file already had to fix
        // once for the five gates above.
        Assert.Contains(".StopAsync(root, fresh, confirmed, CancellationToken.None)", source, StringComparison.Ordinal);
        Assert.Contains(
            "isRewind ? rewindReasonEntry.Text : null, confirmed, CancellationToken.None)",
            source, StringComparison.Ordinal);
        // None of the mutation calls above may pass a literal `true` for the confirmation
        // argument -- every occurrence of `true` immediately before `CancellationToken.None)` in
        // this file must instead be one of the dialog-answer variable names.
        Assert.DoesNotContain(", true, CancellationToken.None)", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// PR #99 review finding 5: every gated action above (gate/supersede/confirm/test-work/finalize/
    /// stop/stage-move) aborts before touching the Host when its own confirmation dialog is declined
    /// -- except "cancel sprint," which used to call <c>CancelSprintAsync</c> unconditionally
    /// regardless of the dialog's answer. No MAUI control can be instantiated headlessly in this
    /// suite (see this file's other dialog checks), so this pins the fix the same way
    /// <see cref="ConfirmNodeRefusesBlankRequiredFieldsBeforeShowingTheConfirmationDialog"/> and
    /// <see cref="SupersedeAttemptRefusesABlankInstructionBeforeShowingTheConfirmationDialog"/> pin
    /// their own guards: the decline branch must appear, and it must run before the mutation call.
    /// </summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void CancelSprintAbortsBeforeMutatingWhenTheConfirmationDialogIsDeclined()
    {
        string method = SprintWorkspaceBody(
            "actions, AvailableActionProjector.CancelSprintActionId, text.Resolve(MessageKeys.SprintCancelAction),");

        int guardIndex = method.IndexOf("if (!dialogConfirmed)", StringComparison.Ordinal);
        int mutationIndex = method.IndexOf(".CancelSprintAsync(", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "The cancel-sprint handler no longer aborts on a declined confirmation.");
        Assert.True(mutationIndex >= 0, "The cancel-sprint handler no longer calls CancelSprintAsync.");
        Assert.True(guardIndex < mutationIndex, "The decline guard must run before the mutation call.");
    }

    /// <summary>
    /// PR #99 round-2 review, non-blocking: round-1 finding 1 fixed a dropped-click bug caused by
    /// routing the timeline poll's entire fetch-then-render step through the shell's shared mutation
    /// guard (<c>RunAsync</c>/<c>ShellRenderGate.RunAsync</c>), which held <c>busy</c> for the
    /// duration of an unattended Host round-trip and silently swallowed a user click landing in that
    /// window. The regression test for that fix, <c>ShellRenderGateTests.
    /// ARenderRequestDeferredDuringAnInFlightMutationNeverBlocksOrDropsAConcurrentMutation</c>,
    /// exercises <see cref="Forge.Desktop.Presentation.ShellRenderGate"/> directly and has no way to
    /// notice which caller actually invokes it, so a future edit could silently re-wrap the poll's
    /// tick or its fetch in <c>RunAsync</c> again -- reintroducing the exact bug -- with every
    /// existing test still green. No MAUI control can be instantiated headlessly in this suite (see
    /// this file's other dialog/source pins), so this pins the fix directly in the source text: the
    /// poll's own fetch method and the timer's tick handler must never call <c>RunAsync(</c>, the
    /// fetch must still call <c>LoadMoreAsync(</c> directly, and the render step must still go through
    /// <c>renderGate.RequestRender(</c>, never <c>RunAsync(</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void TimelinePollNeverRoutesThroughTheSharedMutationGate()
    {
        string pollMethod = SprintWorkspaceBody("async Task PollTimelineAsync()");
        string tickHandler = SprintWorkspaceBody("timelinePollTimer.Tick += (_, _) =>");

        Assert.DoesNotContain("RunAsync(", pollMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("RunAsync(", tickHandler, StringComparison.Ordinal);
        Assert.Contains(".LoadMoreAsync(", pollMethod, StringComparison.Ordinal);
        Assert.Contains("renderGate.RequestRender(", pollMethod, StringComparison.Ordinal);
    }

    /// <summary>PR #98 review round 1 finding 2: <c>LabeledRow</c> used to discard its label
    /// parameter entirely (<c>(string labelKeyIgnored, View control) =&gt; control</c>), so
    /// confirm-destructive and notifications-enabled shipped as bare switches with no visible
    /// caption. No MAUI control can be instantiated headlessly in this suite, so this pins the fix
    /// directly in the source: the helper must actually resolve and render the label.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void ForgeSettingsLabeledRowRendersItsLabelInsteadOfDiscardingIt()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "src", "Forge.Desktop", "WorkspaceShellPage.ForgeSettings.cs"));
        string method = BracedBlockAfter(source, "private HorizontalStackLayout LabeledRow(string labelKey, View control) =>");

        Assert.DoesNotContain("labelKeyIgnored", source, StringComparison.Ordinal);
        Assert.Contains("text.Resolve(labelKey)", method, StringComparison.Ordinal);
    }

    /// <summary>PR #98 review round 1 finding 10: plan 5.1 requires
    /// <c>interaction.confirm_destructive</c>'s row to carry a "mandatory-gate disclaimer" explaining
    /// that human/stop/rewind confirmations are never bypassed by this setting -- otherwise the
    /// toggle reads as a global "stop asking me" switch.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void ForgeSettingsConfirmDestructiveRowRendersItsMandatoryGateDisclaimer()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "src", "Forge.Desktop", "WorkspaceShellPage.ForgeSettings.cs"));

        Assert.Contains(
            "text.Resolve(MessageKeys.ForgeSettingsConfirmDestructiveDisclaimer)", source, StringComparison.Ordinal);
    }

    /// <summary>PR #98 review round 1 finding 7: <c>ProjectOverviewSnapshot</c> already computed
    /// <c>DisplayName</c>/<c>Root</c>/<c>Initialized</c>/<c>StartupReady</c>/<c>Providers</c>, but
    /// <c>RenderProjectOverviewAsync</c> rendered none of them, even though plan 4.2 and
    /// CHANGELOG.md's v0.67.0 entry both claim the overview shows startup/repository readiness and
    /// provider status.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void ProjectOverviewRendersReadinessAndProviderStatus()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "src", "Forge.Desktop", "WorkspaceShellPage.ProjectOverview.cs"));

        Assert.Contains("snapshot.DisplayName", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.Root", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.Initialized", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.StartupReady", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.Providers", source, StringComparison.Ordinal);
    }

    /// <summary>Plan section 11 Slice 6 item 3 ("remove manual ID fields from ordinary workflows"):
    /// the attempt id superseded is derived from the sprint's own current active attempt
    /// (<see cref="SprintWorkspaceViewModel.FindActiveAttemptId"/>), never typed into an
    /// <c>Entry</c> -- the exact raw-ID-entry field the old sprint-workspace page had here before
    /// this slice. No MAUI control can be instantiated headlessly (see the dialog-naming checks
    /// above), so this pins the fix directly in the source text.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void SupersedeNeverCollectsTheAttemptIdFromAManualEntry()
    {
        string source = DesktopSourceText();

        Assert.Contains("SprintWorkspaceViewModel.FindActiveAttemptId(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AttemptIdLabel", source, StringComparison.Ordinal);
    }

    /// <summary>Round 3 review: a blank replacement instruction was refused only after the user
    /// confirmed the irreversible action, asymmetric with the attempt-id guard immediately above and
    /// its own "ask before, not after" rationale.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void SupersedeAttemptRefusesABlankInstructionBeforeShowingTheConfirmationDialog()
    {
        string method = SupersedeAttemptClickHandlerBody();

        int guardIndex = method.IndexOf("instructionEntry.Text", StringComparison.Ordinal);
        int dialogIndex = method.IndexOf("DisplayAlertAsync(", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "The supersede handler no longer refuses a blank instruction.");
        Assert.True(
            guardIndex < dialogIndex, "The blank-instruction guard must run before the confirmation dialog.");
    }

    private static string SupersedeAttemptClickHandlerBody() =>
        SprintWorkspaceBody("supersede.Clicked += (_, _) => _ = RunAsync(async () =>");

    private static string SprintWorkspaceBody(string anchor)
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "src", "Forge.Desktop", "WorkspaceShellPage.SprintWorkspace.cs"));
        return BracedBlockAfter(source, anchor);
    }

    private static string BracedBlockAfter(string source, string anchor)
    {
        int start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Anchor text '{anchor}' was not found.");
        return BracedBlock(source, start);
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
    /// for the startup-checks section (`StartupChecksTitle`). Round 1 review of PR #80 found this
    /// section had the identical, previously-unfixed divergence the provider-health test just below
    /// closes: `forge doctor --startup` (`CliApplication.CreateDoctorCommand`) indents each row two
    /// spaces under its title; `MainPageViewModel.RefreshAsync`'s own rendering did not, until fixed
    /// alongside this test. `forge doctor --startup`'s own output interleaves the project-root/state
    /// lines *after* the checks (unlike Desktop, where they are separate `MainPageSnapshot` fields),
    /// so the checks section is bounded by the next known marker (`ProjectRootLabel`) rather than
    /// simply reading to the end of the CLI output the way the sibling parity tests do.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameStartupChecksForOneSnapshot()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter cliOutput = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);

        await CliApplication
            .CreateRootCommand(text, cliOutput, environment.Application, diagnostics)
            .Parse(["doctor", "--startup", "--project-root", environment.ProjectRoot])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);
        MainPageSnapshot desktop = await new MainPageViewModel(text, environment.Application)
            .RefreshAsync(environment.ProjectRoot, null, cancellationToken);

        string full = cliOutput.ToString();
        string title = text.Resolve(MessageKeys.StartupChecksTitle);
        string projectLabel = text.Resolve(MessageKeys.ProjectRootLabel);
        int start = full.IndexOf(title, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{title}' was not found in CLI output: {full}");
        int end = full.IndexOf(projectLabel, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"'{projectLabel}' was not found after the checks section: {full}");
        string cliSection = full[start..end].TrimEnd();

        Assert.Equal(cliSection, desktop.StartupChecksText);
        // The comparison above is only as strong as its fixture -- pin that the compared text
        // really carries a real check row, not an empty section both sides would trivially agree on.
        Assert.Contains("user_configuration", desktop.StartupChecksText, StringComparison.Ordinal);
    }

    /// <summary>Same no-drift proof as <see cref="DesktopAndCliRenderTheSameEventsForOneSnapshot"/>,
    /// for the provider-health section (`ProviderToolchainTitle`). Stage 12's P12.16-P12.32 audit
    /// (this codebase's own security/robustness test sweep) found this section had never been
    /// pinned to literal equality the way the tree/events/integration sections already were --
    /// exposing a real, if cosmetic, divergence: `forge models` (`CliApplication.CreateModelsCommand`)
    /// indents each provider row two spaces under its title; `MainPageViewModel.RefreshAsync`'s own
    /// rendering did not. Fixed alongside this test, not discovered by it after the fact -- matching
    /// AGENTS.md's "confirm through inspection... before authoring new tests" ordering.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameProvidersForOneSnapshot()
    {
        // A default TestEnvironment's toolchain manager reports codex/claude_code as Missing
        // (FakeProviderToolchainManager.NotReady), which is real, illustrative-but-unhealthy default
        // behavior (matching StartupCliTests's own "providers blocked provider_preflight_pending"
        // fixture) but not what this test needs to prove -- an explicit Ready fixture keeps the
        // comparison about rendering, not about an incidentally-nonzero exit code neither surface's
        // own rendering path is responsible for.
        using TestEnvironment environment = new(
            providers: new FakeProviderToolchainManager(FakeProviderToolchainManager.Ready));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter cliOutput = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);

        // `forge models` has no `--project-root` -- provider toolchain health is a per-user concept
        // (which CLIs are installed), not a per-project one.
        Assert.Equal(0, await CliApplication
            .CreateRootCommand(text, cliOutput, environment.Application, diagnostics)
            .Parse(["models"])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));
        MainPageSnapshot desktop = await new MainPageViewModel(text, environment.Application)
            .RefreshAsync(environment.ProjectRoot, null, cancellationToken);

        Assert.Equal(cliOutput.ToString().TrimEnd(), desktop.ProvidersText);
        Assert.Empty(diagnostics.ToString());
        // The comparison above is only as strong as its fixture -- pin that the compared text
        // really carries a real provider row, not an empty section both sides would trivially agree
        // on regardless of whether rendering actually matches.
        Assert.Contains("codex", desktop.ProvidersText, StringComparison.Ordinal);
    }

    /// <summary>Same no-drift proof as <see cref="DesktopAndCliRenderTheSameEventsForOneSnapshot"/>,
    /// for the suggested-actions section (`SuggestedActionsTitle`). Round 2 review of PR #80 found
    /// this section had the identical, previously-unfixed indent divergence from `forge status`'s
    /// own `WriteActions` (`CliApplication.cs`) as the startup-checks/provider sections already
    /// fixed. `WriteActions` is the last thing `forge status` writes, so the section is everything
    /// from its own title to the end of output -- the same "read to end" shape the sibling parity
    /// tests already use, unlike the startup-checks test's own bounded slice.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameSuggestedActionsForOneSnapshot()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter cliOutput = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);

        // An uninitialized project always suggests `initialize_project` -- a real, non-empty
        // fixture without needing any sprint/provider setup.
        await CliApplication
            .CreateRootCommand(text, cliOutput, environment.Application, diagnostics)
            .Parse(["status", "--project-root", environment.ProjectRoot])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);
        MainPageSnapshot desktop = await new MainPageViewModel(text, environment.Application)
            .RefreshAsync(environment.ProjectRoot, null, cancellationToken);

        string title = text.Resolve(MessageKeys.SuggestedActionsTitle);
        string full = cliOutput.ToString();
        int start = full.IndexOf(title, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{title}' was not found in CLI output: {full}");
        string cliSection = full[start..].TrimEnd();

        Assert.Equal(cliSection, desktop.SuggestedActionsText);
        Assert.Contains("initialize_project", desktop.SuggestedActionsText, StringComparison.Ordinal);
    }

    /// <summary>Same no-drift proof as <see cref="DesktopAndCliRenderTheSameEventsForOneSnapshot"/>,
    /// for the configuration-values section. Round 2 review of PR #80 found the same indent
    /// divergence from `forge config show`'s own `WriteValues` (`CliApplication.cs`). Unlike every
    /// other section here, Desktop's own `Render(null, ...)` call deliberately excludes the title --
    /// `ConfigurationTitleLabel` in `MainPage.xaml.cs`'s constructor already renders
    /// `MessageKeys.ConfigurationTitle` as its own static label, not part of this scrollable text --
    /// so the CLI comparison text is sliced to start right after its own title line instead of
    /// including it.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameConfigurationValuesForOneSnapshot()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter cliOutput = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);

        await CliApplication
            .CreateRootCommand(text, cliOutput, environment.Application, diagnostics)
            .Parse(["config", "show", "--project-root", environment.ProjectRoot])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);
        MainPageSnapshot desktop = await new MainPageViewModel(text, environment.Application)
            .RefreshAsync(environment.ProjectRoot, null, cancellationToken);

        string title = text.Resolve(MessageKeys.ConfigurationTitle);
        string full = cliOutput.ToString();
        int titleIndex = full.IndexOf(title, StringComparison.Ordinal);
        Assert.True(titleIndex >= 0, $"'{title}' was not found in CLI output: {full}");
        string cliSection = full[(titleIndex + title.Length)..].TrimStart('\r', '\n').TrimEnd();

        Assert.Equal(cliSection, desktop.ConfigurationText);
        // The comparison above is only as strong as its fixture -- pin that real content is
        // actually compared, not two empty sections that would trivially agree either way.
        Assert.NotEmpty(desktop.ConfigurationText);
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
