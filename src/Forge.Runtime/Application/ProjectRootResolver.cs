using Forge.Configuration;
using YamlDotNet.Core;

namespace Forge.Application;

/// <summary>
/// Resolves the project root from the current or an explicitly supplied absolute directory.
/// The resolver never searches upward, so configuration is never created outside a confirmed root.
/// </summary>
public sealed class ProjectRootResolver(
    IConfigurationRegistry registry,
    IEnvironmentPaths environment)
{
    public const string ForgeDirectoryName = ".forge";
    public const string ManifestFileName = "manifest.yaml";

    public static string ForgeDirectory(string root) =>
        Path.Combine(root, ForgeDirectoryName);

    public static string ManifestPath(string root) =>
        Path.Combine(ForgeDirectory(root), ManifestFileName);

    public async Task<ProjectRootStatus> ResolveAsync(
        string? requestedRoot,
        CancellationToken cancellationToken)
    {
        string candidate = requestedRoot ?? environment.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
        {
            return new(candidate ?? string.Empty, false, false, false, DiagnosticCodes.ProjectRootNotAbsolute);
        }

        string root = Path.GetFullPath(candidate);
        if (!Directory.Exists(root))
        {
            return new(root, false, false, false, DiagnosticCodes.ProjectRootMissing);
        }

        if (!Directory.Exists(ForgeDirectory(root)))
        {
            return new(root, true, false, false, DiagnosticCodes.ProjectNotInitialized);
        }

        return await ReadManifestAsync(root, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProjectRootStatus> ReadManifestAsync(
        string root,
        CancellationToken cancellationToken)
    {
        string manifest = ManifestPath(root);
        if (!File.Exists(manifest))
        {
            return new(root, true, false, true, DiagnosticCodes.ProjectDirectoryUnknown);
        }

        try
        {
            ConfigurationDocument document =
                await new YamlConfigurationStore(manifest, ConfigurationScope.Project, registry)
                    .ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
            return document.ProjectId is null
                ? new(root, true, false, true, DiagnosticCodes.ProjectDirectoryUnknown)
                : new(root, true, true, false, DiagnosticCodes.None);
        }
        catch (Exception error) when (
            error is YamlException or InvalidDataException or FormatException or
                ConfigurationScopeException or IOException or UnauthorizedAccessException)
        {
            return new(root, true, false, true, DiagnosticCodes.ProjectDirectoryUnknown);
        }
    }
}
