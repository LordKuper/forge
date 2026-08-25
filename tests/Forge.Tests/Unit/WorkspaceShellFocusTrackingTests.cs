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
        // Every real Entry/Button/Picker this file builds must opt into one of the two mechanisms.
        string source = SprintWorkspaceSource();
        List<string> untrackedControls = [];
        foreach (string controlName in DeclaredFocusableControlNames(source))
        {
            if (!IsWiredForFocusTracking(source, controlName, requireTrackContentFocus: false))
            {
                untrackedControls.Add(controlName);
            }
        }

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
        bool tracked = new Regex(
                @"TrackContentFocus\([^;]*?\b" + Regex.Escape(controlName) + @"\b[^;]*?\)", RegexOptions.Singleline)
            .IsMatch(source);
        if (requireTrackContentFocus)
        {
            return tracked;
        }

        bool clearedOnFocus = new Regex(@"ClearContentFocusWhenFocused\(\s*" + Regex.Escape(controlName) + @"\s*\)")
            .IsMatch(source);
        return tracked || clearedOnFocus;
    }

    // Every local Entry/Button/Picker RenderSprintWorkspaceAsync declares -- the exact set of MAUI
    // control types this file ever builds that are natively keyboard-focusable (see
    // WorkspaceShellAccessibilityTests's own "real control" reasoning). Declarations of any other
    // type (Label, VerticalStackLayout, HorizontalStackLayout, ...) are not focusable and are out of
    // scope for this scan.
    private static IEnumerable<string> DeclaredFocusableControlNames(string source) =>
        new Regex(@"^\s*(?:Entry|Button|Picker)\s+(\w+)\s*=", RegexOptions.Multiline)
            .Matches(source)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);

    private static string SprintWorkspaceSource() =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "src", "Forge.Desktop", "WorkspaceShellPage.SprintWorkspace.cs"));
}
