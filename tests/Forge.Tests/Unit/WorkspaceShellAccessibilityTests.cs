using System.Text.RegularExpressions;

namespace Forge.UnitTests;

/// <summary>
/// Plan 12.6: "All actions are keyboard reachable, ... usable at supported text scaling". These are
/// static source scans, not live UI automation (ADR 0050: no MAUI control can be instantiated
/// headlessly in this suite) -- they mechanically pin down the anti-patterns that would silently
/// defeat WinUI's own default behavior (every real <c>Button</c>/<c>Entry</c>/etc. is keyboard-focusable
/// and font-auto-scaling out of the box): a control built from a bare gesture recognizer or
/// <c>GraphicsView</c> instead of a real focusable control, a real control opted back out of Tab
/// reachability (<c>IsTabStop="False"</c>/<c>InputTransparent="True"</c>), and a fixed pixel height or a
/// disabled <c>FontAutoScalingEnabled</c> on a text-bearing row that would clip text at a larger OS
/// text-scale setting.
/// </summary>
public sealed class WorkspaceShellAccessibilityTests
{
    // PR #110 review finding 4: the previous hardcoded 6-file allowlist gave every other [Fact]
    // below a silent blind spot -- a future WorkspaceShellPage*.cs/.xaml file could carry a real
    // anti-pattern and still pass, because it was never in the list to begin with. Enumerating the
    // shell's own naming prefix at test-run time means a new partial-class file is automatically in
    // scope with no allowlist edit required.
    // PR #112 review round 2 finding 1: the second alternative is what makes this pattern see a
    // ResourceDictionary Style at all. A Setter spells the property as a quoted *value*
    // (<Setter Property="HeightRequest" Value="28" />), never as `HeightRequest=`, so the first
    // alternative alone silently skipped every Style in App.xaml even once the file was in scope --
    // exactly how the deleted IconButtonStyle's fixed height reached review. MinimumHeightRequest /
    // MaximumHeightRequest stay excluded in both forms (the lookbehinds, and the exact-match quotes).
    private static readonly Regex FixedHeightRequestPattern = new(
        @"(?<!Minimum)(?<!Maximum)HeightRequest\s*=|Property\s*=\s*""HeightRequest""", RegexOptions.Compiled);

    // PR #110 review finding 6: IsTabStop="False"/InputTransparent="True" (XAML) or
    // IsTabStop = false/InputTransparent = true (code-behind) opt an otherwise-real, otherwise-
    // focusable control back out of Tab reachability just as effectively as the bare-gesture-
    // recognizer fake button the sibling test below guards against -- neither property is scanned
    // by that test. The re-enabling direction (IsTabStop="True", InputTransparent="False") is left
    // unflagged since it never removes reachability.
    private static readonly Regex TabReachabilityOptOutPattern = new(
        @"IsTabStop\s*=\s*""?[Ff]alse""?|InputTransparent\s*=\s*""?[Tt]rue""?", RegexOptions.Compiled);

    // PR #112 review round 2 finding 1: resolves the control type a <Style> applies to, so a Setter
    // on one of its own lines can be judged against that type rather than against its own text.
    private static readonly Regex StyleTargetTypePattern =
        new(@"<Style\b[^>]*\bTargetType\s*=\s*""(?<type>[^""]+)""", RegexOptions.Compiled);

    [Fact]
    [Trait("Category", "Architecture")]
    public void ShellSourceFilesAreDiscovered()
    {
        // Guards every other [Fact] below against silently scanning zero files if the shell is ever
        // renamed/moved out of src/Forge.Desktop -- Assert.DoesNotContain/Assert.All over an empty
        // sequence would otherwise pass vacuously.
        Assert.NotEmpty(ShellFilePaths());
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void TheWorkspaceShellNeverBuildsAFakeButtonFromABareGestureRecognizer()
    {
        // Plan 12.6 ("keyboard reachable"): a Grid/Label/Image wrapped in only a TapGestureRecognizer,
        // or a hand-drawn GraphicsView, looks clickable but is invisible to Tab/Shift+Tab and
        // Enter/Space -- unlike a real control of one of MauiFocusableControlTypes's own types (PR
        // #110 review round 2 finding 3: the shared "real control" set, so this comment's own list and
        // WorkspaceShellFocusTrackingTests's discovery regex can never silently drift apart again),
        // which WinUI already makes keyboard-focusable and activatable for free. Every interactive
        // control in this shell today is one of those real controls; this pins that down so a future
        // control does not quietly regress to a mouse-only fake button.
        AssertNoneContain(["GestureRecognizer", "GraphicsView"]);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void TheWorkspaceShellNeverOptsARealControlOutOfTabReachability()
    {
        // Plan 12.6 ("keyboard reachable"): unlike the bare-gesture-recognizer fake button above, this
        // guards the opposite direction -- a genuinely real, genuinely focusable control that a future
        // change quietly makes unreachable by setting IsTabStop="False" or InputTransparent="True" on
        // it (or on an ancestor layout), rather than by never having been a real control at all.
        AssertNoneMatch(line => TabReachabilityOptOutPattern.IsMatch(line));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void TheWorkspaceShellNeverDisablesFontAutoScaling()
    {
        // Plan 12.6 ("usable at supported text scaling"): MAUI's Label/Button/Entry/etc. default
        // FontAutoScalingEnabled to true, which already respects the OS text-scale setting on Windows
        // with no extra code. Setting it to false anywhere in the shell would opt that control out of
        // scaling and clip its text at larger OS text-scale settings.
        AssertNoneContain(["FontAutoScalingEnabled"]);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void TheWorkspaceShellXamlNeverFixesAHeightOnATextBearingRow()
    {
        // Plan 12.6 ("usable at supported text scaling"): a fixed HeightRequest on a text-containing
        // row clips scaled-up text instead of letting the row grow. The XAML's only legitimate
        // HeightRequests today are section-divider BoxViews (a 1px rule, not text) --
        // MinimumHeightRequest is the correct alternative wherever a row's height genuinely must have
        // a floor, and FixedHeightRequestPattern (PR #110 review finding 5) excludes it from this scan
        // so that alternative is never itself blocked.
        // PR #112 review round 2 finding 1: App.xaml carries the shell's presentation Styles, where a
        // HeightRequest sits on a <Setter> line whose TargetType is on the enclosing <Style> line
        // above it -- so a per-line "the line must mention BoxView" check would reject the legitimate
        // DividerBoxStyle. The enclosing Style's TargetType is resolved instead, which keeps the
        // real anti-pattern (a fixed height on a Button/Label/Entry style, e.g. the IconButtonStyle
        // this PR's round 1 had to delete by hand) failing.
        List<string> offendingLines = [];
        foreach (string path in ShellFilePaths().Where(path => path.EndsWith(".xaml", StringComparison.Ordinal)))
        {
            string? enclosingStyleTarget = null;
            foreach (string line in File.ReadLines(path))
            {
                Match styleTarget = StyleTargetTypePattern.Match(line);
                if (styleTarget.Success)
                {
                    enclosingStyleTarget = styleTarget.Groups["type"].Value;
                }
                else if (line.Contains("</Style>", StringComparison.Ordinal))
                {
                    enclosingStyleTarget = null;
                }

                if (!FixedHeightRequestPattern.IsMatch(line) ||
                    line.Contains("BoxView", StringComparison.Ordinal) ||
                    string.Equals(enclosingStyleTarget, "BoxView", StringComparison.Ordinal))
                {
                    continue;
                }

                offendingLines.Add($"{Path.GetFileName(path)}: {line.Trim()}");
            }
        }

        Assert.True(
            offendingLines.Count == 0,
            $"Fixed HeightRequest(s) on a non-BoxView target:\n{string.Join('\n', offendingLines)}");
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void TheWorkspaceShellCodeBehindNeverFixesAHeightRequest()
    {
        // Every control the code-behind builds is created programmatically (plan section 4's own
        // "every visible string is assigned in code-behind" convention) -- a HeightRequest set there
        // would apply to a Button/Label/Entry, i.e. always a text-bearing control, unlike the XAML's one
        // BoxView exception above. FixedHeightRequestPattern (PR #110 review finding 5) leaves
        // MinimumHeightRequest -- the correct alternative the sibling test above documents, and the
        // natural next fix after PR #103 review finding 2's MinimumWidthRequest precedent in this same
        // file -- unbanned.
        AssertNoneMatch(line => FixedHeightRequestPattern.IsMatch(line), onlyCsFiles: true);
    }

    private static void AssertNoneContain(string[] anyOf, bool onlyCsFiles = false) =>
        AssertNoneMatch(line => anyOf.Any(needle => line.Contains(needle, StringComparison.Ordinal)), onlyCsFiles);

    private static void AssertNoneMatch(Func<string, bool> isDisallowed, bool onlyCsFiles = false)
    {
        List<string> offendingLines = [];
        foreach (string path in ShellFilePaths().Where(path => !onlyCsFiles || path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            offendingLines.AddRange(
                File.ReadLines(path)
                    .Where(isDisallowed)
                    .Select(line => $"{Path.GetFileName(path)}: {line.Trim()}"));
        }

        Assert.True(offendingLines.Count == 0, $"Found disallowed pattern(s):\n{string.Join('\n', offendingLines)}");
    }

    // PR #110 review finding 4: enumerates every WorkspaceShellPage*.cs/.xaml file under
    // src/Forge.Desktop at test-run time instead of spelling out a fixed list, so a future partial-
    // class file (or XAML) is automatically included with no test edit. Recursive so a future
    // sub-folder (e.g. a Views/ split) is not silently missed either; bin/obj build output is
    // explicitly excluded since it can contain stale or generated copies of the same file names.
    private static IEnumerable<string> ShellFilePaths()
    {
        string shellDirectory = Path.Combine(RepositoryRoot.Find(), "src", "Forge.Desktop");
        return Directory.EnumerateFiles(shellDirectory, "*", SearchOption.AllDirectories)
            .Where(path =>
                (path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".xaml", StringComparison.Ordinal)) &&
                IsScannedShellFile(Path.GetFileName(path)) &&
                !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    // PR #112 review round 2 finding 1: App.xaml belongs to the same scan as the shell's own files.
    // The Nocturne pass moved the shell's text-bearing presentation out of the guarded code-behind
    // and into App.xaml's resource dictionary -- the implicit Label/Entry/Editor/Picker styles and
    // the Primary/Secondary/Ghost/Danger button styles now reach every control in the shell, so a
    // fixed HeightRequest or FontAutoScalingEnabled="False" added to any of them clips scaled text
    // app-wide. Scanning only WorkspaceShellPage* left that entirely unguarded.
    private static bool IsScannedShellFile(string fileName) =>
        fileName.StartsWith("WorkspaceShellPage", StringComparison.Ordinal) ||
        string.Equals(fileName, "App.xaml", StringComparison.Ordinal);
}
