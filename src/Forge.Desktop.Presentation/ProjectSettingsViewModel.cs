using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Localization;

namespace Forge.Desktop.Presentation;

/// <summary>Plan section 5.2's project-settings fields. <see cref="Root"/>/<see cref="ProjectId"/>
/// are read-only (shown with their own provenance is not applicable to them -- they are identity,
/// not configuration); every other field carries <see cref="ConfigurationProvenance"/>.</summary>
public sealed record ProjectSettingsSnapshot(
    string Root,
    Guid ProjectId,
    string? Alias,
    string UserFacingLanguage,
    ConfigurationProvenance UserFacingLanguageProvenance,
    string AgentFacingLanguage,
    ConfigurationProvenance AgentFacingLanguageProvenance,
    int TokenBudget,
    ConfigurationProvenance TokenBudgetProvenance,
    IReadOnlyList<string> AllowedModels,
    ConfigurationProvenance AllowedModelsProvenance,
    string DiagnosticCode);

public sealed class ProjectSettingsEdit
{
    public required string UserFacingLanguage { get; set; }

    public required string AgentFacingLanguage { get; set; }

    public required int TokenBudget { get; set; }

    public required IReadOnlyList<string> AllowedModels { get; set; }

    public static ProjectSettingsEdit From(ProjectSettingsSnapshot snapshot) => new()
    {
        UserFacingLanguage = snapshot.UserFacingLanguage,
        AgentFacingLanguage = snapshot.AgentFacingLanguage,
        TokenBudget = snapshot.TokenBudget,
        AllowedModels = snapshot.AllowedModels,
    };
}

public sealed record ProjectSettingsSaveResult(bool Succeeded, IReadOnlyList<string> ValidationErrorKeys, string DiagnosticCode)
{
    public static ProjectSettingsSaveResult Success { get; } = new(true, [], DiagnosticCodes.None);
}

/// <summary>
/// Plan section 5.2's project settings page: the four project-scoped configuration keys, provider
/// integration inspection/install/removal, startup recovery, and diagnostic bundle generation. The
/// last three delegate to <see cref="MainPageViewModel"/> unchanged -- the same capabilities the
/// previous monolithic page already exposed (plan 12.1). Configuration writes resolve
/// <see cref="IForgeMutations"/> exactly like <see cref="MainPageViewModel.SetConfigurationAsync"/>
/// does (ADR 0005: a project mutation is routed through its Host once one is reachable), calling its
/// <see cref="IForgeMutations.SetConfigurationAsync"/> method directly rather than through that
/// method's own text-rendering wrapper, so a save can validate the whole edit set atomically instead
/// of losing each write's structured result to a rendered string.
/// </summary>
public sealed class ProjectSettingsViewModel(
    ForgeApplication application,
    ProjectCatalogStore catalog,
    MainPageViewModel legacy,
    Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
    IFolderPickerPort folderPicker)
{
    public const string UserFacingLanguageKey = "artifacts.language.user_facing";
    public const string AgentFacingLanguageKey = "artifacts.language.agent_facing";
    public const string TokenBudgetKey = "context.token_budget";
    public const string AllowedModelsKey = "models.allowed_models";

    private const int MinTokenBudget = 1;

    private readonly ForgeApplication application = application ?? throw new ArgumentNullException(nameof(application));
    private readonly ProjectCatalogStore catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly MainPageViewModel legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
    private readonly Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations =
        resolveMutations ?? throw new ArgumentNullException(nameof(resolveMutations));
    private readonly IFolderPickerPort folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));

    public async Task<ProjectSettingsSnapshot> LoadAsync(
        Guid projectId, string root, string? alias, CancellationToken cancellationToken)
    {
        ConfigurationView view = await application
            .GetProjectConfigurationAsync(root, cancellationToken)
            .ConfigureAwait(false);
        return new(
            root,
            projectId,
            alias,
            StringValue(view, UserFacingLanguageKey) ?? "en",
            Provenance(view, UserFacingLanguageKey),
            StringValue(view, AgentFacingLanguageKey) ?? "en",
            Provenance(view, AgentFacingLanguageKey),
            IntValue(view, TokenBudgetKey) ?? TokenBudgetResolver.DefaultTokenBudget,
            Provenance(view, TokenBudgetKey),
            StringArrayValue(view, AllowedModelsKey) ?? [],
            Provenance(view, AllowedModelsKey),
            view.DiagnosticCode);
    }

    public static IReadOnlyList<string> Validate(ProjectSettingsEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(edit.UserFacingLanguage))
        {
            errors.Add(MessageKeys.SettingsLanguageUnsupported);
        }

        if (string.IsNullOrWhiteSpace(edit.AgentFacingLanguage))
        {
            errors.Add(MessageKeys.SettingsLanguageUnsupported);
        }

        if (edit.TokenBudget < MinTokenBudget)
        {
            errors.Add(MessageKeys.SettingsTokenBudgetInvalid);
        }

        return errors;
    }

    public async Task<ProjectSettingsSaveResult> SaveAsync(
        string? root, ProjectSettingsEdit edit, ProjectSettingsSnapshot current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(current);
        IReadOnlyList<string> errors = Validate(edit);
        if (errors.Count > 0)
        {
            return new(false, errors, DiagnosticCodes.ConfigurationInvalid);
        }

        List<(string Key, string RawValue)> writes = [];
        if (!string.Equals(edit.UserFacingLanguage, current.UserFacingLanguage, StringComparison.Ordinal))
        {
            writes.Add((UserFacingLanguageKey, edit.UserFacingLanguage));
        }

        if (!string.Equals(edit.AgentFacingLanguage, current.AgentFacingLanguage, StringComparison.Ordinal))
        {
            writes.Add((AgentFacingLanguageKey, edit.AgentFacingLanguage));
        }

        if (edit.TokenBudget != current.TokenBudget)
        {
            writes.Add((TokenBudgetKey, edit.TokenBudget.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (!edit.AllowedModels.SequenceEqual(current.AllowedModels, StringComparer.Ordinal))
        {
            writes.Add((AllowedModelsKey, JsonSerializer.Serialize(edit.AllowedModels)));
        }

        if (writes.Count == 0)
        {
            return ProjectSettingsSaveResult.Success;
        }

        IForgeMutations mutations = await resolveMutations(root, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach ((string key, string rawValue) in writes)
            {
                ConfigurationWriteResult result = await mutations
                    .SetConfigurationAsync(ConfigurationScope.Project, root, key, rawValue, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    return new(false, [], result.DiagnosticCode);
                }
            }
        }
        finally
        {
            if (mutations is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }

        return ProjectSettingsSaveResult.Success;
    }

    public async Task<string> SetAliasAsync(Guid projectId, string? alias, CancellationToken cancellationToken)
    {
        ProjectCatalogResult result = await catalog.SetAliasAsync(projectId, alias, cancellationToken)
            .ConfigureAwait(false);
        return result.DiagnosticCode;
    }

    /// <summary>Plan section 6.1's relink: picks a new folder through the same neutral port the
    /// sidebar's add-project flow uses, then verifies it against the catalog entry's own project id
    /// (<see cref="ProjectCatalogStore.RelinkAsync"/> never trusts the caller's claim).</summary>
    public async Task<string> RelinkAsync(Guid projectId, CancellationToken cancellationToken)
    {
        string? newRoot = await folderPicker.PickFolderAsync(cancellationToken).ConfigureAwait(false);
        if (newRoot is null)
        {
            return DiagnosticCodes.None;
        }

        ProjectCatalogResult result = await catalog.RelinkAsync(projectId, newRoot, cancellationToken)
            .ConfigureAwait(false);
        return result.DiagnosticCode;
    }

    public async Task<string> RemoveFromCatalogAsync(Guid projectId, CancellationToken cancellationToken)
    {
        ProjectCatalogResult result = await catalog.RemoveAsync(projectId, cancellationToken).ConfigureAwait(false);
        return result.DiagnosticCode;
    }

    public Task<string> RecoverAsync(string? root, bool confirmed, CancellationToken cancellationToken) =>
        legacy.RecoverAsync(root, confirmed, cancellationToken);

    public Task<string> GenerateIntegrationPreviewAsync(string? root, CancellationToken cancellationToken) =>
        legacy.GenerateIntegrationPreviewAsync(root, cancellationToken);

    public Task<string> InstallIntegrationAsync(string? root, bool confirmed, CancellationToken cancellationToken) =>
        legacy.InstallIntegrationAsync(root, confirmed, cancellationToken);

    public Task<string> RemoveIntegrationAsync(string? root, bool confirmed, CancellationToken cancellationToken) =>
        legacy.RemoveIntegrationAsync(root, confirmed, cancellationToken);

    /// <summary>Plan section 5.2's "diagnostic bundle generation," returned as the same redacted,
    /// allowlisted JSON `forge doctor --bundle` prints -- Desktop renders it; saving it to a file is
    /// left to a future slice's own save-file port (not required by this slice's scope).</summary>
    public async Task<string> GenerateDiagnosticBundleAsync(string? root, CancellationToken cancellationToken)
    {
        DiagnosticBundle bundle = await application.CollectDiagnosticBundleAsync(root, cancellationToken)
            .ConfigureAwait(false);
        return StatusJson.Serialize(bundle);
    }

    private static ConfigurationProvenance Provenance(ConfigurationView view, string key) =>
        view.Values.FirstOrDefault(value => value.Key == key)?.Provenance ?? ConfigurationProvenance.BuiltInDefault;

    private static string? StringValue(ConfigurationView view, string key)
    {
        JsonElement? value = Value(view, key);
        return value is { ValueKind: JsonValueKind.String } element ? element.GetString() : null;
    }

    private static int? IntValue(ConfigurationView view, string key)
    {
        JsonElement? value = Value(view, key);
        return value is { ValueKind: JsonValueKind.Number } element && element.TryGetInt32(out int result)
            ? result
            : null;
    }

    private static IReadOnlyList<string>? StringArrayValue(ConfigurationView view, string key)
    {
        JsonElement? value = Value(view, key);
        if (value is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        return [.. array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)];
    }

    private static JsonElement? Value(ConfigurationView view, string key) =>
        view.Values.FirstOrDefault(value => value.Key == key)?.Value;
}
