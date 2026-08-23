namespace Forge.Desktop.Presentation;

/// <summary>Plan section 4.2: "Project display name initially defaults to the root directory name.
/// A local alias belongs to the user project catalog and does not modify the repository manifest."</summary>
public static class ProjectDisplayName
{
    public static string Resolve(string root, string? alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!string.IsNullOrWhiteSpace(alias))
        {
            return alias;
        }

        // Deliberately not Path.GetFileName: it only recognizes the host OS's own separator
        // (backslash is an ordinary character on Linux/macOS), so a Windows-recorded root read
        // back on a different OS -- or, as CI's cross-platform run caught, a Windows-style path in
        // a portable-core test -- would fail to resolve at all. Neutral code must behave
        // identically on every OS (AGENTS.md Portability), so both separators are always
        // recognized here regardless of which OS is actually running.
        string trimmed = root.TrimEnd('/', '\\');
        int lastSeparator = trimmed.LastIndexOfAny(['/', '\\']);
        string name = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
        return string.IsNullOrEmpty(name) ? root : name;
    }
}
