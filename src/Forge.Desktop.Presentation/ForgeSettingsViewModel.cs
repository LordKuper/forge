using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Localization;
using Forge.Providers;

namespace Forge.Desktop.Presentation;

/// <summary>Plan section 5.1's table, one field per row, each carrying its own effective value and
/// <see cref="ConfigurationProvenance"/>. <see langword="null"/> for the two inheriting language
/// fields means "inherit," matching the registry's own <c>language.interaction</c>/<c>language.llm</c>
/// default of JSON <c>null</c>.</summary>
public sealed record ForgeSettingsSnapshot(
    string LanguageUi,
    ConfigurationProvenance LanguageUiProvenance,
    string? LanguageInteraction,
    ConfigurationProvenance LanguageInteractionProvenance,
    string? LanguageLlm,
    ConfigurationProvenance LanguageLlmProvenance,
    bool ConfirmDestructive,
    ConfigurationProvenance ConfirmDestructiveProvenance,
    IReadOnlyList<string> ProvidersEnabled,
    ConfigurationProvenance ProvidersEnabledProvenance,
    IReadOnlyList<ProviderHealthEntry> KnownProviders,
    bool NotificationsEnabled,
    ConfigurationProvenance NotificationsEnabledProvenance,
    IReadOnlyCollection<string> SupportedLanguages,
    string DiagnosticCode);

/// <summary>The in-progress edit <see cref="ForgeSettingsViewModel.SaveAsync"/> validates and writes
/// atomically as one set (plan 5.1/12.2: "Save validates the full edit set... Invalid edits cannot be
/// saved and do not partially modify configuration").</summary>
public sealed class ForgeSettingsEdit
{
    public required string LanguageUi { get; set; }

    public string? LanguageInteraction { get; set; }

    public string? LanguageLlm { get; set; }

    public required bool ConfirmDestructive { get; set; }

    public required IReadOnlyList<string> ProvidersEnabled { get; set; }

    public required bool NotificationsEnabled { get; set; }

    public static ForgeSettingsEdit From(ForgeSettingsSnapshot snapshot) => new()
    {
        LanguageUi = snapshot.LanguageUi,
        LanguageInteraction = snapshot.LanguageInteraction,
        LanguageLlm = snapshot.LanguageLlm,
        ConfirmDestructive = snapshot.ConfirmDestructive,
        ProvidersEnabled = snapshot.ProvidersEnabled,
        NotificationsEnabled = snapshot.NotificationsEnabled,
    };
}

public sealed record ForgeSettingsSaveResult(bool Succeeded, IReadOnlyList<string> ValidationErrorKeys, string DiagnosticCode)
{
    public static ForgeSettingsSaveResult Success { get; } = new(true, [], DiagnosticCodes.None);
}

/// <summary>
/// Plan section 5.1's Forge (user-scoped) settings page. Every key here is user-scope, so -- matching
/// ADR 0005's own reasoning for user-scope configuration -- it is always written through the local
/// <see cref="ForgeApplication"/> directly, never a project's Host. Reads and writes reuse
/// <see cref="ForgeApplication.GetUserConfigurationAsync"/>/
/// <see cref="ForgeApplication.SetConfigurationAsync(ConfigurationScope,string?,string,string?,CancellationToken)"/>
/// directly -- the same interface surface the previous monolithic page's own generic
/// configuration editor called into (since deleted as dead code; ADR 0050's finding-11 update) --
/// rather than a second configuration I/O path.
/// </summary>
public sealed class ForgeSettingsViewModel(
    ForgeApplication application, ProviderCatalog providerCatalog, SurfaceTextProvider text)
{
    public const string LanguageUiKey = "language.ui";
    public const string LanguageInteractionKey = "language.interaction";
    public const string LanguageLlmKey = "language.llm";
    public const string ConfirmDestructiveKey = "interaction.confirm_destructive";
    public const string NotificationsEnabledKey = "notifications.enabled";

    private readonly ForgeApplication application = application ?? throw new ArgumentNullException(nameof(application));
    private readonly ProviderCatalog providerCatalog = providerCatalog ?? throw new ArgumentNullException(nameof(providerCatalog));
    private readonly SurfaceTextProvider text = text ?? throw new ArgumentNullException(nameof(text));

    public async Task<ForgeSettingsSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        ConfigurationView view = await application.GetUserConfigurationAsync(cancellationToken).ConfigureAwait(false);
        ProviderToolchainStatus toolchain =
            await application.GetProviderHealthAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProviderHealthEntry> known = ProviderHealthProjector.Project(toolchain, providerCatalog);
        ConfigurationProvenance interactionProvenance = Provenance(view, LanguageInteractionKey);
        ConfigurationProvenance llmProvenance = Provenance(view, LanguageLlmKey);
        return new(
            StringValue(view, LanguageUiKey) ?? "en",
            Provenance(view, LanguageUiKey),
            // The resolver already returns the *inherited effective* value here (e.g. "en" via
            // language.ui), not a raw null, when nothing overrides it -- ConfigurationProvenance.Inherited
            // is what actually distinguishes "inheriting" from "explicitly set to this value," so
            // that (not nullness of the resolved value) is what decides the edit's own inherit
            // sentinel below.
            interactionProvenance == ConfigurationProvenance.Inherited ? null : StringValue(view, LanguageInteractionKey),
            interactionProvenance,
            llmProvenance == ConfigurationProvenance.Inherited ? null : StringValue(view, LanguageLlmKey),
            llmProvenance,
            BoolValue(view, ConfirmDestructiveKey) ?? true,
            Provenance(view, ConfirmDestructiveKey),
            StringArrayValue(view, ConfigurationKeys.ProvidersEnabled) ?? [.. known.Select(entry => entry.Id)],
            Provenance(view, ConfigurationKeys.ProvidersEnabled),
            known,
            BoolValue(view, NotificationsEnabledKey) ?? true,
            Provenance(view, NotificationsEnabledKey),
            SupportedLanguages,
            view.DiagnosticCode);
    }

    // The two-culture set ResourceLocalizationCatalog actually ships
    // (LocalizationCatalogTests.BuiltInCatalogsHaveIdenticalKeys already pins this pair); not read
    // from ILocalizationCatalog.SupportedCultures because this view-model depends on
    // SurfaceTextProvider, not the catalog itself.
    private static readonly IReadOnlyCollection<string> SupportedLanguages = ["en", "ru"];

    public static IReadOnlyList<string> Validate(ForgeSettingsEdit edit, ForgeSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(snapshot);
        List<string> errors = [];
        if (!snapshot.SupportedLanguages.Contains(edit.LanguageUi))
        {
            errors.Add(MessageKeys.SettingsLanguageUnsupported);
        }

        if (edit.LanguageInteraction is { } interaction && !snapshot.SupportedLanguages.Contains(interaction))
        {
            errors.Add(MessageKeys.SettingsLanguageUnsupported);
        }

        if (edit.LanguageLlm is { } llm && !snapshot.SupportedLanguages.Contains(llm))
        {
            errors.Add(MessageKeys.SettingsLanguageUnsupported);
        }

        HashSet<string> knownIds = new(snapshot.KnownProviders.Select(entry => entry.Id), StringComparer.Ordinal);
        if (edit.ProvidersEnabled.Any(id => !knownIds.Contains(id)))
        {
            errors.Add(MessageKeys.SettingsUnknownProvider);
        }

        return errors;
    }

    /// <summary>Validates the whole edit set before writing anything, then applies only the changed
    /// keys. If a later write still fails (e.g. a concurrent external edit invalidated a value this
    /// validation already accepted), the result reports that failure's diagnostic code and no further
    /// key is written -- validation mirrors every rule <see cref="ForgeApplication.SetConfigurationAsync(ConfigurationScope,string?,string,string?,CancellationToken)"/>
    /// itself enforces, so this is expected to be exceptionally rare, not a normal path.</summary>
    public async Task<ForgeSettingsSaveResult> SaveAsync(
        ForgeSettingsEdit edit, ForgeSettingsSnapshot current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(current);
        IReadOnlyList<string> errors = Validate(edit, current);
        if (errors.Count > 0)
        {
            return new(false, errors, DiagnosticCodes.ConfigurationInvalid);
        }

        List<(string Key, string RawValue)> writes = [];
        if (!string.Equals(edit.LanguageUi, current.LanguageUi, StringComparison.Ordinal))
        {
            writes.Add((LanguageUiKey, edit.LanguageUi));
        }

        if (!string.Equals(edit.LanguageInteraction, current.LanguageInteraction, StringComparison.Ordinal))
        {
            writes.Add((LanguageInteractionKey, edit.LanguageInteraction ?? "null"));
        }

        if (!string.Equals(edit.LanguageLlm, current.LanguageLlm, StringComparison.Ordinal))
        {
            writes.Add((LanguageLlmKey, edit.LanguageLlm ?? "null"));
        }

        if (edit.ConfirmDestructive != current.ConfirmDestructive)
        {
            writes.Add((ConfirmDestructiveKey, edit.ConfirmDestructive ? "true" : "false"));
        }

        if (!edit.ProvidersEnabled.SequenceEqual(current.ProvidersEnabled, StringComparer.Ordinal))
        {
            writes.Add((ConfigurationKeys.ProvidersEnabled, JsonSerializer.Serialize(edit.ProvidersEnabled)));
        }

        if (edit.NotificationsEnabled != current.NotificationsEnabled)
        {
            writes.Add((NotificationsEnabledKey, edit.NotificationsEnabled ? "true" : "false"));
        }

        foreach ((string key, string rawValue) in writes)
        {
            ConfigurationWriteResult result = await application
                .SetConfigurationAsync(ConfigurationScope.User, null, key, rawValue, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return new(false, [], result.DiagnosticCode);
            }
        }

        if (writes.Any(write => write.Key == LanguageUiKey))
        {
            // Plan 5.1/12.2: "Saving a UI language change updates all visible text without
            // restart." Every page holding this provider re-renders once this fires.
            text.SetLanguage(edit.LanguageUi);
        }

        return ForgeSettingsSaveResult.Success;
    }

    private static ConfigurationProvenance Provenance(ConfigurationView view, string key) =>
        view.Values.FirstOrDefault(value => value.Key == key)?.Provenance ?? ConfigurationProvenance.BuiltInDefault;

    private static string? StringValue(ConfigurationView view, string key)
    {
        JsonElement? value = Value(view, key);
        return value is { ValueKind: JsonValueKind.String } element ? element.GetString() : null;
    }

    private static bool? BoolValue(ConfigurationView view, string key)
    {
        JsonElement? value = Value(view, key);
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
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
