namespace Forge.UnitTests;

/// <summary>
/// PR #110 review round 2 finding 3: the canonical list of MAUI control types that are natively
/// keyboard-focusable out of the box -- the same "real control" set
/// <see cref="WorkspaceShellAccessibilityTests"/>'s own remarks describe (a real
/// <c>Button</c>/<c>Entry</c>/etc., unlike a bare gesture recognizer or <c>GraphicsView</c>, is
/// Tab-reachable and activatable for free). Shared by every static source scan in this suite that
/// needs to recognize one of these types by name, so no one scan's own hardcoded subset can silently
/// drift from what the shell's code-behind actually builds -- <c>WorkspaceShellPage.ForgeSettings.cs</c>
/// already builds <c>Switch</c>/<c>CheckBox</c> today, and <c>WorkspaceShellFocusTrackingTests</c>'s
/// own discovery regex used to hardcode only <c>Entry</c>/<c>Button</c>/<c>Picker</c>, silently missing
/// both those already-real types and every other focusable MAUI control
/// (<c>Editor</c>/<c>SearchBar</c>/<c>DatePicker</c>/<c>TimePicker</c>/<c>Slider</c>/<c>Stepper</c>/
/// <c>RadioButton</c>).
/// </summary>
internal static class MauiFocusableControlTypes
{
    public static readonly IReadOnlyList<string> Names =
    [
        "Button", "CheckBox", "DatePicker", "Editor", "Entry", "Picker", "RadioButton", "SearchBar",
        "Slider", "Stepper", "Switch", "TimePicker",
    ];
}
