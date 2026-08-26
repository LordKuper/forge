using Forge.Application;
using Forge.Desktop.Presentation;
using Forge.Localization;
using Forge.Providers;

namespace Forge.Desktop;

/// <summary>Plan section 4.2's project overview. Rendering only; every value and lifecycle action
/// goes through <see cref="ProjectOverviewViewModel"/> (which itself delegates to
/// <see cref="MainPageViewModel"/> for init/recover/create/run/resume/cancel).</summary>
public partial class WorkspaceShellPage
{
    private async Task RenderProjectOverviewAsync(Guid projectId, string root)
    {
        SidebarSnapshot sidebarSnapshot =
            await sidebar.LoadAsync(CancellationToken.None, projectId).ConfigureAwait(true);
        string? alias = sidebarSnapshot.Projects
            .FirstOrDefault(project => project.ProjectId == projectId)?.DisplayName;
        ProjectOverviewSnapshot snapshot =
            await projectOverview.LoadAsync(root, alias, CancellationToken.None).ConfigureAwait(true);

        // PR #98 review finding 7: DisplayName/Root/Initialized/StartupReady/Providers were already
        // computed on the snapshot but never bound -- plan 4.2 and CHANGELOG.md both claim the
        // overview shows startup/repository readiness and provider status.
        //
        // No mockup screen covers this page (it is not one of the three screens the Nocturne mockup
        // renders) -- styling here is by analogy with the mockup's general system (surface-filled
        // cards, muted secondary text, semantic status color for a real typed boolean/enum, never
        // for text parsed out of a localized display string -- see ProviderStatusColor's own
        // remarks).
        Label header = Styled(new Label { Text = snapshot.DisplayName }, "HeadingLabelStyle");
        SemanticProperties.SetHeadingLevel(header, SemanticHeadingLevel.Level1);
        ContentHost.Children.Add(header);
        ContentHost.Children.Add(Styled(new Label
        {
            Text = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{text.Resolve(MessageKeys.ProjectRootLabel)} {snapshot.Root}"),
        }, "MonoLabelStyle"));
        ContentHost.Children.Add(new Label
        {
            Text = text.Resolve(
                snapshot.Initialized ? MessageKeys.ProjectInitialized : MessageKeys.ProjectNotInitialized),
            TextColor = ResColor(snapshot.Initialized ? "ColorStatusGreenText" : "ColorStatusAmberText"),
        });
        ContentHost.Children.Add(new Label
        {
            Text = text.Resolve(snapshot.StartupReady ? MessageKeys.StartupReady : MessageKeys.StartupFailed),
            TextColor = ResColor(snapshot.StartupReady ? "ColorStatusGreenText" : "ColorStatusRedText"),
        });

        Label result = Styled(new Label(), "MutedLabelStyle");
        if (snapshot.InitializeEnabled)
        {
            Button initialize = Styled(new Button { Text = text.Resolve(MessageKeys.InitializeAction) }, "PrimaryButtonStyle");
            initialize.Clicked += (_, _) => _ = RunAsync(async () =>
            {
                ProjectSnapshot projectSnapshot =
                    await projectOverview.GetProjectSnapshotAsync(root, CancellationToken.None).ConfigureAwait(true);
                string action = text.Resolve(MessageKeys.InitializeAction);
                bool confirmed = await DisplayAlertAsync(
                        action, projectOverview.InitializePrompt(projectSnapshot), action,
                        text.Resolve(MessageKeys.CancelAction))
                    .ConfigureAwait(true);
                if (!confirmed)
                {
                    result.Text = text.Resolve(MessageKeys.InitConfirmationRequired);
                    return;
                }

                result.Text = await projectOverview
                    .InitializeAsync(projectSnapshot, CancellationToken.None)
                    .ConfigureAwait(true);
                await RenderContentAsync().ConfigureAwait(true);
            });
            ContentHost.Children.Add(initialize);
        }

        if (snapshot.RecoverEnabled)
        {
            Button recover = Styled(new Button { Text = text.Resolve(MessageKeys.RecoverAction) }, "SecondaryButtonStyle");
            recover.Clicked += (_, _) => _ = RunAsync(async () =>
            {
                string action = text.Resolve(MessageKeys.RecoverAction);
                bool confirmed = await DisplayAlertAsync(action, action, action, text.Resolve(MessageKeys.CancelAction))
                    .ConfigureAwait(true);
                result.Text = await projectOverview.RecoverAsync(root, confirmed, CancellationToken.None)
                    .ConfigureAwait(true);
                await RenderContentAsync().ConfigureAwait(true);
            });
            ContentHost.Children.Add(recover);
        }

        Button createSprint = Styled(new Button { Text = text.Resolve(MessageKeys.SprintCreateAction) }, "PrimaryButtonStyle");
        createSprint.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            result.Text = await projectOverview.CreateSprintAsync(root, CancellationToken.None).ConfigureAwait(true);
            await RenderContentAsync().ConfigureAwait(true);
        });
        ContentHost.Children.Add(createSprint);

        ContentHost.Children.Add(SectionDivider());
        ContentHost.Children.Add(GroupTitle(MessageKeys.ProjectOverviewActiveSprintsTitle));
        foreach (ProjectOverviewSprintCard card in snapshot.ActiveSprints)
        {
            ContentHost.Children.Add(BuildSprintCard(root, card, result));
        }

        if (snapshot.RecentHistory.Count > 0)
        {
            ContentHost.Children.Add(SectionDivider());
            ContentHost.Children.Add(GroupTitle(MessageKeys.ProjectOverviewHistoryTitle));
            foreach (ProjectOverviewSprintCard card in snapshot.RecentHistory)
            {
                Label historyLine = Styled(new Label
                {
                    Text = string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{card.CreationSequence}. {card.StateText}"),
                }, "MutedLabelStyle");
                ContentHost.Children.Add(historyLine);
            }
        }

        if (snapshot.SuggestedActions.Count > 0)
        {
            ContentHost.Children.Add(SectionDivider());
            ContentHost.Children.Add(GroupTitle(MessageKeys.SuggestedActionsTitle));
            foreach (AvailableAction action in snapshot.SuggestedActions)
            {
                ContentHost.Children.Add(new Label
                {
                    Text = string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{action.ActionId} ({(action.Enabled ? "enabled" : "blocked")})"),
                    // Semantic status color from the already-typed AvailableAction.Enabled bool
                    // (never parsed out of the localized ActionId), matching this pass's
                    // "blocked"-is-red convention.
                    TextColor = ResColor(action.Enabled ? "ColorText" : "ColorStatusRedText"),
                });
            }
        }

        if (snapshot.Providers.Count > 0)
        {
            // PR #98 review finding 7: provider integration status was computed but never rendered.
            // Reuses SurfaceFormatting.ProviderRow -- the same already-tested, parity-checked
            // per-provider projection `forge models` and the Forge settings page's own provider
            // section rely on -- rather than a new ad hoc rendering.
            ContentHost.Children.Add(SectionDivider());
            ContentHost.Children.Add(GroupTitle(MessageKeys.ProjectOverviewProvidersTitle));
            foreach (ProviderHealthEntry provider in snapshot.Providers)
            {
                ContentHost.Children.Add(new Label
                {
                    Text = SurfaceFormatting.ProviderRow(provider),
                    TextColor = ProviderStatusColor(provider),
                });
            }
        }

        ContentHost.Children.Add(result);
    }

    private Border BuildSprintCard(string root, ProjectOverviewSprintCard card, Label result)
    {
        VerticalStackLayout column = new() { Spacing = 6 };
        Label header = Styled(new Label
        {
            Text = string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{card.CreationSequence}. {card.StateText}"),
            // A sprint needing human attention is the one case this card's own typed data (not a
            // parsed status string) already flags -- amber, matching this pass's
            // "waiting for input"/"paused" convention.
            TextColor = ResColor(card.RequiresHumanAttention ? "ColorStatusAmberText" : "ColorText"),
        }, "HeadingLabelStyle");
        if (card.RequiresHumanAttention && card.AttentionReasonKey is { } reasonKey)
        {
            SemanticProperties.SetDescription(
                header,
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{header.Text}, {text.Resolve(reasonKey)}"));
        }

        column.Children.Add(header);
        Button run = Styled(new Button { Text = text.Resolve(MessageKeys.SprintRunAction) }, "PrimaryButtonStyle");
        run.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            result.Text = await projectOverview
                .RunSprintAsync(root, card.SprintId.ToString("D"), CancellationToken.None)
                .ConfigureAwait(true);
            await RenderContentAsync().ConfigureAwait(true);
        });
        Button resume = Styled(new Button { Text = text.Resolve(MessageKeys.SprintResumeAction) }, "SecondaryButtonStyle");
        resume.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            result.Text = await projectOverview
                .ResumeSprintAsync(root, card.SprintId.ToString("D"), CancellationToken.None)
                .ConfigureAwait(true);
            await RenderContentAsync().ConfigureAwait(true);
        });
        Button cancel = Styled(new Button { Text = text.Resolve(MessageKeys.SprintCancelAction) }, "SecondaryButtonStyle");
        cancel.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            string action = text.Resolve(MessageKeys.SprintCancelAction);
            bool confirmed = await DisplayAlertAsync(
                    action, projectOverview.SprintCancelPrompt(card.SprintId.ToString("D")), action,
                    text.Resolve(MessageKeys.CancelAction))
                .ConfigureAwait(true);
            result.Text = await projectOverview
                .CancelSprintAsync(root, card.SprintId.ToString("D"), confirmed, CancellationToken.None)
                .ConfigureAwait(true);
            await RenderContentAsync().ConfigureAwait(true);
        });
        Button open = Styled(new Button { Text = text.Resolve(MessageKeys.SprintIdLabel) }, "SecondaryButtonStyle");
        open.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            SidebarSnapshot snapshot = await sidebar.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            SidebarProjectItem? project = snapshot.Projects.FirstOrDefault(item => item.Root == root);
            if (project is not null)
            {
                await workspace
                    .NavigateAsync(
                        WorkspaceRoute.ToSprintWorkspace(project.ProjectId, root, card.SprintId), CancellationToken.None)
                    .ConfigureAwait(true);
            }
        });
        column.Children.Add(new HorizontalStackLayout { Spacing = 8, Children = { open, run, resume, cancel } });
        // Mockup's surface-filled card pattern (App.xaml's CardStyle), applied by analogy since no
        // mockup screen shows this page: each active/history sprint is a discrete list item, the
        // same shape the model rows and provider rows on the other two screens use.
        return Styled(new Border { Content = column }, "CardStyle");
    }
}
