using System.Text.RegularExpressions;

namespace Forge.UnitTests;

/// <summary>
/// PR #110 review findings 2 and 3: static source scans (ADR 0050: no MAUI control can be
/// instantiated headlessly in this suite, so <c>WorkspaceShellPage.SprintWorkspace.cs</c>'s actual
/// focus-preservation behavior can only be pinned down textually here, the same discipline
/// <see cref="WorkspaceShellAccessibilityTests"/> already established) that every focusable control
/// the sprint workspace's contextual-action renderer builds is either registered with
/// <c>TrackContentFocus</c> (a meaningful restoration target) or explicitly wired to clear the focus
/// tracker when it gains focus (<c>ClearContentFocusWhenFocused</c>, for a control that is
/// deliberately not a restoration target) -- never neither, which is what silently reintroduces the
/// stale-focus-restoration bug these two findings describe.
/// </summary>
public sealed class WorkspaceShellFocusTrackingTests
{
    private static readonly string[] HoistedEntryFieldNames =
        ["rewindReasonEntry", "instructionEntry", "definitionOfDoneEntry", "evidenceEntry", "justificationEntry"];

    [Fact]
    [Trait("Category", "Architecture")]
    public void TheFiveHoistedEntryFieldsAreEachTrackedForFocusPreservation()
    {
        // PR #110 review finding 2: rewindReasonEntry, instructionEntry, definitionOfDoneEntry,
        // evidenceEntry, and justificationEntry are hoisted so their *instance* -- and whatever the
        // user already typed -- survives RefreshActionsAsync, but before this fix none of them was
        // ever passed through TrackContentFocus, so a refresh mid-edit still dropped focus to the top
        // of the page exactly as it would for a freshly built button.
        string source = SprintWorkspaceSource();
        foreach (string fieldName in HoistedEntryFieldNames)
        {
            Assert.True(
                IsWiredForFocusTracking(source, fieldName, requireTrackContentFocus: true),
                $"Expected '{fieldName}' to be registered via TrackContentFocus.");
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void NoFocusableControlInTheSprintWorkspaceIsLeftUntrackedAndUnclearing()
    {
        // PR #110 review finding 3: a control that is registered with neither TrackContentFocus nor
        // ClearContentFocusWhenFocused is exactly how the stale-key bug reappears -- tabbing from a
        // tracked control into that untracked one leaves the focus tracker's captured key stale, and
        // the next refresh wrongly restores focus to the control the user already tabbed away from.
        // Every real focusable control this file builds must opt into one of the two mechanisms.
        string source = SprintWorkspaceSource();
        List<string> untrackedControls = [];
        foreach (string controlName in DeclaredFocusableControlNames(source))
        {
            if (!IsWiredForFocusTracking(source, controlName, requireTrackContentFocus: false))
            {
                untrackedControls.Add(controlName);
            }
        }

        // PR #110 review round 2 finding 3: a control built and used inline (no separate declared
        // name -- e.g. TrackContentFocus("key", new Switch())) would be invisible to the name-based
        // scan above, which is exactly the same "silently missed" risk the sibling accessibility
        // test's dynamic ShellFilePaths() rewrite already eliminated one layer up. Every control this
        // file builds today is still a named declaration (see InlineConstructedControlStatements' own
        // remarks), so this normally contributes nothing -- it exists so a *future* inline control
        // cannot quietly bypass this guard the way a hardcoded type list already let Switch/CheckBox
        // do.
        untrackedControls.AddRange(InlineConstructedControlStatements(source)
            .Where(statement =>
                !statement.Contains("TrackContentFocus(", StringComparison.Ordinal) &&
                !statement.Contains("ClearContentFocusWhenFocused(", StringComparison.Ordinal))
            .Select(statement => $"(inline) {statement.Trim()}"));

        Assert.True(
            untrackedControls.Count == 0,
            $"Found focusable control(s) wired to neither TrackContentFocus nor ClearContentFocusWhenFocused: " +
                string.Join(", ", untrackedControls));
    }

    // Mirrors ClearContentFocusWhenFocused's own remarks in WorkspaceShellPage.SprintWorkspace.cs:
    // a control is safely accounted for either by becoming a real restoration target
    // (TrackContentFocus) or by explicitly discarding a stale captured key when it gains focus
    // (ClearContentFocusWhenFocused).
    private static bool IsWiredForFocusTracking(string source, string controlName, bool requireTrackContentFocus)
    {
        // PR #110 review round 2 finding 4: anchored to the CONTROL argument's own position --
        // immediately after the call's one separating comma, right before its closing paren -- rather
        // than "controlName appears anywhere in the whole call expression". The old, wider search
        // matched a KEY STRING LITERAL that merely happened to contain controlName as a substring word
        // (e.g. "action:confirm:not-confirmed" contains "confirmed"), so this guard stayed green even
        // after TrackContentFocus("action:confirm:confirmed", confirmed) was deleted outright -- proved
        // by mutation against 8fde647, the exact commit this fix builds on. The key argument itself may
        // still contain one level of nested parens ((?:[^()]|\([^()]*\))*): every key expression this
        // file ever builds is either a plain string literal or a single string.Create(...) call: no
        // deeper nesting exists today.
        bool tracked = new Regex(
                @"TrackContentFocus\(\s*(?:[^()]|\([^()]*\))*,\s*" + Regex.Escape(controlName) + @"\s*\)",
                RegexOptions.Singleline)
            .IsMatch(source);
        if (requireTrackContentFocus)
        {
            return tracked;
        }

        bool clearedOnFocus = new Regex(@"ClearContentFocusWhenFocused\(\s*" + Regex.Escape(controlName) + @"\s*\)")
            .IsMatch(source);
        return tracked || clearedOnFocus;
    }

    // Every local declaration RenderSprintWorkspaceAsync writes for one of MauiFocusableControlTypes'
    // own types -- PR #110 review round 2 finding 3 widened this from a hardcoded Entry/Button/Picker
    // allowlist (which silently missed Switch/CheckBox, already real elsewhere in this shell, plus
    // every other natively focusable MAUI control) to the shared list
    // WorkspaceShellAccessibilityTests's own remarks already describe. Declarations of any other type
    // (Label, VerticalStackLayout, HorizontalStackLayout, ...) are not focusable and are out of scope
    // for this scan.
    private static IEnumerable<string> DeclaredFocusableControlNames(string source) =>
        new Regex(@"^\s*(?:" + FocusableTypesPattern + @")\s+(\w+)\s*=", RegexOptions.Multiline)
            .Matches(source)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);

    // PR #110 review round 2 finding 3: every "new Type(...)" construction of one of
    // MauiFocusableControlTypes' own types whose ENCLOSING STATEMENT does not itself start with a
    // named declaration (Type name = ... / var name = ...) -- i.e. a control built and used with no
    // separate identifier of its own, which DeclaredFocusableControlNames above can never see because
    // there is no name to find. Filtering on the STATEMENT'S OWN START (not merely "no '=' immediately
    // before 'new'") is what keeps this from misfiring on today's Describe(new Entry(), ...) hoisted-
    // field pattern: that whole statement DOES start with "Entry rewindReasonEntry =", so it is
    // correctly left to the name-based checks above even though "new Entry(" itself sits a few
    // characters into a Describe(...) call. Splitting on ';' is a simplification (it does not parse
    // real C# statement boundaries), but every control-construction statement in this file is a single
    // simple statement with no embedded ';', matching this suite's existing regex-based-source-scan
    // discipline (see this file's own remarks on ADR 0050's live-MAUI-control constraint).
    private static IEnumerable<string> InlineConstructedControlStatements(string source)
    {
        // This file documents nearly every declaration with one or more full-line "//" comments
        // immediately above it (see rewindReasonEntry/messageEntry/instructionEntry's own remarks a
        // few lines up in WorkspaceShellPage.SprintWorkspace.cs) -- stripped here so those comment
        // lines never sit between a statement chunk's own start (^) and its actual first line of code,
        // which would otherwise make declarationStart below miss a perfectly ordinary named
        // declaration and misreport it as an untracked inline construction.
        string withoutFullLineComments = new Regex(@"^[ \t]*//.*$", RegexOptions.Multiline).Replace(source, "");
        Regex declarationStart = new(@"^\s*(?:" + FocusableTypesPattern + @"|var)\s+\w+\s*=", RegexOptions.Singleline);
        Regex construction = new(@"\bnew\s+(?:" + FocusableTypesPattern + @")\s*[\(\{]");
        foreach (string statement in withoutFullLineComments.Split(';'))
        {
            if (!declarationStart.IsMatch(statement) && construction.IsMatch(statement))
            {
                yield return statement;
            }
        }
    }

    private static readonly string FocusableTypesPattern = string.Join('|', MauiFocusableControlTypes.Names);

    private static string SprintWorkspaceSource() =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "src", "Forge.Desktop", "WorkspaceShellPage.SprintWorkspace.cs"));
}
