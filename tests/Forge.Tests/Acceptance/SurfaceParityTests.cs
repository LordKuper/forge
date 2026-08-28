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

    /// <summary>Same gap-closing purpose as <see cref="WorkspaceSummaryDocumentedCliOptionsMatchTheirActualRequiredness"/>,
    /// for `provider.quota_status` (Slice 7, ADR 0043/0052).</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void ProviderQuotaStatusDocumentedCliOptionsMatchTheirActualRequiredness()
    {
        using TestEnvironment environment = new();
        RootCommand root = CliApplication.CreateRootCommand(
            new SurfaceText(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture),
            new StringWriter(CultureInfo.InvariantCulture),
            environment.Application);

        string cli = ReadCli("provider.quota_status");
        string[] tokens = cli.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Command models = Assert.Single(root.Subcommands, subcommand => subcommand.Name == tokens[1]);
        Command quota = Assert.Single(models.Subcommands, subcommand => subcommand.Name == "quota");

        Assert.All(
            ParseDocumentedOptionRequiredness(cli),
            entry =>
            {
                Option? actual = FindOptionRecursively(quota, entry.Option);
                Assert.True(actual is not null, $"'quota' does not expose documented option '{entry.Option}'.");
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

        // ADR 0064: the node-id slot moved from a hardcoded `null` to `ResolveGateAsync`'s own
        // `nodeId` parameter, so the inline timeline gate card can name the exact gate node.
        // ContextualActionHost's own call keeps passing nothing -- not because it can't name a node
        // (`currentDetails.Nodes` is in scope there too), but because its single approve/reject pair
        // renders exactly one gate at a time; naming a node there is only correct while at most one
        // is pending, and doing it properly means per-gate rendering (finding A2), deferred on
        // purpose. Only the node-id slot changed here -- the property this assertion exists for,
        // that `confirmed` is the dialog's own answer rather than a literal `true`, is untouched.
        Assert.Contains(
            ".ResolveGateAsync(root, sprintId, nodeId, approved, confirmed, CancellationToken.None)",
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
        // PR #101 review finding 3: the reason argument now also excludes a rewind-in-progress resume
        // (which reuses the reason already recorded when the rewind first committed), but `confirmed`
        // itself must still be the dialog's own answer here too.
        Assert.Contains(
            "isRewind && !isRewindInProgress ? rewindReasonEntry.Text : null,",
            source, StringComparison.Ordinal);
        Assert.Contains(
            "confirmed, CancellationToken.None)\n                .ConfigureAwait(true);\n            await RefreshAllAsync",
            source, StringComparison.Ordinal);
        // None of the mutation calls above may pass a literal `true` for the confirmation
        // argument -- every occurrence of `true` immediately before `CancellationToken.None)` in
        // this file must instead be one of the dialog-answer variable names.
        Assert.DoesNotContain(", true, CancellationToken.None)", source, StringComparison.Ordinal);
    }

    /// <summary>PR #105 review finding 3: a 4.0 device-independent-unit DISTANCE gate was not a
    /// throttle -- a single mouse-wheel notch exceeds it, so essentially every scroll event issued a
    /// full <c>catalog.json</c> read-modify-write with no rest detection at all. The fix replaces it
    /// with a TIME-based debounce: a single-shot <c>IDispatcherTimer</c> (<c>IsRepeating = false</c>)
    /// restarted (<c>Stop</c> then <c>Start</c>) on every <c>ScrollView.Scrolled</c> event, so only
    /// the position at rest -- once the timer is finally left alone long enough to fire -- is ever
    /// written. No MAUI control can be instantiated headlessly in this suite, so this pins the
    /// mechanism directly in the source: the old distance-gate constant must be gone, and the
    /// debounce restart must appear inside the <c>Scrolled</c> handler itself.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void ScrollPositionPersistenceDebouncesByElapsedTimeNotScrollDistance()
    {
        string source = DesktopSourceText();

        Assert.DoesNotContain("ScrollPositionPersistThreshold", source, StringComparison.Ordinal);
        Assert.Contains("scrollPersistDebounceTimer.IsRepeating = false;", source, StringComparison.Ordinal);

        string scrolledHandler = BracedBlockAfter(source, "scrollView.Scrolled += (_, args) =>");
        Assert.Contains("scrollPersistCoordinator.RecordScroll(", scrolledHandler, StringComparison.Ordinal);
        Assert.Contains("scrollPersistDebounceTimer.Stop();", scrolledHandler, StringComparison.Ordinal);
        Assert.Contains("scrollPersistDebounceTimer.Start();", scrolledHandler, StringComparison.Ordinal);
    }

    /// <summary>PR #105 round-1 review finding 4(c): the debounced scroll-position write used to be
    /// fire-and-forget with its <c>ProjectCatalogResult</c> silently discarded -- the only catalog/
    /// config write in this shell with no failure notice at all, unlike every sibling write (e.g.
    /// <c>SidebarProjectSprintsSaveFailed</c>). That round-1 fix only asserted the message key was
    /// referenced somewhere in the file -- it did not prove the notice ever reached a user. Round-2
    /// finding 3 caught exactly that gap: the notice was routed through a content-host-scoped label
    /// reachable only via <c>ShellRenderGate.RequestRender</c>, which is unreachable on the
    /// navigate-away/page-close paths (<c>RenderContentAsync</c> clears <c>ContentHost</c> and
    /// rebuilds the destination route before a render deferred while the mutation guard was held ever
    /// runs, and the very next sprint-workspace render resets the label back to empty before it could
    /// ever be seen). No MAUI control can be instantiated headlessly in this suite, so this pins the
    /// fix's actual routing rather than just the message key: the notice goes through
    /// <c>sidebarNotice</c> -- this shell's own established "notice that survives a content rebuild"
    /// precedent (see that field's remarks, PR #98/#103 review finding 3/1) -- via
    /// <c>RequestSidebarRender</c>, guarded so a successful/no-op flush never touches it, and never
    /// through the content-only <c>RequestRender</c> that <c>PollTimelineAsync</c>'s own timeline
    /// refresh already owns (round-2 finding 4's collision).</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void AFailedScrollPositionWriteSurfacesANoticeInsteadOfBeingDiscarded()
    {
        string method =
            SprintWorkspaceBody("private async Task FlushScrollPositionAsync(Guid projectId, Guid sprintId)");

        Assert.Contains("MessageKeys.SprintScrollPositionSaveFailed", method, StringComparison.Ordinal);

        Assert.Contains("renderGate.RequestSidebarRender();", method, StringComparison.Ordinal);

        int noSuccessGuardIndex =
            method.IndexOf("if (!outcome.Applied || outcome.Succeeded)", StringComparison.Ordinal);
        int noticeIndex = method.IndexOf("sidebarNotice = Message(", StringComparison.Ordinal);
        Assert.True(noSuccessGuardIndex >= 0, "A successful/no-op flush must return before reporting anything.");
        Assert.True(noticeIndex >= 0, "The failure notice must be routed through sidebarNotice.");
        Assert.True(
            noSuccessGuardIndex < noticeIndex,
            "The success/no-op guard must run before the notice is ever set, so a routine flush never reports anything.");

        // Never routed through the content-only slot PollTimelineAsync's own render request already
        // owns (round-2 finding 4) -- a content-host-scoped label was also round-2 finding 3's own
        // unreachability bug.
        Assert.DoesNotContain("renderGate.RequestRender(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("scrollPersistNoticeLabel", method, StringComparison.Ordinal);
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

    /// <summary>
    /// PR #103 review finding 3: the sidebar collapse/expand toggle used to re-render by calling the
    /// full <c>RenderSidebarAsync</c>, which drives <c>SidebarViewModel.LoadAsync</c>'s per-project
    /// workspace-summary refetch and a configuration read -- work a purely cosmetic width change
    /// needs none of -- while holding <c>ShellRenderGate.busy</c> for the whole round-trip. The exact
    /// "unnecessary work holding the mutation guard" pattern PR #99 review finding 1 and PR #100
    /// review finding 1 already pushed back on in this same surface (see
    /// <see cref="TimelinePollNeverRoutesThroughTheSharedMutationGate"/> and
    /// <c>SidebarViewModelTests.LoadAsyncNeverIssuesASecondToolchainProbeToComputeTheQuotaRow</c> for
    /// those fixes' own regression coverage). Unlike those two, this one is not reachable from a
    /// neutral view-model call-count assertion: the decision to skip the reload lives entirely in the
    /// toggle's own code-behind click handler, and no MAUI control can be instantiated headlessly in
    /// this suite (see this file's other dialog/source pins), so this pins the fix directly in the
    /// source text instead -- the handler must never call the full-reload <c>RenderSidebarAsync(</c>,
    /// only the cheap, already-loaded-snapshot <c>RenderSidebarFromSnapshot(</c> rebuild.
    /// </summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void SidebarToggleNeverTriggersAFullSidebarReload()
    {
        string handler = BracedBlockAfter(
            DesktopSourceText(), "toggle.Clicked += (_, _) => _ = RunAsync(async () =>");

        Assert.DoesNotContain("RenderSidebarAsync(", handler, StringComparison.Ordinal);
        Assert.Contains("RenderSidebarFromSnapshot(", handler, StringComparison.Ordinal);
        Assert.Contains(".SetCollapsedAsync(", handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// PR #103 review, iteration 2 (thread discussion_r3843212692): the finding-1 fix (surfacing a
    /// failed collapse/expand write via <c>sidebarNotice</c>) worked only for the collapse direction.
    /// Two defects combined to strand the user for the expand direction specifically: (1)
    /// <c>RenderSidebarFromSnapshot</c> rendered <c>sidebarNotice</c> only after its collapsed-state
    /// early return, so a notice set by a failed *expand* write -- which leaves the rail collapsed --
    /// was built and then discarded before it could ever appear; (2) the toggle's <c>Clicked</c>
    /// handler rolled the visible state back to <c>collapsed</c> on any failed write, so a failed
    /// expand attempt re-entered the one layout whose only control is this same toggle, and since the
    /// write kept failing for the same durable reason (a locked/read-only/full <c>config.json</c>),
    /// every retry reproduced the identical silent lockout with no in-app way back to an expanded
    /// sidebar. No MAUI control can be instantiated headlessly in this suite (see this file's other
    /// dialog/source pins), so this pins both parts of the fix directly in the source text: the
    /// notice must render before the collapsed-state early return (visible in both layouts), and the
    /// click handler must always apply the requested state rather than rolling it back on failure.
    /// </summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void SidebarCollapseToggleNeverStrandsTheUserInACollapsedRailAfterAFailedWrite()
    {
        string source = DesktopSourceText();
        string renderMethod =
            BracedBlockAfter(source, "private void RenderSidebarFromSnapshot(SidebarSnapshot snapshot)");
        string toggleHandler = BracedBlockAfter(source, "toggle.Clicked += (_, _) => _ = RunAsync(async () =>");

        int noticeIndex = renderMethod.IndexOf(
            "Add(Describe(new Label { Text = sidebarNotice }))", StringComparison.Ordinal);
        int collapsedReturnIndex = renderMethod.IndexOf("if (snapshot.Collapsed)", StringComparison.Ordinal);
        Assert.True(noticeIndex >= 0, "sidebarNotice must still be rendered inside RenderSidebarFromSnapshot.");
        Assert.True(collapsedReturnIndex >= 0, "The collapsed-state early return must still exist.");
        Assert.True(
            noticeIndex < collapsedReturnIndex,
            "sidebarNotice must render before the collapsed-state early return, or a notice set by a " +
            "failed expand attempt is discarded before it can ever be shown.");

        Assert.Contains("bool nowCollapsed = !collapsed;", toggleHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("nowCollapsed = collapsed;", toggleHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("if (result.Succeeded)", toggleHandler, StringComparison.Ordinal);
    }

    /// <summary>Finding B2 (docs/plans/desktop-design-parity-review.md): the sidebar's
    /// highlighted-row treatment (tinted background plus accent border) was driven by
    /// <c>SidebarSprintItem.HasActiveOperation</c>, so the row the user was actually looking at got
    /// only a text tint while an unrelated busy sprint owned the highlight -- and with no sprint
    /// running, nothing in the rail showed where the shell was routed. The highlight now follows the
    /// selected route. No MAUI control can be instantiated headlessly in this suite (see this file's
    /// other dialog/source pins), so this pins the fix directly in the source text. It also pins the
    /// other half of the change: <c>HasActiveOperation</c> is a strictly narrower fact than the state
    /// text (a live, non-stop-requested attempt is executing right now), so it was retargeted onto the
    /// row's own status dot rather than deleted along with the highlight it used to drive.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void SidebarRowHighlightFollowsTheSelectedRouteInsteadOfAnActiveOperation()
    {
        string source = DesktopSourceText();
        string container = BracedBlockAfter(
            source, "private static Border SidebarSelectableRow(View content, bool isSelected) =>");
        string sprintRow = BracedBlockAfter(
            source, "private Border BuildSprintRow(SidebarProjectItem project, SidebarSprintItem sprint)");

        Assert.Contains(
            "BackgroundColor = isSelected ? ThemeColor(\"ColorAccent900\") : Colors.Transparent",
            container, StringComparison.Ordinal);
        Assert.Contains(
            "Stroke = isSelected ? ThemeColor(\"ColorAccent\") : Colors.Transparent",
            container, StringComparison.Ordinal);
        Assert.Contains("SidebarSelectableRow(row, isSelected)", sprintRow, StringComparison.Ordinal);

        // The pre-fix shape, gone from the whole shell: nothing tints a row's ground or draws its
        // border from HasActiveOperation any more.
        Assert.DoesNotContain(
            "HasActiveOperation ? ThemeColor(\"ColorAccent900\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HasActiveOperation ? ThemeColor(\"ColorAccent\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderWidth = sprint.HasActiveOperation", source, StringComparison.Ordinal);

        // ...and the signal it used to carry is preserved, not dropped.
        Assert.Contains(
            "Text = sprint.HasActiveOperation ? ActiveOperationDot : IdleOperationDot",
            sprintRow, StringComparison.Ordinal);
    }

    /// <summary>PR #122 review round 2 finding 1: an archived row rendered its title alone, leaving
    /// <c>SidebarRowAccentColor</c>'s green-versus-neutral tint as the only thing separating a
    /// completed sprint from a cancelled one -- and only once the row was selected, since an
    /// unselected row paints every state the same ink. That is status by colour alone, which plan
    /// 12.6 forbids outright. The same holds for the active row's <c>state · n/m</c> line: the rail
    /// carries the progress fraction nowhere else. Both lines are the ONLY rail-visible carriers of
    /// their fact, so both must exist as text.
    ///
    /// Both are also deliberately <c>Decorative(...)</c>-excluded rather than described: the row's
    /// single focusable control already speaks the same state (and progress) through
    /// <c>ToSprintItem</c>/<c>ToHistoryItem</c>'s accessible name, so describing the label too would
    /// announce it twice -- PR #112 review round 2 finding 4's rule. Restoring the word as a second
    /// screen-reader stop would therefore be the wrong repair, which is why the exclusion is pinned
    /// alongside the presence.
    ///
    /// No MAUI control is instantiable headlessly in this suite (see this file's other source pins),
    /// so this pins both lines in the source text, inside their own brace-matched method bodies.
    /// </summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void SidebarRowsCarryTheirStateAndProgressAsDecorativeTextNotColourAlone()
    {
        string source = DesktopSourceText();
        string sprintRow = BracedBlockAfter(
            source, "private Border BuildSprintRow(SidebarProjectItem project, SidebarSprintItem sprint)");
        string historyRow = BracedBlockAfter(
            source, "private Border BuildHistoryRow(SidebarProjectItem project, SidebarHistoryItem historyItem)");

        AssertDecorativeLabelText(
            historyRow, "Text = historyItem.StateText", "the archived row's state word");
        AssertDecorativeLabelText(
            sprintRow,
            "$\"{sprint.StateText} · {sprint.StagesCompleted}/{sprint.StagesTotal}\"",
            "the active row's state and progress fraction");
    }

    /// <summary>Asserts that <paramref name="labelText"/> is rendered by a <c>Label</c> whose
    /// nearest enclosing construction is a <c>Decorative(...)</c> wrap -- proven by nearest-preceding
    /// <c>new Label</c> rather than by line positions, so reformatting cannot silently pass it.
    /// </summary>
    private static void AssertDecorativeLabelText(string methodBody, string labelText, string what)
    {
        int textIndex = methodBody.IndexOf(labelText, StringComparison.Ordinal);
        Assert.True(
            textIndex >= 0,
            $"{what} is not rendered as text at all; colour would be its only carrier (plan 12.6).");

        string beforeText = methodBody[..textIndex];
        int nearestLabel = beforeText.LastIndexOf("new Label", StringComparison.Ordinal);
        Assert.True(nearestLabel >= 0, $"{what} is not carried by a Label.");
        Assert.True(
            nearestLabel >= "Decorative(".Length
                && beforeText.AsSpan(nearestLabel - "Decorative(".Length, "Decorative(".Length)
                    .SequenceEqual("Decorative("),
            $"{what}'s Label must be Decorative(...)-excluded: the row's button already speaks the "
                + "same fact, so describing the label would announce it twice.");
    }

    /// <summary>PR #122 review round 3 finding 1. Both sidebar row labels draw
    /// <c>SidebarSprintItem.DisplayTitle</c>, which leads with the <c>"(Sprint N)"</c> ordinal that
    /// keeps two same-titled sprints distinguishable, into a fixed-width rail that a 200-character
    /// frozen title (ADR 0057) routinely overruns.
    ///
    /// Only <c>TailTruncation</c> keeps that leading ordinal. An earlier revision asked for
    /// <c>MiddleTruncation</c> with a TRAILING ordinal instead, which cannot work on the only
    /// platform this app ships to: <c>Forge.Desktop</c> targets <c>net10.0-windows10.0.19041.0</c>
    /// only, and MAUI's <c>TextBlockExtensions.SetLineBreakMode</c> maps both <c>HeadTruncation</c>
    /// and <c>MiddleTruncation</c> onto WinUI's <c>TextTrimming.WordEllipsis</c> -- WinUI has no
    /// head or middle form, and <c>WordEllipsis</c> trims from the END at a word boundary. Either
    /// mode is therefore a coarser tail trim that would drop a leading ordinal's title tail one
    /// whole word at a time, and would have dropped a trailing ordinal outright.
    ///
    /// Scoped to these two method bodies rather than the whole shell: other surfaces draw strings
    /// with no truncation-sensitive anchor and are free to choose their own mode.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void SidebarRowLabelsTruncateAtTheTailWhereWinUiActuallyTruncates()
    {
        string source = DesktopSourceText();

        foreach (string anchor in new[]
                 {
                     "private Border BuildSprintRow(SidebarProjectItem project, SidebarSprintItem sprint)",
                     "private Border BuildHistoryRow(SidebarProjectItem project, SidebarHistoryItem historyItem)",
                 })
        {
            string body = BracedBlockAfter(source, anchor);
            Assert.Contains("LineBreakMode = LineBreakMode.TailTruncation", body, StringComparison.Ordinal);
            Assert.DoesNotContain("LineBreakMode.MiddleTruncation", body, StringComparison.Ordinal);
            Assert.DoesNotContain("LineBreakMode.HeadTruncation", body, StringComparison.Ordinal);
        }
    }

    /// <summary>PR #105 review finding 2: the per-project chevron's own accessible name promises
    /// "Collapse sprints" (the whole per-project block), but the fix that shipped in that PR gated
    /// only the active-sprint loop on <c>project.SprintListExpanded</c> -- the history label and its
    /// (up to 10) navigable buttons rendered unconditionally underneath, so a collapsed project could
    /// still show more sprint rows than it hid. No MAUI control can be instantiated headlessly in
    /// this suite, so this pins the fix directly in the source: both the active-sprint loop AND the
    /// history block must sit inside the SAME <c>if (project.SprintListExpanded)</c> braced block,
    /// proven by brace-matching (<see cref="BracedBlockAfter"/>) rather than a line-position guess
    /// that formatting could invalidate.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void CollapsingAProjectsSprintListHidesBothActiveSprintsAndHistory()
    {
        string expandedBlock = BracedBlockAfter(DesktopSourceText(), "if (project.SprintListExpanded)");

        Assert.Contains(
            "foreach (SidebarSprintItem sprint in project.ActiveSprints)", expandedBlock, StringComparison.Ordinal);
        Assert.Contains(
            "foreach (SidebarHistoryItem historyItem in project.History)", expandedBlock, StringComparison.Ordinal);
    }

    /// <summary>PR #105 review finding 1: the sidebar's "History (n)" label used to read
    /// <c>project.History.Count</c> -- capped at <see cref="SidebarViewModel.MaxSidebarHistory"/> --
    /// instead of the uncapped total, silently under-reporting for any project with more than 10
    /// terminal sprints. <see cref="SidebarViewModelTests.LoadAsyncCapsHistoryAtTheDocumentedBoundOrderedNewestFirst"/>
    /// proves <c>SidebarProjectItem.HistoryTotalCount</c> itself carries the true total; this pins the
    /// desktop label actually reading that field instead of the capped list length (no MAUI control
    /// can be instantiated headlessly in this suite).</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void SidebarHistoryLabelReadsTheUncappedTotalNotTheCappedListLength()
    {
        string source = DesktopSourceText();

        Assert.Contains(
            "$\"  {text.Resolve(MessageKeys.SidebarHistoryLabel)} ({project.HistoryTotalCount})\"",
            source, StringComparison.Ordinal);
        Assert.DoesNotContain("SidebarHistoryLabel)} ({project.History.Count})", source, StringComparison.Ordinal);
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
            .CreateSprintAsync(desktopEnvironment.ProjectRoot, null, cancellationToken);

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

    private static readonly string SampleDigest = "sha256:" + new string('0', 64);

    /// <summary>Plan ~643-649's Known-gaps bucket 3 sub-item: `forge attempt stop`
    /// (<c>CliApplication.CreateAttemptStopCommand</c>) and <see cref="SprintActionsViewModel.StopAsync"/>
    /// both resolve to the same fixed success text (<c>MessageKeys.AttemptStopped</c>) -- unlike
    /// stage assessment (below), stop's success shape is a single fixed sentence on both surfaces, so
    /// this compares literal output exactly like <see cref="DesktopAndCliRenderTheSameSprintCancelMessageForOneSnapshot"/>
    /// does for cancel's own fixed text.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameStopResultForOneSnapshot()
    {
        using TestEnvironment cliEnvironment = new();
        using TestEnvironment desktopEnvironment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (SprintId cliSprintId, Guid cliAttemptId) = await CreateRunningAttemptAsync(cliEnvironment, cancellationToken);
        (SprintId desktopSprintId, _) = await CreateRunningAttemptAsync(desktopEnvironment, cancellationToken);
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter cliOutput = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);

        Assert.Equal(0, await CliApplication
            .CreateRootCommand(
                text, cliOutput, cliEnvironment.Application, diagnostics, isInteractive: () => true)
            .Parse([
                "attempt", "stop", cliAttemptId.ToString(), "--sprint", cliSprintId.Value.ToString(), "--yes",
                "--project-root", cliEnvironment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));

        SprintActionsViewModel desktopActions = new(
            desktopEnvironment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(desktopEnvironment.Application),
            text);
        IReadOnlyList<AvailableAction> actions = await desktopActions
            .LoadAsync(desktopEnvironment.ProjectRoot, desktopSprintId.Value, cancellationToken);
        AvailableAction stopAction = Assert.Single(
            actions, action => action.ActionId == AvailableActionProjector.StopCurrentOperationActionId);
        string desktop = await desktopActions
            .StopAsync(desktopEnvironment.ProjectRoot, stopAction, true, cancellationToken);

        Assert.Equal(cliOutput.ToString().TrimEnd(), desktop);
        Assert.Empty(diagnostics.ToString());
        Assert.Equal(text.Resolve(MessageKeys.AttemptStopped), desktop);
    }

    /// <summary>Plan ~643-649's Known-gaps bucket 3 sub-item: `forge sprint move-stage`
    /// (<c>CliApplication.CreateSprintMoveStageCommand</c>) and <see cref="SprintActionsViewModel.MoveAsync"/>
    /// both resolve to the same fixed success text (<c>MessageKeys.SprintStageMoved</c>) on a
    /// successful Advance -- compared literally, matching the stop/cancel/resume message tests'
    /// established shape.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameStageMoveResultForOneSnapshot()
    {
        using TestEnvironment cliEnvironment = new();
        using TestEnvironment desktopEnvironment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId cliSprintId = await CreateReadyToAdvanceSprintAsync(cliEnvironment, cancellationToken);
        SprintId desktopSprintId = await CreateReadyToAdvanceSprintAsync(desktopEnvironment, cancellationToken);
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter cliOutput = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);

        Assert.Equal(0, await CliApplication
            .CreateRootCommand(
                text, cliOutput, cliEnvironment.Application, diagnostics, isInteractive: () => true)
            .Parse([
                "sprint", "move-stage", cliSprintId.Value.ToString(), "--target-stage", "b", "--yes",
                "--project-root", cliEnvironment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));

        SprintActionsViewModel desktopActions = new(
            desktopEnvironment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(desktopEnvironment.Application),
            text);
        StageTransitionAssessment assessment = await desktopActions
            .AssessMoveAsync(desktopEnvironment.ProjectRoot, desktopSprintId.Value, "b", cancellationToken);
        string desktop = await desktopActions
            .MoveAsync(desktopEnvironment.ProjectRoot, desktopSprintId.Value, assessment, null, true, cancellationToken);

        Assert.Equal(cliOutput.ToString().TrimEnd(), desktop);
        Assert.Empty(diagnostics.ToString());
        Assert.Equal(text.Resolve(MessageKeys.SprintStageMoved), desktop);
    }

    /// <summary>Plan ~643-649's Known-gaps bucket 3 sub-item, for `forge sprint assess-stage`
    /// (<c>CliApplication.CreateSprintAssessStageCommand</c>) vs. <see cref="SprintActionsViewModel.MovePrompt"/>.
    /// Unlike stop/move-stage's own fixed success sentence, these two renderings are deliberately
    /// different *shapes* by design -- the CLI's is a compact, single-purpose query line
    /// (<c>"{source} -&gt; {target}: {Direction}, allowed={Allowed}"</c> plus one <c>"  blocked: ..."</c>
    /// line per unsatisfied prerequisite) while Desktop's is a labeled, human-facing confirmation
    /// prompt (source/target/direction/satisfied/unsatisfied/consequences lines) -- so a literal
    /// string-equality assertion would fail by construction, not by drift, and would test something
    /// false about the agreed behavior. Both are still built from the exact same
    /// <see cref="StageTransitionAssessment"/> (`AssessStageTransitionAsync`), so "semantically
    /// identical" here means every fact the CLI's own text reports -- source, target, direction,
    /// allowed, and each unsatisfied prerequisite's id/message-key pair -- must also be present,
    /// unaltered, in Desktop's own rendering. The fixture pins a genuinely mixed result (one
    /// satisfied predecessor, one unsatisfied `NoBlockingFindings`) so both the "allowed" and
    /// "blocked" halves of this comparison are exercised, not trivially vacuous.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DesktopAndCliRenderTheSameStageAssessmentSemanticsForOneSnapshot()
    {
        using TestEnvironment cliEnvironment = new();
        using TestEnvironment desktopEnvironment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SprintId cliSprintId = await CreateAdvanceBlockedByAnOpenFindingSprintAsync(cliEnvironment, cancellationToken);
        SprintId desktopSprintId =
            await CreateAdvanceBlockedByAnOpenFindingSprintAsync(desktopEnvironment, cancellationToken);
        SurfaceText text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
        StringWriter cliOutput = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);

        Assert.Equal(0, await CliApplication
            .CreateRootCommand(text, cliOutput, cliEnvironment.Application, diagnostics)
            .Parse([
                "sprint", "assess-stage", cliSprintId.Value.ToString(), "--target-stage", "b",
                "--project-root", cliEnvironment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken));

        SprintActionsViewModel desktopActions = new(
            desktopEnvironment.Application,
            (_, _) => Task.FromResult<IForgeMutations>(desktopEnvironment.Application),
            text);
        StageTransitionAssessment desktopAssessment = await desktopActions
            .AssessMoveAsync(desktopEnvironment.ProjectRoot, desktopSprintId.Value, "b", cancellationToken);
        string desktopPrompt = desktopActions.MovePrompt(desktopAssessment);

        string[] lines = cliOutput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r')).ToArray();
        Match summary = Regex.Match(
            lines[0], @"^(?<source>\S+) -> (?<target>\S+): (?<direction>\w+), allowed=(?<allowed>True|False)$");
        Assert.True(summary.Success, $"Could not parse assess-stage summary line: '{lines[0]}'");
        string cliSource = summary.Groups["source"].Value;
        string cliTarget = summary.Groups["target"].Value;
        StageTransitionDirection cliDirection =
            Enum.Parse<StageTransitionDirection>(summary.Groups["direction"].Value);
        bool cliAllowed = bool.Parse(summary.Groups["allowed"].Value);
        List<(string Id, string MessageKey)> cliBlocked = [];
        foreach (string line in lines.Skip(1))
        {
            Match blocked = Regex.Match(line, @"^\s*blocked: (?<id>\S+) \((?<key>\S+)\)$");
            if (blocked.Success)
            {
                cliBlocked.Add((blocked.Groups["id"].Value, blocked.Groups["key"].Value));
            }
        }

        // Pins that the fixture actually exercises both the "allowed" determination and at least one
        // unsatisfied prerequisite -- otherwise this comparison would trivially pass regardless of
        // whether either surface actually surfaces a blocker.
        Assert.False(cliAllowed);
        Assert.NotEmpty(cliBlocked);

        Assert.Equal(cliSource, desktopAssessment.SourceStageId);
        Assert.Equal(cliTarget, desktopAssessment.TargetStageId);
        Assert.Equal(cliDirection, desktopAssessment.Direction);
        Assert.Equal(cliAllowed, desktopAssessment.Allowed);
        // Round 2 review of PR #109: bare `Assert.Contains(cliSource/cliTarget, ...)` is vacuous with
        // this fixture's single-character stage ids -- "a" matches inside "Current st**a**ge" and "b"
        // matches inside "**b**locked"/"**b**udget" regardless of whether MovePrompt ever interpolates
        // the actual source/target id at all. Asserting the labelled line instead pins this to the
        // real semantic-parity claim -- it fails if the id is ever dropped from Desktop's rendering.
        Assert.Contains(
            $"{text.Resolve(MessageKeys.MoveToStageSourceLabel)} {cliSource}", desktopPrompt, StringComparison.Ordinal);
        Assert.Contains(
            $"{text.Resolve(MessageKeys.MoveToStageTargetLabel)} {cliTarget}", desktopPrompt, StringComparison.Ordinal);
        // SurfaceFormatting.Machine is the exact shared production formatter MovePrompt's own
        // Direction line uses -- reusing it here (rather than hardcoding a casing assumption) proves
        // Desktop's rendering carries the same direction CLI reported, not a test-authored guess.
        Assert.Contains(SurfaceFormatting.Machine(cliDirection), desktopPrompt, StringComparison.Ordinal);
        Assert.Contains(text.Resolve(MessageKeys.MoveToStageBlockedCannotProceed), desktopPrompt, StringComparison.Ordinal);
        foreach ((string id, string messageKey) in cliBlocked)
        {
            Assert.Contains(
                string.Create(CultureInfo.InvariantCulture, $"{id} ({messageKey})"),
                desktopPrompt,
                StringComparison.Ordinal);
        }
    }

    private static async Task<(SprintId SprintId, Guid AttemptId)> CreateRunningAttemptAsync(
        TestEnvironment environment, CancellationToken cancellationToken)
    {
        Assert.True((await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken)).Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: SprintManageParityGraph),
            cancellationToken)).SprintId!;
        await RunSprintToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        long version = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!
            .Nodes["a"].Version;
        StartAttemptResult started = await scheduler
            .StartAttemptAsync(environment.ProjectRoot, sprintId, "a", version, cancellationToken);
        Assert.True(started.Succeeded);
        return (sprintId, started.AttemptId!.Value);
    }

    private static readonly IReadOnlyList<NodeDefinition> StageMoveParityGraph =
        [new("a", NodeKind.Work, []), new("b", NodeKind.Work, ["a"])];

    private static async Task<SprintId> CreateReadyToAdvanceSprintAsync(
        TestEnvironment environment, CancellationToken cancellationToken)
    {
        Assert.True((await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken)).Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: StageMoveParityGraph),
            cancellationToken)).SprintId!;
        await RunSprintToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await CompleteWorkNodeDirectlyAsync(scheduler, store, environment.ProjectRoot, sprintId, "a", cancellationToken);
        return sprintId;
    }

    private static async Task<SprintId> CreateAdvanceBlockedByAnOpenFindingSprintAsync(
        TestEnvironment environment, CancellationToken cancellationToken)
    {
        SprintId sprintId = await CreateReadyToAdvanceSprintAsync(environment, cancellationToken);
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
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
        return sprintId;
    }

    private static async Task CompleteWorkNodeDirectlyAsync(
        SprintScheduler scheduler,
        ISprintStore store,
        string root,
        SprintId sprintId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        long version = (await store.LoadAsync(root, sprintId, cancellationToken))!.Nodes[nodeId].Version;
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(root, sprintId, nodeId, version, cancellationToken);
        Assert.True(started.Succeeded);
        CompleteAttemptResult completed = await scheduler.CompleteAttemptAsync(
            root, sprintId, nodeId, started.AttemptId!, true, SampleDigest, [], [], cancellationToken);
        Assert.True(completed.Succeeded);
    }

    private static async Task RunSprintToRunningAsync(
        SprintOrchestrator orchestrator, string root, SprintId sprintId, CancellationToken cancellationToken)
    {
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(root, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(root, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(root, sprintId, toReady.Sprint!.Version, SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
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
