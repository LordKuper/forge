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
}
