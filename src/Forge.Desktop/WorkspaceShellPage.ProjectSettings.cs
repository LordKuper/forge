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
        SidebarSnapshot sidebarSnapshot =
            await sidebar.LoadAsync(CancellationToken.None, projectId).ConfigureAwait(true);
        string? alias = sidebarSnapshot.Projects.FirstOrDefault(project => project.ProjectId == projectId)
            is { } project && project.DisplayName != System.IO.Path.GetFileName(root.TrimEnd('/', '\\'))
                ? project.DisplayName
                : null;
        ProjectSettingsSnapshot snapshot =
            await projectSettings.LoadAsync(projectId, root, alias, CancellationToken.None).ConfigureAwait(true);

        // Mockup's "Repository" card (a read-only local-path field in a bordered section) -- the
        // closest existing equivalent is this identity block (root path, project id, editable
        // alias), so it gets the same OutlinedPanelStyle-derived container; Root/ProjectId use
        // MonoLabelStyle since both are technical path/identifier text, matching the mockup's own
        // monospace treatment of its local-path field.
        VerticalStackLayout identityCard = new() { Spacing = 6 };
        identityCard.Children.Add(Themed(new Label
        {
            Text = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{text.Resolve(MessageKeys.ProjectSettingsRootLabel)}: {snapshot.Root}"),
        }, "MonoLabelStyle"));
        identityCard.Children.Add(Themed(new Label
        {
            Text = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{text.Resolve(MessageKeys.ProjectSettingsProjectIdLabel)}: {snapshot.ProjectId:D}"),
        }, "MonoLabelStyle"));

        Entry aliasEntry = Describe(new Entry { Text = snapshot.Alias }, text.Resolve(MessageKeys.ProjectSettingsAliasLabel));
        Button aliasSave = Themed(new Button { Text = text.Resolve(MessageKeys.SettingsSaveAction) }, "SecondaryButtonStyle");
        Label result = Themed(new Label(), "MutedLabelStyle");
        aliasSave.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            // PR #98 review finding 4: report the alias write's real outcome instead of an
            // unconditional "saved" -- SetAliasAsync now returns the actual, already-localized
            // result (success or failure).
            result.Text = await projectSettings.SetAliasAsync(projectId, aliasEntry.Text, CancellationToken.None)
                .ConfigureAwait(true);
        });
        identityCard.Children.Add(new HorizontalStackLayout { Spacing = 8, Children = { aliasEntry, aliasSave } });
        Border identityPanel = Themed(new Border { Content = identityCard, Padding = ThemeSpace("Space4") }, "OutlinedPanelStyle");
        identityPanel.BackgroundColor = ThemeColor("ColorSurface");
        ContentHost.Children.Add(identityPanel);

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

        Button save = Themed(new Button { Text = text.Resolve(MessageKeys.SettingsSaveAction) }, "PrimaryButtonStyle");
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
            // PR #102 round 1 review: this is the only Desktop caller of the gated
            // `configuration.manage` capability -- a bare Succeeded ternary discarded
            // saveResult.DiagnosticCode entirely, so a CapabilityNotSupported rejection rendered as
            // the same generic "settings validation failed" as any other cause. Route through the
            // shared Message helper (WorkspaceShellPage.xaml.cs), the same pattern already used for
            // every other Desktop save/action failure in this class (e.g.
            // WorkspaceShellPage.SprintWorkspace.cs's stale-refresh and rewind-reason-save results).
            result.Text = Message(
                text.Resolve(saveResult.Succeeded ? MessageKeys.SettingsSaved : MessageKeys.SettingsValidationFailed),
                saveResult.DiagnosticCode);
            if (saveResult.Succeeded)
            {
                await RenderContentAsync().ConfigureAwait(true);
            }
        });
        Button discard = Themed(new Button { Text = text.Resolve(MessageKeys.SettingsDiscardAction) }, "SecondaryButtonStyle");
        discard.Clicked += (_, _) => _ = RunAsync(RenderContentAsync);
        ContentHost.Children.Add(new HorizontalStackLayout { Spacing = 8, Children = { save, discard } });
        ContentHost.Children.Add(result);

        ContentHost.Children.Add(SectionDivider());
        Button relink = Themed(new Button { Text = text.Resolve(MessageKeys.ProjectSettingsRelinkAction) }, "SecondaryButtonStyle");
        relink.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            result.Text = await projectSettings.RelinkAsync(projectId, CancellationToken.None).ConfigureAwait(true);
        });
        ContentHost.Children.Add(relink);

        Button recover = Themed(new Button { Text = text.Resolve(MessageKeys.RecoverAction) }, "SecondaryButtonStyle");
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

        Button diagnosticBundle = Themed(new Button { Text = text.Resolve(MessageKeys.ProjectSettingsDiagnosticBundleAction) }, "SecondaryButtonStyle");
        Label bundleOutput = Themed(new Label(), "MutedLabelStyle");
        diagnosticBundle.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            bundleOutput.Text = await projectSettings.GenerateDiagnosticBundleAsync(root, CancellationToken.None)
                .ConfigureAwait(true);
        });
        ContentHost.Children.Add(diagnosticBundle);
        ContentHost.Children.Add(bundleOutput);

        // Mockup's "Danger zone" card: this build's closest equivalent to its "Remove project"
        // action (detaches without touching the repository on disk) is RemoveFromCatalogAction --
        // same destructive-but-recoverable semantics, so it gets the same red-tinted container and
        // DangerButtonStyle rather than a fabricated "Danger zone" heading (that text has no
        // localization key today; see the final report).
        ContentHost.Children.Add(SectionDivider());
        Button removeFromCatalog =
            Themed(new Button { Text = text.Resolve(MessageKeys.ProjectSettingsRemoveFromCatalogAction) }, "DangerButtonStyle");
        removeFromCatalog.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            await projectSettings.RemoveFromCatalogAsync(projectId, CancellationToken.None).ConfigureAwait(true);
            await workspace.RestoreAsync(CancellationToken.None).ConfigureAwait(true);
            await RenderSidebarAsync().ConfigureAwait(true);
            await RenderContentAsync().ConfigureAwait(true);
        });
        ContentHost.Children.Add(Themed(new Border { Content = removeFromCatalog }, "DangerCardStyle"));
    }

    private VerticalStackLayout BuildIntegrationSection(string root, Label result)
    {
        Label integrationPreview = Themed(new Label(), "MutedLabelStyle");
        Button generate = Themed(new Button { Text = text.Resolve(MessageKeys.IntegrationGenerateAction) }, "SecondaryButtonStyle");
        generate.Clicked += (_, _) => _ = RunAsync(async () =>
            integrationPreview.Text = await projectSettings
                .GenerateIntegrationPreviewAsync(root, CancellationToken.None)
                .ConfigureAwait(true));
        Button install = Themed(new Button { Text = text.Resolve(MessageKeys.IntegrationInstallAction) }, "SecondaryButtonStyle");
        install.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            string action = text.Resolve(MessageKeys.IntegrationInstallAction);
            bool confirmed = await DisplayAlertAsync(action, RootPrompt(root), action, text.Resolve(MessageKeys.CancelAction))
                .ConfigureAwait(true);
            result.Text = await projectSettings.InstallIntegrationAsync(root, confirmed, CancellationToken.None)
                .ConfigureAwait(true);
        });
        Button remove = Themed(new Button { Text = text.Resolve(MessageKeys.IntegrationRemoveAction) }, "SecondaryButtonStyle");
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
            Children = { new HorizontalStackLayout { Spacing = 8, Children = { generate, install, remove } }, integrationPreview },
        };
    }

    private string RootPrompt(string root) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture, $"{text.Resolve(MessageKeys.ProjectRootLabel)} {root}");
}
