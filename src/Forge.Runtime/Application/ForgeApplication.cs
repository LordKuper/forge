using System.Text.Json;
using Forge.Configuration;
using YamlDotNet.Core;

namespace Forge.Application;

public sealed record InitializeProjectCommand(
    string? Root,
    bool Confirmed,
    long? ExpectedStateVersion = null,
    Guid? IdempotencyKey = null,
    string UserFacingLanguage = "en",
    string AgentFacingLanguage = "en");

public sealed record ConfigurationWriteResult(bool Succeeded, string DiagnosticCode)
{
    public static ConfigurationWriteResult Success { get; } = new(true, DiagnosticCodes.None);
}

public sealed record ProjectOverview(StartupStatus Startup, ProjectStatusSnapshot Status);

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
    public const string InitializeProjectAction = "initialize_project";

    public Task<StartupStatus> GetStartupStatusAsync(
        string? projectRoot,
        CancellationToken cancellationToken) =>
        pipeline.RunAsync(projectRoot, cancellationToken);

    /// <summary>Runs the startup sequence once and derives the status snapshot from it.</summary>
    public async Task<ProjectOverview> GetOverviewAsync(
        string? projectRoot,
        CancellationToken cancellationToken)
    {
        StartupStatus startup =
            await pipeline.RunAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        return new(startup, advisor.CreateSnapshot(startup));
    }

    public async Task<ProjectStatusSnapshot> GetProjectStatusAsync(
        string? projectRoot,
        CancellationToken cancellationToken) =>
        (await GetOverviewAsync(projectRoot, cancellationToken).ConfigureAwait(false)).Status;

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
        StartupStatus startup =
            await pipeline.RunAsync(command.Root, cancellationToken).ConfigureAwait(false);
        ProjectRootStatus status = startup.Project;
        if (!startup.AllowsProjectMutation)
        {
            return new(false, status.Root, null, DiagnosticCodes.StartupFailed);
        }

        long stateVersion = StatusAdvisor.StateVersion(status);
        if (command.ExpectedStateVersion is { } expected && expected != stateVersion)
        {
            return new(false, status.Root, null, DiagnosticCodes.SuggestionStale);
        }

        if (command.IdempotencyKey is { } key &&
            key != StatusAdvisor.IdempotencyKey(
                InitializeProjectAction,
                new("project", status.Root),
                stateVersion))
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

    public async Task<IReadOnlyList<EffectiveConfigurationValue>> GetUserConfigurationAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await configuration.GetUserAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<EffectiveConfigurationValue>> GetProjectConfigurationAsync(
        string? projectRoot,
        CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return [];
        }

        try
        {
            return await configuration
                .GetProjectAsync(status.Root, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            return [];
        }
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
            return new(false, DiagnosticCodes.ConfigurationKeyUnknown);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            return new(false, DiagnosticCodes.ConfigurationInvalid);
        }
    }

    private static bool IsRecoverable(Exception error) =>
        error is JsonException or YamlException or InvalidDataException or FormatException or
            InvalidOperationException or IOException or UnauthorizedAccessException;
}
