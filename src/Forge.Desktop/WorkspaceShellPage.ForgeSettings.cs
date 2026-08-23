using Forge.Configuration;
using Forge.Desktop.Presentation;
using Forge.Localization;
using Forge.Providers;

namespace Forge.Desktop;

/// <summary>Plan section 5.1's Forge settings page. Rendering only; every value, provenance,
/// validation, and write goes through <see cref="ForgeSettingsViewModel"/>.</summary>
public partial class WorkspaceShellPage
{
    private async Task RenderForgeSettingsAsync()
    {
        ForgeSettingsSnapshot snapshot = await forgeSettings.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        ForgeSettingsEdit edit = ForgeSettingsEdit.From(snapshot);

        ContentHost.Children.Add(GroupTitle(MessageKeys.ForgeSettingsLanguageGroupTitle));
        Picker uiPicker = LanguagePicker(MessageKeys.ForgeSettingsLanguageUiLabel, snapshot.SupportedLanguages, snapshot.LanguageUi, allowInherit: false);
        ContentHost.Children.Add(uiPicker);
        ContentHost.Children.Add(ProvenanceLabel(snapshot.LanguageUiProvenance));
        Picker interactionPicker = LanguagePicker(
            MessageKeys.ForgeSettingsLanguageInteractionLabel, snapshot.SupportedLanguages, snapshot.LanguageInteraction,
            allowInherit: true);
        ContentHost.Children.Add(interactionPicker);
        ContentHost.Children.Add(ProvenanceLabel(snapshot.LanguageInteractionProvenance));
        Picker llmPicker = LanguagePicker(
            MessageKeys.ForgeSettingsLanguageLlmLabel, snapshot.SupportedLanguages, snapshot.LanguageLlm, allowInherit: true);
        ContentHost.Children.Add(llmPicker);
        ContentHost.Children.Add(ProvenanceLabel(snapshot.LanguageLlmProvenance));

        ContentHost.Children.Add(GroupTitle(MessageKeys.ForgeSettingsSafetyGroupTitle));
        Switch confirmDestructive = new() { IsToggled = snapshot.ConfirmDestructive };
        SemanticProperties.SetDescription(confirmDestructive, text.Resolve(MessageKeys.ForgeSettingsConfirmDestructiveLabel));
        ContentHost.Children.Add(LabeledRow(MessageKeys.ForgeSettingsConfirmDestructiveLabel, confirmDestructive));
        // Plan 5.1: this row needs its own "mandatory-gate disclaimer" -- otherwise the toggle reads
        // as a global "stop asking me," which it is not (PR #98 review finding 10).
        ContentHost.Children.Add(new Label { Text = text.Resolve(MessageKeys.ForgeSettingsConfirmDestructiveDisclaimer) });
        ContentHost.Children.Add(ProvenanceLabel(snapshot.ConfirmDestructiveProvenance));

        ContentHost.Children.Add(GroupTitle(MessageKeys.ForgeSettingsProvidersGroupTitle));
        // Wires the previously-declared-but-unrendered ForgeSettingsProvidersEnabledLabel key: plan
        // 5.1's settings table lists this row with its own visible label, like every other row on
        // this page.
        ContentHost.Children.Add(new Label { Text = text.Resolve(MessageKeys.ForgeSettingsProvidersEnabledLabel) });
        Dictionary<string, CheckBox> providerToggles = [];
        foreach (ProviderHealthEntry provider in snapshot.KnownProviders)
        {
            CheckBox toggle = new() { IsChecked = snapshot.ProvidersEnabled.Contains(provider.Id) };
            SemanticProperties.SetDescription(toggle, provider.Id);
            providerToggles[provider.Id] = toggle;
            ContentHost.Children.Add(new HorizontalStackLayout
            {
                Children = { toggle, new Label { Text = provider.Id } },
            });
        }

        ContentHost.Children.Add(ProvenanceLabel(snapshot.ProvidersEnabledProvenance));

        ContentHost.Children.Add(GroupTitle(MessageKeys.ForgeSettingsNotificationsGroupTitle));
        Switch notifications = new() { IsToggled = snapshot.NotificationsEnabled };
        SemanticProperties.SetDescription(notifications, text.Resolve(MessageKeys.ForgeSettingsNotificationsEnabledLabel));
        ContentHost.Children.Add(LabeledRow(MessageKeys.ForgeSettingsNotificationsEnabledLabel, notifications));
        ContentHost.Children.Add(ProvenanceLabel(snapshot.NotificationsEnabledProvenance));

        Label result = new();
        Button save = new() { Text = text.Resolve(MessageKeys.SettingsSaveAction) };
        save.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            edit.LanguageUi = (string)uiPicker.SelectedItem;
            edit.LanguageInteraction = ResolveInheritable(interactionPicker.SelectedItem as string);
            edit.LanguageLlm = ResolveInheritable(llmPicker.SelectedItem as string);
            edit.ConfirmDestructive = confirmDestructive.IsToggled;
            edit.NotificationsEnabled = notifications.IsToggled;
            edit.ProvidersEnabled = [.. providerToggles.Where(pair => pair.Value.IsChecked).Select(pair => pair.Key)];
            ForgeSettingsSaveResult saveResult =
                await forgeSettings.SaveAsync(edit, snapshot, CancellationToken.None).ConfigureAwait(true);
            result.Text = text.Resolve(saveResult.Succeeded ? MessageKeys.SettingsSaved : MessageKeys.SettingsValidationFailed);
            if (saveResult.Succeeded)
            {
                await RenderContentAsync().ConfigureAwait(true);
            }
        });
        Button discard = new() { Text = text.Resolve(MessageKeys.SettingsDiscardAction) };
        discard.Clicked += (_, _) => _ = RunAsync(RenderContentAsync);
        ContentHost.Children.Add(new HorizontalStackLayout { Children = { save, discard } });
        ContentHost.Children.Add(result);
    }

    private Label GroupTitle(string key)
    {
        Label label = new() { Text = text.Resolve(key) };
        SemanticProperties.SetHeadingLevel(label, SemanticHeadingLevel.Level2);
        return label;
    }

    private Label ProvenanceLabel(ConfigurationProvenance provenance) => new()
    {
        Text = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{text.Resolve(MessageKeys.SettingsProvenanceLabel)}: {provenance}"),
    };

    /// <summary>Renders the control alongside its own visible, localized label -- PR #98 review
    /// finding 2: the previous implementation discarded <paramref name="labelKey"/> entirely, so
    /// confirm-destructive and notifications-enabled shipped as bare switches with no visible
    /// caption (only a screen-reader name).</summary>
    private HorizontalStackLayout LabeledRow(string labelKey, View control) => new()
    {
        Children = { new Label { Text = text.Resolve(labelKey) }, control },
    };

    private string? ResolveInheritable(string? selected) =>
        selected is null || string.Equals(selected, text.Resolve(MessageKeys.ForgeSettingsInheritOption), StringComparison.Ordinal)
            ? null
            : selected;

    private Picker LanguagePicker(string labelKey, IReadOnlyCollection<string> supported, string? current, bool allowInherit)
    {
        List<string> options = allowInherit ? [text.Resolve(MessageKeys.ForgeSettingsInheritOption), .. supported] : [.. supported];
        Picker picker = new() { ItemsSource = options };
        SemanticProperties.SetDescription(picker, text.Resolve(labelKey));
        picker.SelectedItem = current is null && allowInherit ? options[0] : current;
        return picker;
    }
}
