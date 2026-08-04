using System.Text.Json;
using Forge.Configuration;

namespace Forge.Application;

public sealed record InitializeProjectCommand(
    string? Root,
    bool Confirmed,
    long? ExpectedStateVersion = null,
    string UserFacingLanguage = "en",
    string AgentFacingLanguage = "en");

public sealed record ConfigurationWriteResult(bool Succeeded, string DiagnosticCode)
{
    public static ConfigurationWriteResult Success { get; } = new(true, DiagnosticCodes.None);
}

/// <summary>
/// The single entry point both surfaces use. Presentation adapters format and collect input;
/// every check, mutation, and recommendation is decided here.
/// </summary>
public sealed class ForgeApplication(
    StartupPipeline pipeline,
    ProjectRootResolver rootResolver,
    ProjectInitializer initializer,
    StatusAdvisor advisor,
    ScopedConfigurationService configuration)
{
    public Task<StartupStatus> GetStartupStatusAsync(
        string? projectRoot,
        CancellationToken cancellationToken) =>
        pipeline.RunAsync(projectRoot, cancellationToken);

    public async Task<ProjectStatusSnapshot> GetProjectStatusAsync(
        string? projectRoot,
        CancellationToken cancellationToken) =>
        advisor.CreateSnapshot(
            await pipeline.RunAsync(projectRoot, cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<SuggestedAction>> GetSuggestedActionsAsync(
        string? projectRoot,
        CancellationToken cancellationToken) =>
        (await GetProjectStatusAsync(projectRoot, cancellationToken).ConfigureAwait(false))
            .SuggestedActions;

    public async Task<InitializeProjectResult> InitializeProjectAsync(
        InitializeProjectCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(command.Root, cancellationToken).ConfigureAwait(false);
        if (command.ExpectedStateVersion is { } expected &&
            expected != StatusAdvisor.StateVersion(status))
        {
            return new(false, status.Root, null, DiagnosticCodes.SuggestionStale);
        }

        return await initializer
            .InitializeAsync(
                new(
                    status.Root,
                    command.Confirmed,
                    command.UserFacingLanguage,
                    command.AgentFacingLanguage),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<IReadOnlyList<EffectiveConfigurationValue>> GetUserConfigurationAsync(
        CancellationToken cancellationToken) =>
        configuration.GetUserAsync(null, cancellationToken);

    public async Task<IReadOnlyList<EffectiveConfigurationValue>> GetProjectConfigurationAsync(
        string? projectRoot,
        CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        return status.Initialized
            ? await configuration.GetProjectAsync(status.Root, cancellationToken).ConfigureAwait(false)
            : [];
    }

    public async Task<ConfigurationWriteResult> SetConfigurationAsync(
        ConfigurationScope scope,
        string? projectRoot,
        string key,
        JsonElement value,
        CancellationToken cancellationToken)
    {
        try
        {
            if (scope == ConfigurationScope.User)
            {
                await configuration.SetUserAsync(key, value, cancellationToken).ConfigureAwait(false);
                return ConfigurationWriteResult.Success;
            }

            ProjectRootStatus status = await rootResolver
                .ResolveAsync(projectRoot, cancellationToken)
                .ConfigureAwait(false);
            if (!status.Initialized)
            {
                return new(false, status.DiagnosticCode);
            }

            await configuration
                .SetProjectAsync(status.Root, key, value, cancellationToken)
                .ConfigureAwait(false);
            return ConfigurationWriteResult.Success;
        }
        catch (ConfigurationScopeException)
        {
            return new(false, DiagnosticCodes.ConfigurationScopeViolation);
        }
        catch (KeyNotFoundException)
        {
            return new(false, DiagnosticCodes.ConfigurationScopeViolation);
        }
        catch (InvalidDataException)
        {
            return new(false, DiagnosticCodes.ConfigurationInvalid);
        }
    }
}
