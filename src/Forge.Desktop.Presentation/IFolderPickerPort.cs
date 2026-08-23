namespace Forge.Desktop.Presentation;

/// <summary>
/// Plan section 4.1's "add-project action using the platform folder picker through a neutral port"
/// (AGENTS.md Portability: neutral presentation code selects a capability through a port, it never
/// calls an OS API directly). The Windows composition root (<c>Forge.Desktop</c>) implements this
/// with the real Windows folder-picker dialog; <see cref="SidebarViewModel"/> only ever depends on
/// this interface, never on the Windows type behind it.
/// </summary>
public interface IFolderPickerPort
{
    /// <summary>Returns the picked absolute folder path, or <see langword="null"/> when the user
    /// dismisses the picker without choosing one.</summary>
    Task<string?> PickFolderAsync(CancellationToken cancellationToken);
}
