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

        string trimmed = root.TrimEnd('/', '\\');
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? root : name;
    }
}
