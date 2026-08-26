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

        ContentHost.Children.Add(SectionDivider());
        ContentHost.Children.Add(GroupTitle(MessageKeys.ForgeSettingsSafetyGroupTitle));
        Switch confirmDestructive = new() { IsToggled = snapshot.ConfirmDestructive };
        SemanticProperties.SetDescription(confirmDestructive, text.Resolve(MessageKeys.ForgeSettingsConfirmDestructiveLabel));
        ContentHost.Children.Add(LabeledRow(MessageKeys.ForgeSettingsConfirmDestructiveLabel, confirmDestructive));
        // Plan 5.1: this row needs its own "mandatory-gate disclaimer" -- otherwise the toggle reads
        // as a global "stop asking me," which it is not (PR #98 review finding 10).
        ContentHost.Children.Add(Themed(new Label { Text = text.Resolve(MessageKeys.ForgeSettingsConfirmDestructiveDisclaimer) }, "MutedLabelStyle"));
        ContentHost.Children.Add(ProvenanceLabel(snapshot.ConfirmDestructiveProvenance));

        ContentHost.Children.Add(SectionDivider());
        ContentHost.Children.Add(GroupTitle(MessageKeys.ForgeSettingsProvidersGroupTitle));
        // Wires the previously-declared-but-unrendered ForgeSettingsProvidersEnabledLabel key: plan
        // 5.1's settings table lists this row with its own visible label, like every other row on
        // this page.
        ContentHost.Children.Add(Themed(new Label { Text = text.Resolve(MessageKeys.ForgeSettingsProvidersEnabledLabel) }, "MutedLabelStyle"));
        Dictionary<string, CheckBox> providerToggles = [];
        foreach (ProviderHealthEntry provider in snapshot.KnownProviders)
        {
            CheckBox toggle = new() { IsChecked = snapshot.ProvidersEnabled.Contains(provider.Id) };
            SemanticProperties.SetDescription(toggle, provider.Id);
            providerToggles[provider.Id] = toggle;
            // Mockup's model-row card (surface background, divider border) -- the closest existing
            // equivalent to that "models & providers" list this build actually has is this
            // enable/disable checkbox row (no ordering or per-row effort control exists to restyle;
            // see the final report). The small dot reuses ProviderStatusColor's already-typed
            // ProviderHealthEntry.State mapping instead of a fabricated indicator.
            // WidthRequest/MinimumHeightRequest (never a bare HeightRequest -- banned in this shell's
            // own code-behind by WorkspaceShellAccessibilityTests) act as this tiny undecorated dot's
            // only sizing input, which floors it to a fixed small square in practice.
            BoxView statusDot = new() { WidthRequest = 7, MinimumHeightRequest = 7, CornerRadius = 3.5, Color = ProviderStatusColor(provider) };
            Border row = Themed(new Border { Padding = ThemeSpace("Space3") }, "CardStyle");
            row.Stroke = ThemeColor("ColorDivider");
            row.StrokeThickness = 1;
            row.Content = new HorizontalStackLayout { Spacing = 8, Children = { toggle, statusDot, new Label { Text = provider.Id } } };
            ContentHost.Children.Add(row);
        }

        ContentHost.Children.Add(ProvenanceLabel(snapshot.ProvidersEnabledProvenance));

        ContentHost.Children.Add(SectionDivider());
        ContentHost.Children.Add(GroupTitle(MessageKeys.ForgeSettingsNotificationsGroupTitle));
        Switch notifications = new() { IsToggled = snapshot.NotificationsEnabled };
        SemanticProperties.SetDescription(notifications, text.Resolve(MessageKeys.ForgeSettingsNotificationsEnabledLabel));
        ContentHost.Children.Add(LabeledRow(MessageKeys.ForgeSettingsNotificationsEnabledLabel, notifications));
        ContentHost.Children.Add(ProvenanceLabel(snapshot.NotificationsEnabledProvenance));

        Label result = Themed(new Label(), "MutedLabelStyle");
        Button save = Themed(new Button { Text = text.Resolve(MessageKeys.SettingsSaveAction) }, "PrimaryButtonStyle");
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
        Button discard = Themed(new Button { Text = text.Resolve(MessageKeys.SettingsDiscardAction) }, "SecondaryButtonStyle");
        discard.Clicked += (_, _) => _ = RunAsync(RenderContentAsync);
        ContentHost.Children.Add(new HorizontalStackLayout { Spacing = 8, Children = { save, discard } });
        ContentHost.Children.Add(result);
    }

    private Label GroupTitle(string key) => Themed(BuildHeadingLabel(text.Resolve(key)), "HeadingLabelStyle");

    private static Label BuildHeadingLabel(string headingText)
    {
        Label label = new() { Text = headingText };
        SemanticProperties.SetHeadingLevel(label, SemanticHeadingLevel.Level2);
        return label;
    }

    private Label ProvenanceLabel(ConfigurationProvenance provenance) => Themed(new Label
    {
        Text = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{text.Resolve(MessageKeys.SettingsProvenanceLabel)}: {provenance}"),
    }, "MutedLabelStyle");

    /// <summary>Renders the control alongside its own visible, localized label -- PR #98 review
    /// finding 2: the previous implementation discarded <paramref name="labelKey"/> entirely, so
    /// confirm-destructive and notifications-enabled shipped as bare switches with no visible
    /// caption (only a screen-reader name).</summary>
    private HorizontalStackLayout LabeledRow(string labelKey, View control) => new()
    {
        Spacing = 8,
        Children = { new Label { Text = text.Resolve(labelKey) }, control },
    };

    /// <summary>Mockup's <c>&lt;hr class="hr"&gt;</c> section separator, between this page's own
    /// group titles (Language/Safety/Providers/Notifications) -- the section-nav rail itself is not
    /// reproduced (see the final report: the mockup's rail labels "Models &amp; providers"/"Approval
    /// mode"/"Theme" name settings this build does not have, and inventing new label text would mean
    /// adding new localization keys, out of scope for a visual-only pass).</summary>
    private static BoxView SectionDivider() => Themed(new BoxView(), "DividerBoxStyle");

    /// <summary>Semantic status-color mapping for a provider's already-typed
    /// <see cref="ProviderHealthEntry.State"/> -- reuses the same four palette colors the sprint
    /// workspace maps its own statuses to (App.xaml's own remarks), never a fabricated color and
    /// never parsed out of a localized display string (state here is a real enum, not text).</summary>
    private static Color ProviderStatusColor(ProviderHealthEntry provider) => provider.State switch
    {
        ProviderState.Ready => ThemeColor("ColorStatusGreen"),
        ProviderState.Failed or ProviderState.Missing => ThemeColor("ColorStatusRed"),
        ProviderState.Installing or ProviderState.Updating or ProviderState.Rechecking => ThemeColor("ColorStatusAmber"),
        // PR #112 review round 3 finding 4: this arm is rendered as a row's TextColor, so it needs
        // the 4.5:1 body-text floor -- ColorNeutral700 measured ~2.6:1 on the page ground.
        // ColorNeutral500 is this theme's muted-but-readable baseline (MutedLabelStyle's own color)
        // at ~6:1, and still reads as "no status to report" beside the three status hues above.
        _ => ThemeColor("ColorNeutral500"),
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
