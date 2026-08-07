using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Configuration;

namespace Forge.Application;

public sealed record InitializeProjectRequest(
    string? Root,
    bool Confirmed,
    string UserFacingLanguage = "en",
    string AgentFacingLanguage = "en");

public sealed record InitializeProjectResult(
    bool Succeeded,
    string Root,
    Guid? ProjectId,
    string DiagnosticCode);

/// <summary>
/// Creates the minimal <c>.forge/</c> tree through a staging directory and one atomic publish.
/// An unknown or partial existing tree is never overwritten.
/// </summary>
public sealed class ProjectInitializer(
    IConfigurationRegistry registry,
    ProjectRootResolver rootResolver)
{
    internal const string WorkflowName = "implementation-critical";
    internal const string WorkflowContractVersion = "1.0.0";

    public async Task<InitializeProjectResult> InitializeAsync(
        InitializeProjectRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(request.Root, cancellationToken).ConfigureAwait(false);

        if (status.Initialized)
        {
            return new(true, status.Root, null, DiagnosticCodes.ProjectAlreadyInitialized);
        }

        if (!status.Exists || status.Unknown)
        {
            return new(false, status.Root, null, status.DiagnosticCode);
        }

        if (!request.Confirmed)
        {
            return new(false, status.Root, null, DiagnosticCodes.ConfirmationRequired);
        }

        return await PublishAsync(status.Root, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InitializeProjectResult> PublishAsync(
        string root,
        InitializeProjectRequest request,
        CancellationToken cancellationToken)
    {
        Guid projectId = Guid.NewGuid();
        string staging = Path.Combine(
            root,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{ProjectRootResolver.ForgeDirectoryName}.staging-{Guid.NewGuid():N}"));
        bool published = false;
        try
        {
            await StageAsync(staging, projectId, request, cancellationToken).ConfigureAwait(false);
            Directory.Move(staging, ProjectRootResolver.ForgeDirectory(root));
            published = true;
            return new(true, root, projectId, DiagnosticCodes.None);
        }
        catch (IOException)
        {
            ProjectRootStatus current =
                await rootResolver.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
            return current.Initialized
                ? new(true, root, null, DiagnosticCodes.ProjectAlreadyInitialized)
                : new(false, root, null, DiagnosticCodes.InternalError);
        }
        catch (UnauthorizedAccessException)
        {
            return new(false, root, null, DiagnosticCodes.InternalError);
        }
        catch (InvalidDataException)
        {
            return new(false, root, null, DiagnosticCodes.ConfigurationInvalid);
        }
        finally
        {
            if (!published)
            {
                // Cancellation and unexpected failures must never leave a staging tree behind.
                Discard(staging);
            }
        }
    }

    private async Task StageAsync(
        string staging,
        Guid projectId,
        InitializeProjectRequest request,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(staging, "workflows"));
        await new YamlConfigurationStore(
                Path.Combine(staging, ProjectRootResolver.ManifestFileName),
                ConfigurationScope.Project,
                registry)
            .WriteAsync(
                new(
                    1,
                    new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["artifacts.language.user_facing"] =
                            JsonSerializer.SerializeToElement(request.UserFacingLanguage),
                        ["artifacts.language.agent_facing"] =
                            JsonSerializer.SerializeToElement(request.AgentFacingLanguage),
                    },
                    projectId,
                    WorkflowName),
                cancellationToken)
            .ConfigureAwait(false);

        await File.WriteAllTextAsync(
                Path.Combine(staging, "workflows", $"{WorkflowName}.yaml"),
                $"schema_version: {WorkflowContractVersion}{Environment.NewLine}workflow: {WorkflowName}{Environment.NewLine}",
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void Discard(string staging)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
        catch (IOException)
        {
            // A locked staging directory is inert; it is never published.
        }
        catch (UnauthorizedAccessException)
        {
            // A locked staging directory is inert; it is never published.
        }
    }
}
