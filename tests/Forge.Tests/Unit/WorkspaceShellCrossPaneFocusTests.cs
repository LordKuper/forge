using System.Text.RegularExpressions;

namespace Forge.UnitTests;

/// <summary>
/// PR #110 review round 2 finding 2: a static source scan (ADR 0050: no MAUI control can be
/// instantiated headlessly in this suite, so the actual cross-pane focus-stealing repro -- focus a
/// sidebar control, navigate into the content pane, then trigger an unrelated sidebar-only rebuild --
/// can only be pinned down textually here) proving <c>WorkspaceShellPage.RenderContentAsync</c>
/// discards any focus key <c>sidebarFocusTracker</c> is still holding as soon as the content pane
/// becomes the active render target, mirroring the content half's own
/// <c>ClearContentFocusWhenFocused</c> discipline (<c>WorkspaceShellPage.SprintWorkspace.cs</c>).
/// Without this, a key captured from a sidebar control the user has since navigated away from survives
/// until some later, unrelated sidebar-only rebuild (add/remove project, the collapse toggle, or a
/// UI-language save) -- whose own <c>RestoreSidebarFocus</c> then wrongly resolves that stale key
/// against the freshly rebuilt sidebar and yanks focus out of the content pane the user has been
/// working in ever since.
/// </summary>
public sealed class WorkspaceShellCrossPaneFocusTests
{
    [Fact]
    [Trait("Category", "Architecture")]
    public void RenderContentAsyncClearsTheSidebarFocusTrackerBeforeBuildingContent()
    {
        string body = RenderContentAsyncBody();

        // Anchored to the METHOD BODY specifically (not merely "this string appears somewhere in the
        // file") so this cannot be satisfied by, say, a stray call inside RenderSidebarFromSnapshot --
        // the whole point is that rendering CONTENT is what must invalidate a captured SIDEBAR key.
        Assert.Contains("sidebarFocusTracker.Clear()", body, StringComparison.Ordinal);

        // The clear must happen before ContentHost is (re)populated, not after -- clearing too late
        // would not actually change behavior here (nothing else in this method reads
        // sidebarFocusTracker), but ordering it first is what keeps the invariant obviously correct by
        // construction rather than by the coincidence of nothing else in the method needing the old
        // value first.
        int clearIndex = body.IndexOf("sidebarFocusTracker.Clear()", StringComparison.Ordinal);
        int contentHostClearIndex = body.IndexOf("ContentHost.Children.Clear()", StringComparison.Ordinal);
        Assert.True(contentHostClearIndex >= 0, "Expected RenderContentAsync to clear ContentHost's children.");
        Assert.True(
            clearIndex < contentHostClearIndex,
            "Expected sidebarFocusTracker.Clear() to run before ContentHost.Children.Clear() in RenderContentAsync.");
    }

    // Extracts RenderContentAsync's own method body (from its signature to the method's closing brace,
    // which -- matching this file's own 4-space class-member indentation -- is the first line after the
    // signature consisting of exactly "    }") so this test cannot be satisfied by a call anywhere else
    // in WorkspaceShellPage.xaml.cs, only by one actually inside this specific method.
    private static string RenderContentAsyncBody()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "src", "Forge.Desktop", "WorkspaceShellPage.xaml.cs"));
        Match match = new Regex(
                @"private async Task RenderContentAsync\(\)\s*\{(?<body>.*?)\n {4}\}", RegexOptions.Singleline)
            .Match(source);
        Assert.True(match.Success, "Expected to find RenderContentAsync's method body in WorkspaceShellPage.xaml.cs.");
        return match.Groups["body"].Value;
    }
}
