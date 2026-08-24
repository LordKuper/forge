namespace Forge.UnitTests;

/// <summary>
/// Plan 12.6: "All actions are keyboard reachable, ... usable at supported text scaling". These are
/// static source scans, not live UI automation (ADR 0050: no MAUI control can be instantiated
/// headlessly in this suite) -- they mechanically pin down the two anti-patterns that would silently
/// defeat WinUI's own default behavior (every real <c>Button</c>/<c>Entry</c>/etc. is keyboard-focusable
/// and font-auto-scaling out of the box): a control built from a bare gesture recognizer or
/// <c>GraphicsView</c> instead of a real focusable control, and a fixed pixel height or a disabled
/// <c>FontAutoScalingEnabled</c> on a text-bearing row that would clip text at a larger OS text-scale
/// setting.
/// </summary>
public sealed class WorkspaceShellAccessibilityTests
{
    private static readonly string[] ShellSourceFiles = ["WorkspaceShellPage.xaml", "WorkspaceShellPage.xaml.cs",
        "WorkspaceShellPage.ForgeSettings.cs", "WorkspaceShellPage.ProjectOverview.cs",
        "WorkspaceShellPage.ProjectSettings.cs", "WorkspaceShellPage.SprintWorkspace.cs"];

    [Fact]
    [Trait("Category", "Architecture")]
    public void ShellSourceFilesExistAtTheExpectedPaths()
    {
        // Guards every other [Fact] below against silently scanning zero files if the shell is ever
        // renamed/moved -- Assert.DoesNotContain over an empty sequence would otherwise pass vacuously.
        foreach (string path in ShellFilePaths())
        {
            Assert.True(File.Exists(path), $"Expected shell source file not found: {path}.");
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void TheWorkspaceShellNeverBuildsAFakeButtonFromABareGestureRecognizer()
    {
        // Plan 12.6 ("keyboard reachable"): a Grid/Label/Image wrapped in only a TapGestureRecognizer,
        // or a hand-drawn GraphicsView, looks clickable but is invisible to Tab/Shift+Tab and
        // Enter/Space -- unlike a real Button/Entry/Picker/Switch/CheckBox, which WinUI already makes
        // keyboard-focusable and activatable for free. Every interactive control in this shell today is
        // one of those real controls; this pins that down so a future control does not quietly regress
        // to a mouse-only fake button.
        AssertNoneContain(["GestureRecognizer", "GraphicsView"]);
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
        // row clips scaled-up text instead of letting the row grow. The XAML's one legitimate
        // HeightRequest today is the section-divider BoxView (a 1px rule, not text) -- MinimumHeightRequest
        // is the correct alternative wherever a row's height genuinely must have a floor.
        string xamlPath = ShellFilePaths().Single(path => path.EndsWith("WorkspaceShellPage.xaml", StringComparison.Ordinal));
        IEnumerable<string> heightRequestLines =
            File.ReadLines(xamlPath).Where(line => line.Contains("HeightRequest", StringComparison.Ordinal));

        Assert.All(heightRequestLines, line => Assert.Contains("BoxView", line, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void TheWorkspaceShellCodeBehindNeverFixesAHeightRequest()
    {
        // Every control the code-behind builds is created programmatically (plan section 4's own
        // "every visible string is assigned in code-behind" convention) -- a HeightRequest set there
        // would apply to a Button/Label/Entry, i.e. always a text-bearing control, unlike the XAML's one
        // BoxView exception above.
        AssertNoneContain(["HeightRequest"], onlyCsFiles: true);
    }

    private static void AssertNoneContain(string[] anyOf, bool onlyCsFiles = false)
    {
        List<string> offendingLines = [];
        foreach (string path in ShellFilePaths().Where(path => !onlyCsFiles || path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            offendingLines.AddRange(
                File.ReadLines(path)
                    .Where(line => anyOf.Any(needle => line.Contains(needle, StringComparison.Ordinal)))
                    .Select(line => $"{Path.GetFileName(path)}: {line.Trim()}"));
        }

        Assert.True(offendingLines.Count == 0, $"Found disallowed pattern(s):\n{string.Join('\n', offendingLines)}");
    }

    private static IEnumerable<string> ShellFilePaths() =>
        ShellSourceFiles.Select(name => Path.Combine(RepositoryRoot.Find(), "src", "Forge.Desktop", name));
}
