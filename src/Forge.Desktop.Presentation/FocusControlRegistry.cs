using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

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
public sealed class FocusControlRegistry<TControl> where TControl : class
{
    private readonly Dictionary<string, TControl> controlsByKey = [];

    // PR #110 review round 2 finding 1: a control that survives across many rebuilds (one of
    // WorkspaceShellPage.SprintWorkspace.cs's five hoisted Entry fields) gets re-registered on every
    // refresh, but its Focused event handler must be subscribed exactly once for that instance's
    // entire lifetime, never once per refresh -- otherwise the handler count grows without bound on
    // the page's highest-frequency re-render path. A ConditionalWeakTable (rather than a HashSet)
    // holds this "already wired" state with only a weak reference to the control itself, so a
    // freshly built control that this registry sees exactly once (the overwhelming majority -- every
    // dynamic move/gate/lifecycle button) does not stay pinned in memory for the rest of the page's
    // lifetime merely because MarkWiredOnce was called for it; only the caller's own separate strong
    // reference (a hoisted field, or the live MAUI visual tree while a control is still on screen)
    // keeps a wired control's entry reachable, and it is reclaimed once that reference goes away.
    private readonly ConditionalWeakTable<TControl, TControl> wiredControls = new();

    /// <summary>Discards every previously registered key -- called once at the start of a rebuild, before
    /// any control the fresh render produces is re-registered, so a key whose control no longer renders
    /// (plan 12.6: a resolved gate, a move target whose legal set changed) leaves nothing behind for
    /// <see cref="TryResolve"/> to (wrongly) resolve. Deliberately does NOT reset <see cref="MarkWiredOnce"/>'s
    /// own state: a hoisted control's one-time wiring must stay applied across every <see cref="Clear"/>
    /// this registry ever sees for as long as that same control instance keeps being re-registered.</summary>
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

    /// <summary>Returns <see langword="true"/> the first time <paramref name="control"/> is ever passed
    /// to this method on this registry, and <see langword="false"/> on every later call for that same
    /// instance -- including across any number of intervening <see cref="Clear"/> calls, since that
    /// state is intentionally not reset by <see cref="Clear"/> (see its own remarks). Callers use this to
    /// guard a one-time side effect -- typically subscribing an event handler -- against being re-applied
    /// every time a control that survives across rebuilds (a hoisted field) is re-<see cref="Register"/>ed,
    /// while a freshly built control (re-registered under a brand new instance every time) still gets that
    /// side effect applied exactly once, the one and only time it is ever seen.</summary>
    public bool MarkWiredOnce(TControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (wiredControls.TryGetValue(control, out _))
        {
            return false;
        }

        wiredControls.Add(control, control);
        return true;
    }
}
