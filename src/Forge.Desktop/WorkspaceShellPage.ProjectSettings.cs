using Forge.Desktop.Presentation;
using Forge.Localization;

namespace Forge.Desktop;

/// <summary>Plan section 5.2's project settings page. Rendering only; every value, provenance,
/// validation, and write goes through <see cref="ProjectSettingsViewModel"/>, and integration/
/// recovery/diagnostic actions reuse <see cref="MainPageViewModel"/> unchanged.</summary>
public partial class WorkspaceShellPage
{
    private async Task RenderProjectSettingsAsync(Guid projectId, string root)
    {
        SidebarSnapshot sidebarSnapshot = await sidebar.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        string? alias = sidebarSnapshot.Projects.FirstOrDefault(project => project.ProjectId == projectId)
            is { } project && project.DisplayName != System.IO.Path.GetFileName(root.TrimEnd('/', '\\'))
                ? project.DisplayName
                : null;
        ProjectSettingsSnapshot snapshot =
            await projectSettings.LoadAsync(projectId, root, alias, CancellationToken.None).ConfigureAwait(true);

        ContentHost.Children.Add(new Label
        {
            Text = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{text.Resolve(MessageKeys.ProjectSettingsRootLabel)}: {snapshot.Root}"),
        });
        ContentHost.Children.Add(new Label
        {
            Text = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{text.Resolve(MessageKeys.ProjectSettingsProjectIdLabel)}: {snapshot.ProjectId:D}"),
        });

        Entry aliasEntry = Describe(new Entry { Text = snapshot.Alias }, text.Resolve(MessageKeys.ProjectSettingsAliasLabel));
        Button aliasSave = new() { Text = text.Resolve(MessageKeys.SettingsSaveAction) };
        Label result = new();
        aliasSave.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            // PR #98 review finding 4: report the alias write's real outcome instead of an
            // unconditional "saved" -- SetAliasAsync now returns the actual, already-localized
            // result (success or failure).
            result.Text = await projectSettings.SetAliasAsync(projectId, aliasEntry.Text, CancellationToken.None)
                .ConfigureAwait(true);
        });
        ContentHost.Children.Add(new HorizontalStackLayout { Children = { aliasEntry, aliasSave } });

        Entry userFacing = Describe(
            new Entry { Text = snapshot.UserFacingLanguage }, text.Resolve(MessageKeys.ProjectSettingsUserFacingLanguageLabel));
        ContentHost.Children.Add(userFacing);
        ContentHost.Children.Add(ProvenanceLabel(snapshot.UserFacingLanguageProvenance));
        Entry agentFacing = Describe(
            new Entry { Text = snapshot.AgentFacingLanguage }, text.Resolve(MessageKeys.ProjectSettingsAgentFacingLanguageLabel));
        ContentHost.Children.Add(agentFacing);
        ContentHost.Children.Add(ProvenanceLabel(snapshot.AgentFacingLanguageProvenance));
        Entry tokenBudget = Describe(
            new Entry { Text = snapshot.TokenBudget.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            text.Resolve(MessageKeys.ProjectSettingsTokenBudgetLabel));
        ContentHost.Children.Add(tokenBudget);
        ContentHost.Children.Add(ProvenanceLabel(snapshot.TokenBudgetProvenance));
        Entry allowedModels = Describe(
            new Entry { Text = string.Join(", ", snapshot.AllowedModels) },
            text.Resolve(MessageKeys.ProjectSettingsAllowedModelsLabel));
        ContentHost.Children.Add(allowedModels);
        ContentHost.Children.Add(ProvenanceLabel(snapshot.AllowedModelsProvenance));

        Button save = new() { Text = text.Resolve(MessageKeys.SettingsSaveAction) };
        save.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            ProjectSettingsEdit edit = new()
            {
                UserFacingLanguage = userFacing.Text ?? string.Empty,
                AgentFacingLanguage = agentFacing.Text ?? string.Empty,
                TokenBudget = int.TryParse(
                    tokenBudget.Text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int parsed)
                    ? parsed
                    : -1,
                AllowedModels = [.. (allowedModels.Text ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            };
            ProjectSettingsSaveResult saveResult = await projectSettings
                .SaveAsync(root, edit, snapshot, CancellationToken.None)
                .ConfigureAwait(true);
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

        Button relink = new() { Text = text.Resolve(MessageKeys.ProjectSettingsRelinkAction) };
        relink.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            result.Text = await projectSettings.RelinkAsync(projectId, CancellationToken.None).ConfigureAwait(true);
        });
        ContentHost.Children.Add(relink);

        Button removeFromCatalog = new() { Text = text.Resolve(MessageKeys.ProjectSettingsRemoveFromCatalogAction) };
        removeFromCatalog.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            await projectSettings.RemoveFromCatalogAsync(projectId, CancellationToken.None).ConfigureAwait(true);
            await workspace.RestoreAsync(CancellationToken.None).ConfigureAwait(true);
            await RenderSidebarAsync().ConfigureAwait(true);
            await RenderContentAsync().ConfigureAwait(true);
        });
        ContentHost.Children.Add(removeFromCatalog);

        Button recover = new() { Text = text.Resolve(MessageKeys.RecoverAction) };
        recover.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            string action = text.Resolve(MessageKeys.RecoverAction);
            bool confirmed = await DisplayAlertAsync(action, action, action, text.Resolve(MessageKeys.CancelAction))
                .ConfigureAwait(true);
            result.Text = await projectSettings.RecoverAsync(root, confirmed, CancellationToken.None)
                .ConfigureAwait(true);
        });
        ContentHost.Children.Add(recover);

        ContentHost.Children.Add(BuildIntegrationSection(root, result));

        Button diagnosticBundle = new() { Text = text.Resolve(MessageKeys.ProjectSettingsDiagnosticBundleAction) };
        Label bundleOutput = new();
        diagnosticBundle.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            bundleOutput.Text = await projectSettings.GenerateDiagnosticBundleAsync(root, CancellationToken.None)
                .ConfigureAwait(true);
        });
        ContentHost.Children.Add(diagnosticBundle);
        ContentHost.Children.Add(bundleOutput);
    }

    private VerticalStackLayout BuildIntegrationSection(string root, Label result)
    {
        Label integrationPreview = new();
        Button generate = new() { Text = text.Resolve(MessageKeys.IntegrationGenerateAction) };
        generate.Clicked += (_, _) => _ = RunAsync(async () =>
            integrationPreview.Text = await projectSettings
                .GenerateIntegrationPreviewAsync(root, CancellationToken.None)
                .ConfigureAwait(true));
        Button install = new() { Text = text.Resolve(MessageKeys.IntegrationInstallAction) };
        install.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            string action = text.Resolve(MessageKeys.IntegrationInstallAction);
            bool confirmed = await DisplayAlertAsync(action, RootPrompt(root), action, text.Resolve(MessageKeys.CancelAction))
                .ConfigureAwait(true);
            result.Text = await projectSettings.InstallIntegrationAsync(root, confirmed, CancellationToken.None)
                .ConfigureAwait(true);
        });
        Button remove = new() { Text = text.Resolve(MessageKeys.IntegrationRemoveAction) };
        remove.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            string action = text.Resolve(MessageKeys.IntegrationRemoveAction);
            bool confirmed = await DisplayAlertAsync(action, RootPrompt(root), action, text.Resolve(MessageKeys.CancelAction))
                .ConfigureAwait(true);
            result.Text = await projectSettings.RemoveIntegrationAsync(root, confirmed, CancellationToken.None)
                .ConfigureAwait(true);
        });
        return new VerticalStackLayout
        {
            Children = { new HorizontalStackLayout { Children = { generate, install, remove } }, integrationPreview },
        };
    }

    private string RootPrompt(string root) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture, $"{text.Resolve(MessageKeys.ProjectRootLabel)} {root}");
}
