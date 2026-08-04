using System.Text.Json;
using Forge.Configuration;
using YamlDotNet.Core;

namespace Forge.Application;

/// <summary>
/// Runs the ordered startup sequence shared by every surface. Unresolved checks keep sprint
/// work fail-closed while recovery, initialization, and configuration remain available.
/// </summary>
public sealed class StartupPipeline(
    ConfigurationResolver resolver,
    ConfigurationMigrator migrator,
    ScopedConfigurationStores stores,
    ProjectRootResolver rootResolver,
    IPlatformPreflight platformPreflight)
{
    public async Task<StartupStatus> RunAsync(
        string? requestedRoot,
        CancellationToken cancellationToken)
    {
        List<StartupCheck> checks = [];
        (ConfigurationDocument user, StartupCheck userCheck) =
            await LoadUserConfigurationAsync(cancellationToken).ConfigureAwait(false);
        checks.Add(userCheck);

        LanguageSelection language = ResolveLanguage(user, checks);
        checks.AddRange(CheckPlatform());

        // The release check runs on demand through the update capability; Stage 2 owns its lifecycle.
        checks.Add(new(StartupCheckId.Release, StartupCheckState.Skipped, DiagnosticCodes.UpdateCheckDeferred));

        // Provider discovery arrives with the provider toolchain; until then sprint work stays blocked.
        checks.Add(new(
            StartupCheckId.Providers,
            StartupCheckState.Blocked,
            DiagnosticCodes.ProviderPreflightPending));

        ProjectRootStatus project =
            await rootResolver.ResolveAsync(requestedRoot, cancellationToken).ConfigureAwait(false);
        checks.Add(new(
            StartupCheckId.ProjectRoot,
            project.Initialized ? StartupCheckState.Passed : StartupCheckState.Blocked,
            project.DiagnosticCode));
        checks.Add(await CheckProjectConfigurationAsync(project, cancellationToken).ConfigureAwait(false));

        return new(Aggregate(checks), checks, language, project);
    }

    private static StartupState Aggregate(IReadOnlyList<StartupCheck> checks)
    {
        if (checks.Any(check => check.State == StartupCheckState.Failed))
        {
            return StartupState.Failed;
        }

        return checks.Any(check => check.State == StartupCheckState.Blocked)
            ? StartupState.Blocked
            : StartupState.Ready;
    }

    private async Task<(ConfigurationDocument Document, StartupCheck Check)> LoadUserConfigurationAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            ConfigurationDocument document =
                await stores.User.ReadAsync(cancellationToken).ConfigureAwait(false);
            return (
                migrator.Migrate(document, ConfigurationScope.User, ScopedConfigurationStores.SchemaVersion),
                StartupCheck.Passed(StartupCheckId.UserConfiguration));
        }
        catch (Exception error) when (
            error is JsonException or InvalidDataException or ConfigurationMigrationException or
                ConfigurationScopeException or IOException or UnauthorizedAccessException)
        {
            return (
                ConfigurationDocument.Empty,
                new(
                    StartupCheckId.UserConfiguration,
                    StartupCheckState.Failed,
                    DiagnosticCodes.ConfigurationInvalid));
        }
    }

    private LanguageSelection ResolveLanguage(ConfigurationDocument user, List<StartupCheck> checks)
    {
        Dictionary<string, JsonElement> session = new(StringComparer.Ordinal);
        try
        {
            LanguageSelection language = new(
                Read("language.ui"),
                Read("language.interaction"),
                Read("language.llm"));
            checks.Add(StartupCheck.Passed(StartupCheckId.Language));
            return language;
        }
        catch (Exception error) when (
            error is InvalidOperationException or KeyNotFoundException or ConfigurationScopeException)
        {
            checks.Add(new(
                StartupCheckId.Language,
                StartupCheckState.Failed,
                DiagnosticCodes.ConfigurationInvalid));
            return LanguageSelection.Fallback;
        }

        string Read(string key)
        {
            EffectiveConfigurationValue value = resolver.ResolveUser(key, session, user);
            return value.Value.ValueKind == JsonValueKind.String
                ? value.Value.GetString() ?? LanguageSelection.Fallback.Ui
                : LanguageSelection.Fallback.Ui;
        }
    }

    private IEnumerable<StartupCheck> CheckPlatform()
    {
        PlatformPreflightResult platform = platformPreflight.Check();
        bool detected = !string.Equals(platform.OperatingSystem, "unknown", StringComparison.Ordinal);
        yield return new(
            StartupCheckId.Platform,
            detected ? StartupCheckState.Passed : StartupCheckState.Failed,
            detected ? DiagnosticCodes.None : platform.DiagnosticCode);
        yield return new(
            StartupCheckId.UpdateStrategy,
            platform.StrategyResolved ? StartupCheckState.Passed : StartupCheckState.Failed,
            platform.StrategyResolved ? DiagnosticCodes.None : platform.DiagnosticCode);
    }

    private async Task<StartupCheck> CheckProjectConfigurationAsync(
        ProjectRootStatus project,
        CancellationToken cancellationToken)
    {
        if (!project.Initialized)
        {
            return new(
                StartupCheckId.ProjectConfiguration,
                StartupCheckState.Skipped,
                project.DiagnosticCode);
        }

        try
        {
            ConfigurationDocument document = await stores
                .Project(project.Root)
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            migrator.Migrate(document, ConfigurationScope.Project, ScopedConfigurationStores.SchemaVersion);
            return StartupCheck.Passed(StartupCheckId.ProjectConfiguration);
        }
        catch (Exception error) when (
            error is YamlException or InvalidDataException or ConfigurationMigrationException or
                FormatException or ConfigurationScopeException or IOException or UnauthorizedAccessException)
        {
            return new(
                StartupCheckId.ProjectConfiguration,
                StartupCheckState.Failed,
                DiagnosticCodes.ConfigurationInvalid);
        }
    }
}
