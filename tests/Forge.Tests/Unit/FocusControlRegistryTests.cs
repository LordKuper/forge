using Forge.Desktop.Presentation;

namespace Forge.UnitTests;

/// <summary>
/// PR #110 review finding 1: <see cref="FocusControlRegistry{TControl}"/> is the neutral, generic
/// half of the workspace shell's key-to-control mapping -- see its own remarks for why this stays
/// testable here even though only a live MAUI visual tree ever actually populates it in
/// <c>WorkspaceShellPage</c> (ADR 0050: no MAUI control can be instantiated headlessly in this suite).
/// A plain <see langword="string"/> stands in for the MAUI control type these tests would otherwise
/// need.
/// </summary>
public sealed class FocusControlRegistryTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void RegisterThenResolveReturnsTheRegisteredControl()
    {
        FocusControlRegistry<string> registry = new();

        registry.Register("action:finalize", "finalize-button");

        Assert.True(registry.TryResolve("action:finalize", out string? control));
        Assert.Equal("finalize-button", control);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveWithAKeyThatWasNeverRegisteredReturnsFalse()
    {
        FocusControlRegistry<string> registry = new();

        Assert.False(registry.TryResolve("action:finalize", out _));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RegisteringTheSameKeyTwiceReplacesTheEarlierControl()
    {
        FocusControlRegistry<string> registry = new();
        registry.Register("action:gate:approve", "old-approve-button");

        registry.Register("action:gate:approve", "new-approve-button");

        Assert.True(registry.TryResolve("action:gate:approve", out string? control));
        Assert.Equal("new-approve-button", control);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClearRemovesEveryRegisteredKeySoAStaleKeyNoLongerResolvesToItsDetachedControl()
    {
        // PR #110 review finding 1: this is the exact regression the finding described --
        // WorkspaceShellPage.SprintWorkspace.cs's contentFocusRegistry used to keep every key it had
        // ever seen. A rebuild that stops rendering a control (a resolved gate, a move target whose
        // legal set changed) must leave that control's key resolving to nothing, not to the now-
        // detached instance from before the rebuild. Register/Clear/re-register here stands in for
        // one such rebuild: only the key the "fresh render" re-registers should resolve afterward.
        FocusControlRegistry<string> registry = new();
        registry.Register("action:gate:approve", "approve-button");
        registry.Register("action:finalize", "finalize-button");

        registry.Clear();
        registry.Register("action:finalize", "rebuilt-finalize-button");

        Assert.False(registry.TryResolve("action:gate:approve", out _));
        Assert.True(registry.TryResolve("action:finalize", out string? control));
        Assert.Equal("rebuilt-finalize-button", control);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClearOnAnEmptyRegistryIsANoOp()
    {
        FocusControlRegistry<string> registry = new();

        registry.Clear();

        Assert.False(registry.TryResolve("anything", out _));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RegisterRejectsANullKey()
    {
        FocusControlRegistry<string> registry = new();

        Assert.Throws<ArgumentNullException>(() => registry.Register(null!, "control"));
    }

    // PR #110 review round 2 finding 1: TrackContentFocus (WorkspaceShellPage.SprintWorkspace.cs)
    // re-registers each of the sprint workspace's five hoisted Entry fields on every
    // RenderActions call, but must subscribe that control's Focused handler exactly once for the
    // control's entire lifetime -- not once per refresh -- or the handler count grows without bound on
    // the page's highest-frequency re-render path. MarkWiredOnce is the primitive that guard is built
    // on; these tests pin down its own dedupe behavior directly (headlessly, per ADR 0050), independent
    // of any MAUI control or the WorkspaceShellPage call site itself.
    [Fact]
    [Trait("Category", "Unit")]
    public void MarkWiredOnceReturnsTrueOnlyTheFirstTimeForAGivenControlInstance()
    {
        FocusControlRegistry<string> registry = new();
        string control = SomeControl();

        Assert.True(registry.MarkWiredOnce(control));
        Assert.False(registry.MarkWiredOnce(control));
        Assert.False(registry.MarkWiredOnce(control));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MarkWiredOnceTreatsDistinctInstancesWithEqualContentAsDifferentControls()
    {
        // A real VisualElement never overrides Equals/GetHashCode, so control identity in production
        // is always reference-based. This proves MarkWiredOnce agrees: two separately built stand-in
        // controls that happen to carry equal content (the same string value here) must each still get
        // their own independent "first time" result -- otherwise a freshly built control could be
        // wrongly treated as "already wired" merely because an earlier, unrelated control happened to
        // look the same, silently dropping its own Focused subscription.
        FocusControlRegistry<string> registry = new();
        string first = SomeControl();
        string second = SomeControl();

        Assert.True(registry.MarkWiredOnce(first));
        Assert.True(registry.MarkWiredOnce(second));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClearDoesNotResetWiringState()
    {
        // The whole point of MarkWiredOnce: a hoisted control is re-Register()ed (and this registry's
        // own Clear() called) on every refresh across the control's entire lifetime, yet its one-time
        // side effect must stay applied throughout every one of those Clear() cycles.
        FocusControlRegistry<string> registry = new();
        string control = SomeControl();
        Assert.True(registry.MarkWiredOnce(control));

        registry.Clear();
        registry.Register("action:move:rewind-reason", control);

        Assert.False(registry.MarkWiredOnce(control));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MarkWiredOnceRejectsANullControl()
    {
        FocusControlRegistry<string> registry = new();

        Assert.Throws<ArgumentNullException>(() => registry.MarkWiredOnce(null!));
    }

    // A compile-time string literal would be interned and could collide in identity with an unrelated
    // literal of equal content elsewhere in this file, defeating the reference-based scenarios above --
    // this always allocates a genuinely distinct instance.
    private static string SomeControl() => new("control".ToCharArray());
}
