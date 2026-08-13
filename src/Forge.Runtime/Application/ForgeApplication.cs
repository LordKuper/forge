using System.Text.Json;
using Forge.Configuration;
using Forge.Providers;
using YamlDotNet.Core;

namespace Forge.Application;

public sealed record InitializeProjectCommand(
    string? Root,
    bool Confirmed,
    long ExpectedStateVersion,
    Guid IdempotencyKey,
    string UserFacingLanguage = "en",
    string AgentFacingLanguage = "en");

public sealed record ConfigurationWriteResult(bool Succeeded, string DiagnosticCode)
{
    public static ConfigurationWriteResult Success { get; } = new(true, DiagnosticCodes.None);
}

public sealed record ConfigurationView(
    IReadOnlyList<EffectiveConfigurationValue> Values,
    string DiagnosticCode)
{
    public static ConfigurationView Empty { get; } = new([], DiagnosticCodes.None);
}

public sealed record ProjectOverview(StartupStatus Startup, ProjectSnapshot Snapshot);

/// <summary>
/// The single entry point both surfaces use. Presentation adapters format and collect input;
/// every check, mutation, and recommendation is decided here.
/// </summary>
public sealed class ForgeApplication(
    StartupPipeline pipeline,
    ProjectRootResolver rootResolver,
    ProjectInitializer initializer,
    StartupRecovery recovery,
    StatusAdvisor advisor,
    IConfigurationRegistry registry,
    ScopedConfigurationService configuration,
    IProviderToolchainManager providerToolchain,
    ControlEventsReader eventsReader)
{
    public const string InitializeProjectAction = "initialize_project";

    /// <summary>The key any surface must present to initialize the observed project state.</summary>
    public static Guid InitializationKey(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return StatusAdvisor.IdempotencyKey(
            InitializeProjectAction,
            new("project", snapshot.Project.Root),
            snapshot.StateVersion);
    }

    public Task<StartupStatus> GetStartupStatusAsync(
        string? projectRoot,
        CancellationToken cancellationToken) =>
        pipeline.RunAsync(projectRoot, cancellationToken);

    /// <summary>Runs the startup sequence once and derives the status snapshot from it.</summary>
    public async Task<ProjectOverview> GetOverviewAsync(
        string? projectRoot,
        CancellationToken cancellationToken) =>
        await GetOverviewAsync(projectRoot, SnapshotDetail.Summary, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Same as <see cref="GetOverviewAsync(string?,CancellationToken)"/>, additionally
    /// requesting the named or (with <see cref="SnapshotDetail.Full"/>) active sprint's detail
    /// section — the read model behind `GetProjectSnapshot(detail, sprint_id?)`.</summary>
    public async Task<ProjectOverview> GetOverviewAsync(
        string? projectRoot,
        SnapshotDetail detail,
        Guid? sprintId,
        CancellationToken cancellationToken)
    {
        StartupStatus startup =
            await pipeline.RunAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        return new(startup, await advisor.CreateSnapshotAsync(startup, detail, sprintId, cancellationToken)
            .ConfigureAwait(false));
    }

    public async Task<ProjectSnapshot> GetProjectSnapshotAsync(
        string? projectRoot,
        CancellationToken cancellationToken) =>
        (await GetOverviewAsync(projectRoot, cancellationToken).ConfigureAwait(false)).Snapshot;

    public async Task<ProjectSnapshot> GetProjectSnapshotAsync(
        string? projectRoot,
        SnapshotDetail detail,
        Guid? sprintId,
        CancellationToken cancellationToken) =>
        (await GetOverviewAsync(projectRoot, detail, sprintId, cancellationToken).ConfigureAwait(false)).Snapshot;

    /// <summary>The bounded, cursor-driven incremental read behind `ReadControlEvents`. See
    /// <see cref="ControlEventsReader"/> for the merge/cursor contract. An uninitialized or
    /// unresolvable project root reports no events rather than probing a `.forge/sprints/`
    /// directory that cannot exist yet — matching <see cref="StatusAdvisor.CreateSnapshotAsync(StartupStatus,SnapshotDetail,Guid?,CancellationToken)"/>.</summary>
    public async Task<ControlEventsPage> ReadControlEventsAsync(
        string? projectRoot,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (status.Initialized)
        {
            return await eventsReader.ReadAsync(status.Root, cursor, cancellationToken).ConfigureAwait(false);
        }

        // An uninitialized project has no journal to poll, but that is a distinct outcome from a
        // genuine "caught up, nothing new" read of an initialized project — both must not collapse
        // to the same DiagnosticCodes.None a caller could otherwise mistake for real progress. A
        // cursor that was itself already stale/malformed still reports that, unmasked.
        ControlEventsPage empty = ControlEventsPage.Empty(cursor);
        return empty.DiagnosticCode == DiagnosticCodes.None
            ? empty with { DiagnosticCode = DiagnosticCodes.ProjectNotInitialized }
            : empty;
    }

    /// <summary>
    /// Read-only discovery, matching the `provider.health` capability's declared `query`/`read`
    /// contract. Installing or updating is a separate, explicit action: <see cref="RefreshProviderHealthAsync"/>.
    /// </summary>
    public Task<ProviderToolchainStatus> GetProviderHealthAsync(CancellationToken cancellationToken) =>
        providerToolchain.CheckAsync(cancellationToken);

    /// <summary>Installs or updates any provider that is not ready, then rechecks both.</summary>
    public Task<ProviderToolchainStatus> RefreshProviderHealthAsync(CancellationToken cancellationToken) =>
        providerToolchain.EnsureReadyAsync(cancellationToken);

    /// <summary>Quarantines unreadable configuration so a failed startup can reach a usable state.</summary>
    public async Task<RecoverStartupResult> RecoverStartupAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        StartupStatus startup =
            await pipeline.RunAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (startup.FirstFailure is not { } failure)
        {
            return new(true, null, DiagnosticCodes.None);
        }

        if (!confirmed)
        {
            return new(false, failure.Id, DiagnosticCodes.ConfirmationRequired);
        }

        RecoverStartupResult result =
            await recovery.RecoverAsync(startup, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return result;
        }

        // Success means the startup sequence no longer fails, not merely that a file moved.
        StartupStatus repaired =
            await pipeline.RunAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        return repaired.FirstFailure is { } remaining
            ? new(false, remaining.Id, remaining.DiagnosticCode)
            : result;
    }

    public async Task<InitializeProjectResult> InitializeProjectAsync(
        InitializeProjectCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        StartupStatus startup =
            await pipeline.RunAsync(command.Root, cancellationToken).ConfigureAwait(false);
        ProjectRootStatus status = startup.Project;
        if (startup.FirstFailure is { } failure)
        {
            return new(false, status.Root, null, failure.DiagnosticCode);
        }

        long stateVersion = StatusAdvisor.StateVersion(status);
        if (command.ExpectedStateVersion != stateVersion ||
            command.IdempotencyKey != StatusAdvisor.IdempotencyKey(
                InitializeProjectAction,
                new("project", status.Root),
                stateVersion))
        {
            return new(false, status.Root, null, DiagnosticCodes.SuggestionStale);
        }

        bool confirmed = command.Confirmed ||
            !await RequiresConfirmationAsync(cancellationToken).ConfigureAwait(false);
        return await initializer
            .InitializeAsync(
                new(
                    status.Root,
                    confirmed,
                    command.UserFacingLanguage,
                    command.AgentFacingLanguage),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Honours `interaction.confirm_destructive`; an unreadable value stays fail-closed.</summary>
    private async Task<bool> RequiresConfirmationAsync(CancellationToken cancellationToken)
    {
        ConfigurationView user =
            await GetUserConfigurationAsync(cancellationToken).ConfigureAwait(false);
        EffectiveConfigurationValue? value = user.Values
            .FirstOrDefault(item => item.Key == "interaction.confirm_destructive");
        return value?.Value.ValueKind != JsonValueKind.False;
    }

    public async Task<ConfigurationView> GetUserConfigurationAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return new(
                await configuration.GetUserAsync(null, cancellationToken).ConfigureAwait(false),
                DiagnosticCodes.None);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            return new([], DiagnosticCodes.ConfigurationInvalid);
        }
    }

    public async Task<ConfigurationView> GetProjectConfigurationAsync(
        string? projectRoot,
        CancellationToken cancellationToken)
    {
        ProjectRootStatus status =
            await rootResolver.ResolveAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return new([], status.DiagnosticCode);
        }

        try
        {
            return new(
                await configuration
                    .GetProjectAsync(status.Root, cancellationToken)
                    .ConfigureAwait(false),
                DiagnosticCodes.None);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            return new([], DiagnosticCodes.ConfigurationInvalid);
        }
    }

    /// <summary>Converts the raw surface input using the declared type of the key.</summary>
    public async Task<ConfigurationWriteResult> SetConfigurationAsync(
        ConfigurationScope scope,
        string? projectRoot,
        string key,
        string? rawValue,
        CancellationToken cancellationToken)
    {
        ConfigurationKey descriptor;
        try
        {
            descriptor = registry.FindRequired(key);
        }
        catch (KeyNotFoundException)
        {
            return new(false, DiagnosticCodes.ConfigurationKeyUnknown);
        }

        return await SetConfigurationAsync(
                scope,
                projectRoot,
                key,
                ConfigurationValueParser.Parse(rawValue, descriptor),
                cancellationToken)
            .ConfigureAwait(false);
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
            ConfigurationMigrationException or ConfigurationScopeException or IOException or
            UnauthorizedAccessException;
}
