namespace Forge.Desktop.Presentation;

/// <summary>
/// Tracks which stable focus key currently has keyboard focus, surviving a re-render that replaces
/// the focused control instance itself (plan 12.6: "focus-stable after refresh").
/// </summary>
/// <remarks>
/// <see cref="ShellRenderGate"/> serializes and replays the workspace shell's re-renders, and every
/// one of them rebuilds a subtree from scratch -- a <c>Button</c> that had keyboard focus is discarded
/// and a new instance takes its place, silently dropping focus to the page. This type is the neutral
/// half of the fix: it knows nothing about MAUI view types, only about the stable string keys
/// <c>WorkspaceShellPage</c> already derives from domain identifiers (a project id, a sprint id, an
/// action id) the same way <c>SemanticProperties.SetDescription</c> already does elsewhere in that
/// class. <c>WorkspaceShellPage</c> wires a control's <c>Focused</c> event to <see cref="Capture"/> and,
/// after rebuilding, looks up <see cref="Consume"/>'s key in the freshly-built control registry and
/// calls <c>.Focus()</c> if a match exists -- work that needs a live MAUI visual tree and so belongs in
/// the Windows adapter, not here (ADR 0050: no MAUI control can be instantiated headlessly in this
/// suite).
/// </remarks>
public sealed class FocusKeyTracker
{
    private string? currentKey;

    /// <summary>Records <paramref name="key"/> as the stable identifier of the control that most
    /// recently gained focus, replacing whatever was captured before -- only the most recently
    /// focused control is a meaningful restoration target.</summary>
    public void Capture(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        currentKey = key;
    }

    /// <summary>Discards any captured key without returning it -- used when focus moves somewhere this
    /// tracker should not try to restore later (e.g. focus left the tracked region entirely).</summary>
    public void Clear() => currentKey = null;

    /// <summary>Returns the most recently captured key and clears it. Consuming (rather than merely
    /// reading) the key means a later render that finds nothing new to restore -- or restores it once
    /// -- never re-applies the same stale request to a subsequent, unrelated render.</summary>
    public string? Consume()
    {
        string? key = currentKey;
        currentKey = null;
        return key;
    }
}
