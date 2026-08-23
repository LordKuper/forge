using Forge.Application;
using Forge.Configuration;
using Forge.Desktop.Presentation;
using Forge.Localization;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ForgeSettingsViewModelTests
{
    private static ForgeSettingsViewModel Create(TestEnvironment environment, SurfaceTextProvider? text = null) =>
        new(environment.Application, environment.Resolve<ProviderCatalog>(), text ?? new(new ResourceLocalizationCatalog(), "en"));

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncReportsBuiltInDefaultsWithTheirProvenance()
    {
        using TestEnvironment environment = new();
        ForgeSettingsViewModel viewModel = Create(environment);

        ForgeSettingsSnapshot snapshot = await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("en", snapshot.LanguageUi);
        Assert.Equal(ConfigurationProvenance.BuiltInDefault, snapshot.LanguageUiProvenance);
        Assert.Null(snapshot.LanguageInteraction);
        Assert.Null(snapshot.LanguageLlm);
        Assert.True(snapshot.ConfirmDestructive);
        Assert.True(snapshot.NotificationsEnabled);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateRejectsAnUnsupportedUiLanguage()
    {
        ForgeSettingsSnapshot snapshot = new(
            "en", ConfigurationProvenance.BuiltInDefault, null, ConfigurationProvenance.BuiltInDefault, null,
            ConfigurationProvenance.BuiltInDefault, true, ConfigurationProvenance.BuiltInDefault, [],
            ConfigurationProvenance.BuiltInDefault, [], true, ConfigurationProvenance.BuiltInDefault, ["en", "ru"],
            DiagnosticCodes.None);
        ForgeSettingsEdit edit = ForgeSettingsEdit.From(snapshot);
        edit.LanguageUi = "fr";

        IReadOnlyList<string> errors = ForgeSettingsViewModel.Validate(edit, snapshot);

        Assert.Contains(MessageKeys.SettingsLanguageUnsupported, errors);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateRejectsAProviderThisBuildDoesNotKnow()
    {
        ForgeSettingsSnapshot snapshot = new(
            "en", ConfigurationProvenance.BuiltInDefault, null, ConfigurationProvenance.BuiltInDefault, null,
            ConfigurationProvenance.BuiltInDefault, true, ConfigurationProvenance.BuiltInDefault, [],
            ConfigurationProvenance.BuiltInDefault, [], true, ConfigurationProvenance.BuiltInDefault, ["en", "ru"],
            DiagnosticCodes.None);
        ForgeSettingsEdit edit = ForgeSettingsEdit.From(snapshot);
        edit.ProvidersEnabled = ["unknown_provider"];

        IReadOnlyList<string> errors = ForgeSettingsViewModel.Validate(edit, snapshot);

        Assert.Contains(MessageKeys.SettingsUnknownProvider, errors);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SaveAsyncRejectsAnInvalidEditWithoutWritingAnything()
    {
        using TestEnvironment environment = new();
        ForgeSettingsViewModel viewModel = Create(environment);
        ForgeSettingsSnapshot before = await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        ForgeSettingsEdit edit = ForgeSettingsEdit.From(before);
        edit.LanguageUi = "fr";

        ForgeSettingsSaveResult result =
            await viewModel.SaveAsync(edit, before, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.ValidationErrorKeys);
        ForgeSettingsSnapshot after = await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("en", after.LanguageUi);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SaveAsyncWritesOnlyTheChangedKeysAndUpdatesTheirProvenance()
    {
        using TestEnvironment environment = new();
        ForgeSettingsViewModel viewModel = Create(environment);
        ForgeSettingsSnapshot before = await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        ForgeSettingsEdit edit = ForgeSettingsEdit.From(before);
        edit.NotificationsEnabled = false;

        ForgeSettingsSaveResult result =
            await viewModel.SaveAsync(edit, before, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        ForgeSettingsSnapshot after = await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        Assert.False(after.NotificationsEnabled);
        Assert.Equal(ConfigurationProvenance.User, after.NotificationsEnabledProvenance);
        Assert.Equal(ConfigurationProvenance.BuiltInDefault, after.ConfirmDestructiveProvenance);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SavingALanguageUiChangeUpdatesSurfaceTextWithoutARestart()
    {
        using TestEnvironment environment = new();
        SurfaceTextProvider text = new(new ResourceLocalizationCatalog(), "en");
        ForgeSettingsViewModel viewModel = Create(environment, text);
        ForgeSettingsSnapshot before = await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        ForgeSettingsEdit edit = ForgeSettingsEdit.From(before);
        edit.LanguageUi = "ru";
        int changedCalls = 0;
        text.Changed += (_, _) => changedCalls++;

        ForgeSettingsSaveResult result =
            await viewModel.SaveAsync(edit, before, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, changedCalls);
        Assert.Equal("Forge готов.", text.Resolve(MessageKeys.StatusReady));
    }
}
