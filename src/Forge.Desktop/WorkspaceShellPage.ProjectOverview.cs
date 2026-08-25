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
        Label header = new() { Text = snapshot.DisplayName };
        SemanticProperties.SetHeadingLevel(header, SemanticHeadingLevel.Level1);
        ContentHost.Children.Add(header);
        ContentHost.Children.Add(new Label
        {
            Text = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{text.Resolve(MessageKeys.ProjectRootLabel)} {snapshot.Root}"),
        });
        ContentHost.Children.Add(new Label
        {
            Text = text.Resolve(
                snapshot.Initialized ? MessageKeys.ProjectInitialized : MessageKeys.ProjectNotInitialized),
        });
        ContentHost.Children.Add(new Label
        {
            Text = text.Resolve(snapshot.StartupReady ? MessageKeys.StartupReady : MessageKeys.StartupFailed),
        });

        Label result = new();
        if (snapshot.InitializeEnabled)
        {
            Button initialize = new() { Text = text.Resolve(MessageKeys.InitializeAction) };
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
            Button recover = new() { Text = text.Resolve(MessageKeys.RecoverAction) };
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

        Button createSprint = new() { Text = text.Resolve(MessageKeys.SprintCreateAction) };
        createSprint.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            result.Text = await projectOverview.CreateSprintAsync(root, CancellationToken.None).ConfigureAwait(true);
            await RenderContentAsync().ConfigureAwait(true);
        });
        ContentHost.Children.Add(createSprint);

        ContentHost.Children.Add(GroupTitle(MessageKeys.ProjectOverviewActiveSprintsTitle));
        foreach (ProjectOverviewSprintCard card in snapshot.ActiveSprints)
        {
            ContentHost.Children.Add(BuildSprintCard(root, card, result));
        }

        if (snapshot.RecentHistory.Count > 0)
        {
            ContentHost.Children.Add(GroupTitle(MessageKeys.ProjectOverviewHistoryTitle));
            foreach (ProjectOverviewSprintCard card in snapshot.RecentHistory)
            {
                Label historyLine = new()
                {
                    Text = string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{card.CreationSequence}. {card.StateText}"),
                };
                ContentHost.Children.Add(historyLine);
            }
        }

        if (snapshot.SuggestedActions.Count > 0)
        {
            ContentHost.Children.Add(GroupTitle(MessageKeys.SuggestedActionsTitle));
            foreach (AvailableAction action in snapshot.SuggestedActions)
            {
                ContentHost.Children.Add(new Label
                {
                    Text = string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{action.ActionId} ({(action.Enabled ? "enabled" : "blocked")})"),
                });
            }
        }

        if (snapshot.Providers.Count > 0)
        {
            // PR #98 review finding 7: provider integration status was computed but never rendered.
            // Reuses SurfaceFormatting.ProviderRow -- the same already-tested, parity-checked
            // per-provider projection `forge models` and the Forge settings page's own provider
            // section rely on -- rather than a new ad hoc rendering.
            ContentHost.Children.Add(GroupTitle(MessageKeys.ProjectOverviewProvidersTitle));
            foreach (ProviderHealthEntry provider in snapshot.Providers)
            {
                ContentHost.Children.Add(new Label { Text = SurfaceFormatting.ProviderRow(provider) });
            }
        }

        ContentHost.Children.Add(result);
    }

    private VerticalStackLayout BuildSprintCard(string root, ProjectOverviewSprintCard card, Label result)
    {
        VerticalStackLayout column = new();
        Label header = new()
        {
            Text = string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{card.CreationSequence}. {card.StateText}"),
        };
        if (card.RequiresHumanAttention && card.AttentionReasonKey is { } reasonKey)
        {
            SemanticProperties.SetDescription(
                header,
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{header.Text}, {text.Resolve(reasonKey)}"));
        }

        column.Children.Add(header);
        Button run = new() { Text = text.Resolve(MessageKeys.SprintRunAction) };
        run.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            result.Text = await projectOverview
                .RunSprintAsync(root, card.SprintId.ToString("D"), CancellationToken.None)
                .ConfigureAwait(true);
            await RenderContentAsync().ConfigureAwait(true);
        });
        Button resume = new() { Text = text.Resolve(MessageKeys.SprintResumeAction) };
        resume.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            result.Text = await projectOverview
                .ResumeSprintAsync(root, card.SprintId.ToString("D"), CancellationToken.None)
                .ConfigureAwait(true);
            await RenderContentAsync().ConfigureAwait(true);
        });
        Button cancel = new() { Text = text.Resolve(MessageKeys.SprintCancelAction) };
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
        Button open = new() { Text = text.Resolve(MessageKeys.SprintIdLabel) };
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
        column.Children.Add(new HorizontalStackLayout { Children = { open, run, resume, cancel } });
        return column;
    }
}
