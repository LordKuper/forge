using System.Diagnostics.CodeAnalysis;

namespace Forge.Desktop.Presentation;

/// <summary>
/// Maps a stable focus key (see <see cref="FocusKeyTracker"/>'s own remarks) to the live control
/// instance most recently registered under it, for exactly as long as that instance is still part of
/// the current render.
/// </summary>
/// <remarks>
/// PR #110 review finding 1: a render that rebuilds its controls from scratch but never calls
/// <see cref="Clear"/> first leaves every earlier key pointing at a now-detached instance -- a
/// resolvable-but-stale entry, not the "key no longer exists" no-op the caller expects. Generic over
/// <typeparamref name="TControl"/> (rather than fixed to MAUI's <c>VisualElement</c>) so this
/// mapping/lookup/clear behavior -- the actual risk that finding identified -- is unit-testable with a
/// plain object in this headless suite (ADR 0050: no MAUI control can be instantiated headlessly
/// here). <c>WorkspaceShellPage</c> instantiates this as <c>FocusControlRegistry&lt;VisualElement&gt;</c>
/// for both its sidebar and sprint-workspace-content halves, calling <see cref="Clear"/> at the start
/// of each rebuild (mirroring the discipline the sidebar half established first) before re-<see
/// cref="Register"/>ing every control the fresh render produces.
/// </remarks>
public sealed class FocusControlRegistry<TControl>
{
    private readonly Dictionary<string, TControl> controlsByKey = [];

    /// <summary>Discards every previously registered key -- called once at the start of a rebuild, before
    /// any control the fresh render produces is re-registered, so a key whose control no longer renders
    /// (plan 12.6: a resolved gate, a move target whose legal set changed) leaves nothing behind for
    /// <see cref="TryResolve"/> to (wrongly) resolve.</summary>
    public void Clear() => controlsByKey.Clear();

    /// <summary>Registers <paramref name="control"/> under <paramref name="key"/>, replacing whatever was
    /// registered under that key before, and returns <paramref name="control"/> unchanged so callers can
    /// wrap a control's own construction expression.</summary>
    public TControl Register(string key, TControl control)
    {
        ArgumentNullException.ThrowIfNull(key);
        controlsByKey[key] = control;
        return control;
    }

    /// <summary>Looks up the control currently registered under <paramref name="key"/>. Returns
    /// <see langword="false"/> both when <paramref name="key"/> was never registered and when it was
    /// discarded by the most recent <see cref="Clear"/> -- either way, "nothing to restore" is a normal,
    /// silent outcome, never an error.</summary>
    public bool TryResolve(string key, [MaybeNullWhen(false)] out TControl control) =>
        controlsByKey.TryGetValue(key, out control);
}
