using System.Globalization;
using Forge.Application;
using Forge.Compiler;
using Forge.Desktop.Presentation;
using Forge.Domain;
using Forge.Localization;

namespace Forge.Desktop;

/// <summary>
/// Plan section 4.3's sprint workspace: a sticky status header (<see cref="StickyHeaderHost"/>), a
/// chronological timeline with incremental loading/filters/unread tracking/copy/technical-detail
/// expansion, and a contextual-action renderer for every typed control Slice 4's
/// <see cref="AvailableAction"/> projection and Slice 2/3's stop/stage-transition capabilities
/// describe. Every destructive or history-invalidating action here shows its exact target and
/// consequences before confirming (plan 4.3/12.4/12.5), and <c>confirmed</c> is always the
/// confirmation dialog's own answer, never a literal <see langword="true"/> -- the exact bug class
/// Slice 5's review caught twice (see <c>WorkspaceShellPage.xaml.cs</c>'s own remarks).
/// Gate/confirm/test-work/finalize/supersede no longer collect a node or attempt id from a manual
/// entry field (plan 11 Slice 6 item 3): a node id of <see langword="null"/> resolves to the
/// built-in graph's own canonical node, and the active attempt id is derived from
/// <see cref="SprintWorkspaceViewModel.FindActiveAttemptId"/>.
/// </summary>
public partial class WorkspaceShellPage
{
    /// <summary>Matches the executor tick cadence (<c>PlanningExecutionOptions.Interval</c> et al.,
    /// all default to 15 seconds) -- the fastest cadence anything in this codebase actually produces
    /// new timeline events at, so polling faster would only add traffic without ever finding
    /// anything sooner (plan 10: "bounded interval").</summary>
    private static readonly TimeSpan TimelinePollInterval = TimeSpan.FromSeconds(15);

    private readonly Dictionary<Guid, double> sprintScrollPositions = [];
    private IDispatcherTimer? timelinePollTimer;
    private Guid scrollTrackedSprintId;
    private bool scrollHandlerAttached;

    private void StopTimelinePoll()
    {
        timelinePollTimer?.Stop();
        timelinePollTimer = null;
    }

    private async Task RenderSprintWorkspaceAsync(string root, Guid sprintId)
    {
        ProjectCatalogListing listing = await catalog.ListAsync(CancellationToken.None).ConfigureAwait(true);
        string? alias = listing.Entries.FirstOrDefault(entry => entry.Root == root)?.Alias;
        string projectDisplayName = ProjectDisplayName.Resolve(root, alias);

        Label detailsLabel = new() { IsVisible = false };
        VerticalStackLayout timelineItemsHost = new();
        Label timelineStatusLabel = new();
        Picker filterPicker = new();
        Label copyNoticeLabel = new();
        Button loadMoreButton = new() { Text = text.Resolve(MessageKeys.TimelineLoadMoreAction) };
        Label gateResult = new();
        Label supersedeResult = new();
        Label confirmResult = new();
        Label testWorkResult = new();
        Label finalizeResult = new();
        Label lifecycleResult = new();
        Label stopResult = new();
        Label moveResult = new();
        Entry rewindReasonEntry = Describe(new Entry(), text.Resolve(MessageKeys.ActionRewindReasonLabel));
        SprintDetails? currentDetails = null;
        bool timelineInitialized = false;

        async Task RefreshHeaderAsync()
        {
            (SprintStatusHeaderData header, ProjectSnapshot snapshot) = await sprintWorkspace
                .RefreshHeaderAsync(root, projectDisplayName, sprintId, CancellationToken.None)
                .ConfigureAwait(true);
            currentDetails = snapshot.Details;
            StickyHeaderHost.Children.Clear();
            string stateText = header.SprintStateText == "paused"
                ? text.Resolve(MessageKeys.SprintStatePaused)
                : header.SprintStateText;
            StickyHeaderHost.Children.Add(Describe(new Label
            {
                Text = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{header.ProjectDisplayName} - {text.Resolve(MessageKeys.SprintIdLabel)} {header.SprintSequence} - {stateText}"),
                FontAttributes = FontAttributes.Bold,
            }));
            StickyHeaderHost.Children.Add(Describe(new Label
            {
                Text = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{text.Resolve(MessageKeys.SprintStatusHeaderStageLabel)}: {header.CurrentStageId ?? "-"}  " +
                        $"{text.Resolve(MessageKeys.SprintStatusHeaderProgressLabel)}: {header.StagesCompleted}/{header.StagesTotal}  " +
                        $"{text.Resolve(MessageKeys.FindingsLabel)}: {header.OpenFindingsCount}"),
            }));
            string lastActivityText = header.LastActivityAt is { } activity
                ? activity.ToString("O", CultureInfo.InvariantCulture)
                : "-";
            string resumeNotBeforeText = header.ResumeNotBefore is { } resumeAt
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {text.Resolve(MessageKeys.SprintStatusHeaderResumeNotBeforeLabel)}: {resumeAt:O}")
                : string.Empty;
            StickyHeaderHost.Children.Add(Describe(new Label
            {
                Text = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{text.Resolve(MessageKeys.SprintStatusHeaderLastActivityLabel)}: {lastActivityText}  " +
                        $"{text.Resolve(MessageKeys.RoutingLabel)} retry_remaining={header.RetryRemaining}") +
                    resumeNotBeforeText,
            }));
            StickyHeaderHost.Children.Add(Describe(new Label { Text = header.ActiveProviderModelText }));
            Button detailsToggle = new() { Text = text.Resolve(MessageKeys.SprintStatusHeaderDetailsAction) };
            detailsToggle.Clicked += (_, _) =>
            {
                detailsLabel.Text = header.DetailsText;
                Describe(detailsLabel);
                detailsLabel.IsVisible = !detailsLabel.IsVisible;
            };
            StickyHeaderHost.Children.Add(detailsToggle);
            StickyHeaderHost.Children.Add(detailsLabel);
        }

        void RenderTimelineItems(TimelineState state)
        {
            timelineItemsHost.Children.Clear();
            if (state.Items.Count == 0)
            {
                timelineItemsHost.Children.Add(Describe(new Label { Text = text.Resolve(MessageKeys.TimelineNoItems) }));
            }

            foreach (TimelineItemView item in state.Items)
            {
                VerticalStackLayout row = new();
                Label summary = Describe(new Label
                {
                    Text = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{(item.Unread ? "* " : "  ")}{item.OccurredAt:O} [{item.Type}/{item.ActorText}] {item.MessageText}"),
                });
                row.Children.Add(summary);
                string argumentsText = string.Join(
                    Environment.NewLine,
                    item.Arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => string.Create(CultureInfo.InvariantCulture, $"  {pair.Key}={pair.Value}")));
                Label technicalDetail = Describe(new Label
                {
                    IsVisible = false,
                    Text = string.Create(
                        CultureInfo.InvariantCulture,
                        $"correlation={item.CorrelationId} causation={item.CausationId}\n{argumentsText}"),
                });
                Button detailsButton = new() { Text = text.Resolve(MessageKeys.TimelineDetailsAction) };
                detailsButton.Clicked += (_, _) => technicalDetail.IsVisible = !technicalDetail.IsVisible;
                Button copyButton = new() { Text = text.Resolve(MessageKeys.TimelineCopyAction) };
                copyButton.Clicked += (_, _) => _ = RunAsync(async () =>
                {
                    await Clipboard.Default.SetTextAsync(item.CopyText).ConfigureAwait(true);
                    copyNoticeLabel.Text = text.Resolve(MessageKeys.TimelineCopiedNotice);
                });
                row.Children.Add(new HorizontalStackLayout { Children = { detailsButton, copyButton } });
                row.Children.Add(technicalDetail);
                timelineItemsHost.Children.Add(row);
            }

            loadMoreButton.IsVisible = state.HasMore;
            timelineStatusLabel.Text = state.UnreadCount > 0
                ? string.Format(CultureInfo.InvariantCulture, text.Resolve(MessageKeys.TimelineUnreadLabel), state.UnreadCount)
                : string.Empty;
            List<string> options = [text.Resolve(MessageKeys.TimelineFilterAllOption), .. state.AvailableFilterTypes];
            filterPicker.ItemsSource = options;
            filterPicker.SelectedItem = state.ActiveFilterType ?? options[0];
        }

        async Task InitializeTimelineAsync()
        {
            TimelineState state = await sprintWorkspace.Timeline
                .InitializeAsync(workspace.Route.ProjectId!.Value, root, sprintId, CancellationToken.None)
                .ConfigureAwait(true);
            rewindReasonEntry.Text = await sprintWorkspace.Timeline.LoadDraftAsync(CancellationToken.None).ConfigureAwait(true);
            RenderTimelineItems(state);
            timelineInitialized = true;
        }

        async Task RefreshActionsAsync()
        {
            ContextualActionHost.Children.Clear();
            IReadOnlyList<AvailableAction> actions = await sprintWorkspace.Actions
                .LoadAsync(root, sprintId, CancellationToken.None)
                .ConfigureAwait(true);
            ContextualActionHost.Children.Add(Describe(new Label { Text = text.Resolve(MessageKeys.ActionsTitle) }));
            if (actions.Count == 0)
            {
                ContextualActionHost.Children.Add(
                    Describe(new Label { Text = text.Resolve(MessageKeys.ActionsNoneAvailable) }));
            }

            AddLifecycleAction(
                actions, AvailableActionProjector.RunSprintActionId, text.Resolve(MessageKeys.SprintRunAction),
                async () =>
                {
                    string message = await sprintWorkspace.RunSprintAsync(root, sprintId, CancellationToken.None)
                        .ConfigureAwait(true);
                    await RefreshAllAsync().ConfigureAwait(true);
                    lifecycleResult.Text = message;
                });
            AddLifecycleAction(
                actions, AvailableActionProjector.ResumeSprintActionId, text.Resolve(MessageKeys.SprintResumeAction),
                async () =>
                {
                    string message = await sprintWorkspace.ResumeSprintAsync(root, sprintId, CancellationToken.None)
                        .ConfigureAwait(true);
                    await RefreshAllAsync().ConfigureAwait(true);
                    lifecycleResult.Text = message;
                });
            AddLifecycleAction(
                actions, AvailableActionProjector.CancelSprintActionId, text.Resolve(MessageKeys.SprintCancelAction),
                async () =>
                {
                    string action = text.Resolve(MessageKeys.SprintCancelAction);
                    bool dialogConfirmed = await DisplayAlertAsync(
                            action, sprintWorkspace.SprintCancelPrompt(sprintId), action, text.Resolve(MessageKeys.CancelAction))
                        .ConfigureAwait(true);
                    string message = await sprintWorkspace
                        .CancelSprintAsync(root, sprintId, dialogConfirmed, CancellationToken.None)
                        .ConfigureAwait(true);
                    await RefreshAllAsync().ConfigureAwait(true);
                    lifecycleResult.Text = message;
                });
            ContextualActionHost.Children.Add(lifecycleResult);

            AvailableAction? stop = SprintActionsViewModel.Find(actions, AvailableActionProjector.StopCurrentOperationActionId);
            if (stop is not null)
            {
                Button stopButton = new() { Text = text.Resolve(MessageKeys.AttemptStopAction) };
                stopButton.Clicked += (_, _) => _ = RunAsync(async () =>
                {
                    // Plan 12.4/12.5: re-validate immediately before the confirmation dialog, never
                    // trust the row that was already on screen.
                    AvailableAction? fresh = await sprintWorkspace.Actions
                        .FindFreshStopTargetAsync(root, sprintId, CancellationToken.None)
                        .ConfigureAwait(true);
                    if (fresh is null)
                    {
                        stopResult.Text = text.Resolve(MessageKeys.AttemptStopNoLongerActive);
                        await RefreshAllAsync().ConfigureAwait(true);
                        return;
                    }

                    string action = text.Resolve(MessageKeys.AttemptStopAction);
                    bool confirmed = await DisplayAlertAsync(
                            action, sprintWorkspace.Actions.StopPrompt(fresh), action, text.Resolve(MessageKeys.CancelAction))
                        .ConfigureAwait(true);
                    if (!confirmed)
                    {
                        stopResult.Text = text.Resolve(MessageKeys.AttemptStopConfirmationRequired);
                        return;
                    }

                    string message = await sprintWorkspace.Actions
                        // The dialog's own answer, never a literal `true`.
                        .StopAsync(root, fresh, confirmed, CancellationToken.None)
                        .ConfigureAwait(true);
                    await RefreshAllAsync().ConfigureAwait(true);
                    stopResult.Text = message;
                });
                ContextualActionHost.Children.Add(stopButton);
            }

            ContextualActionHost.Children.Add(stopResult);

            IReadOnlyList<AvailableAction> moveActions =
                [.. actions.Where(action => action.ActionId.StartsWith(AvailableActionProjector.MoveToStageActionPrefix, StringComparison.Ordinal))];
            if (moveActions.Count > 0)
            {
                ContextualActionHost.Children.Add(rewindReasonEntry);
                foreach (AvailableAction moveAction in moveActions)
                {
                    string targetStageId = moveAction.Target.StageId!;
                    Button moveButton = new()
                    {
                        Text = string.Create(
                            CultureInfo.InvariantCulture, $"{text.Resolve(MessageKeys.MoveToStageAction)}: {targetStageId}"),
                        IsEnabled = moveAction.Enabled,
                    };
                    moveButton.Clicked += (_, _) => _ = RunAsync(async () =>
                        await MoveToStageAsync(targetStageId).ConfigureAwait(true));
                    ContextualActionHost.Children.Add(moveButton);
                    if (!moveAction.Enabled && moveAction.Blockers.Count > 0)
                    {
                        ContextualActionHost.Children.Add(Describe(new Label
                        {
                            Text = string.Create(
                                CultureInfo.InvariantCulture,
                                $"{text.Resolve(MessageKeys.ActionsBlockedPrefix)} {string.Join(", ", moveAction.Blockers)}"),
                        }));
                    }
                }
            }

            ContextualActionHost.Children.Add(moveResult);

            bool hasGate = SprintWorkspaceViewModel.HasPendingGate(currentDetails);
            if (hasGate)
            {
                Button approve = new() { Text = text.Resolve(MessageKeys.GateApproveAction) };
                Button reject = new() { Text = text.Resolve(MessageKeys.GateRejectAction) };
                approve.Clicked += (_, _) => _ = RunAsync(() => ResolveGateAsync(true));
                reject.Clicked += (_, _) => _ = RunAsync(() => ResolveGateAsync(false));
                ContextualActionHost.Children.Add(new HorizontalStackLayout { Children = { approve, reject } });
            }

            ContextualActionHost.Children.Add(gateResult);

            Guid? activeAttemptId = SprintWorkspaceViewModel.FindActiveAttemptId(currentDetails);
            if (activeAttemptId is { } attemptId)
            {
                Entry instruction = Describe(new Entry(), text.Resolve(MessageKeys.AttemptInstructionLabel));
                Button supersede = new() { Text = text.Resolve(MessageKeys.AttemptSupersedeAction) };
                supersede.Clicked += (_, _) => _ = RunAsync(async () =>
                {
                    if (string.IsNullOrWhiteSpace(instruction.Text))
                    {
                        supersedeResult.Text = text.Resolve(MessageKeys.AttemptInstructionRequired);
                        return;
                    }

                    string action = text.Resolve(MessageKeys.AttemptSupersedeAction);
                    bool confirmed = await DisplayAlertAsync(
                            action, sprintWorkspace.AttemptSupersedePrompt(sprintId, attemptId.ToString("D")), action,
                            text.Resolve(MessageKeys.CancelAction))
                        .ConfigureAwait(true);
                    if (!confirmed)
                    {
                        supersedeResult.Text = text.Resolve(MessageKeys.AttemptSupersedeConfirmationRequired);
                        return;
                    }

                    string message = await sprintWorkspace
                        .SupersedeAttemptAsync(
                            root, sprintId, attemptId.ToString("D"), instruction.Text, confirmed, CancellationToken.None)
                        .ConfigureAwait(true);
                    await RefreshAllAsync().ConfigureAwait(true);
                    supersedeResult.Text = message;
                });
                ContextualActionHost.Children.Add(instruction);
                ContextualActionHost.Children.Add(supersede);
            }

            ContextualActionHost.Children.Add(supersedeResult);

            if (NodeIsReady(ImplementationCriticalGraphBuilder.ConfirmationNodeId))
            {
                Entry definitionOfDone = Describe(new Entry(), text.Resolve(MessageKeys.ConfirmDefinitionOfDoneLabel));
                Entry evidence = Describe(new Entry(), text.Resolve(MessageKeys.ConfirmEvidenceLabel));
                Picker evidenceKind = new() { ItemsSource = new List<string> { "inspection", "execution", "existing-check" } };
                evidenceKind.SelectedIndex = 0;
                SemanticProperties.SetDescription(evidenceKind, text.Resolve(MessageKeys.ConfirmEvidenceKindLabel));
                Button confirmed = new() { Text = text.Resolve(MessageKeys.ConfirmConfirmedAction) };
                Button notConfirmed = new() { Text = text.Resolve(MessageKeys.ConfirmNotConfirmedAction) };
                async Task ConfirmAsync(ConfirmationOutcome outcome)
                {
                    if (string.IsNullOrWhiteSpace(definitionOfDone.Text) || string.IsNullOrWhiteSpace(evidence.Text))
                    {
                        confirmResult.Text = text.Resolve(MessageKeys.ConfirmDefinitionOfDoneRequired);
                        return;
                    }

                    string action = text.Resolve(outcome == ConfirmationOutcome.Confirmed
                        ? MessageKeys.ConfirmConfirmedAction
                        : MessageKeys.ConfirmNotConfirmedAction);
                    bool dialogConfirmed = await DisplayAlertAsync(
                            action, sprintWorkspace.ConfirmPrompt(sprintId, null, definitionOfDone.Text, evidence.Text),
                            action, text.Resolve(MessageKeys.CancelAction))
                        .ConfigureAwait(true);
                    if (!dialogConfirmed)
                    {
                        confirmResult.Text = text.Resolve(MessageKeys.ConfirmConfirmationRequired);
                        return;
                    }

                    string message = await sprintWorkspace
                        .ConfirmNodeAsync(
                            root, sprintId, null, outcome, definitionOfDone.Text, evidenceKind.SelectedItem as string,
                            evidence.Text, dialogConfirmed, CancellationToken.None)
                        .ConfigureAwait(true);
                    await RefreshAllAsync().ConfigureAwait(true);
                    confirmResult.Text = message;
                }

                confirmed.Clicked += (_, _) => _ = RunAsync(() => ConfirmAsync(ConfirmationOutcome.Confirmed));
                notConfirmed.Clicked += (_, _) => _ = RunAsync(() => ConfirmAsync(ConfirmationOutcome.NotConfirmed));
                ContextualActionHost.Children.Add(definitionOfDone);
                ContextualActionHost.Children.Add(evidence);
                ContextualActionHost.Children.Add(evidenceKind);
                ContextualActionHost.Children.Add(new HorizontalStackLayout { Children = { confirmed, notConfirmed } });
            }

            ContextualActionHost.Children.Add(confirmResult);

            if (NodeIsReady(ImplementationCriticalGraphBuilder.TestWorkNodeId))
            {
                Entry justification = Describe(new Entry(), text.Resolve(MessageKeys.TestWorkJustificationLabel));
                Button added = new() { Text = text.Resolve(MessageKeys.TestWorkAddedAction) };
                Button noNewTests = new() { Text = text.Resolve(MessageKeys.TestWorkNoNewTestsAction) };
                async Task TestWorkAsync(TestWorkOutcome outcome)
                {
                    if (string.IsNullOrWhiteSpace(justification.Text))
                    {
                        testWorkResult.Text = text.Resolve(MessageKeys.TestWorkJustificationRequired);
                        return;
                    }

                    string action = text.Resolve(outcome == TestWorkOutcome.TestsAdded
                        ? MessageKeys.TestWorkAddedAction
                        : MessageKeys.TestWorkNoNewTestsAction);
                    bool dialogConfirmed = await DisplayAlertAsync(
                            action, sprintWorkspace.TestWorkPrompt(sprintId, null, justification.Text), action,
                            text.Resolve(MessageKeys.CancelAction))
                        .ConfigureAwait(true);
                    if (!dialogConfirmed)
                    {
                        testWorkResult.Text = text.Resolve(MessageKeys.TestWorkConfirmationRequired);
                        return;
                    }

                    string message = await sprintWorkspace
                        .RecordTestWorkAsync(root, sprintId, null, outcome, justification.Text, dialogConfirmed, CancellationToken.None)
                        .ConfigureAwait(true);
                    await RefreshAllAsync().ConfigureAwait(true);
                    testWorkResult.Text = message;
                }

                added.Clicked += (_, _) => _ = RunAsync(() => TestWorkAsync(TestWorkOutcome.TestsAdded));
                noNewTests.Clicked += (_, _) => _ = RunAsync(() => TestWorkAsync(TestWorkOutcome.NoNewTestsJustified));
                ContextualActionHost.Children.Add(justification);
                ContextualActionHost.Children.Add(new HorizontalStackLayout { Children = { added, noNewTests } });
            }

            ContextualActionHost.Children.Add(testWorkResult);

            if (NodeIsReady(ImplementationCriticalGraphBuilder.FinalizationNodeId))
            {
                Button finalize = new() { Text = text.Resolve(MessageKeys.FinalizeAction) };
                finalize.Clicked += (_, _) => _ = RunAsync(async () =>
                {
                    string action = text.Resolve(MessageKeys.FinalizeAction);
                    bool dialogConfirmed = await DisplayAlertAsync(
                            action, sprintWorkspace.FinalizePrompt(sprintId, null), action, text.Resolve(MessageKeys.CancelAction))
                        .ConfigureAwait(true);
                    if (!dialogConfirmed)
                    {
                        finalizeResult.Text = text.Resolve(MessageKeys.FinalizeConfirmationRequired);
                        return;
                    }

                    string message = await sprintWorkspace
                        .FinalizeSprintAsync(root, sprintId, null, dialogConfirmed, CancellationToken.None)
                        .ConfigureAwait(true);
                    await RefreshAllAsync().ConfigureAwait(true);
                    finalizeResult.Text = message;
                });
                ContextualActionHost.Children.Add(finalize);
            }

            ContextualActionHost.Children.Add(finalizeResult);
        }

        void AddLifecycleAction(
            IReadOnlyList<AvailableAction> actions, string actionId, string label, Func<Task> execute)
        {
            AvailableAction? action = SprintActionsViewModel.Find(actions, actionId);
            if (action is null)
            {
                return;
            }

            Button button = new() { Text = label, IsEnabled = action.Enabled };
            button.Clicked += (_, _) => _ = RunAsync(execute);
            ContextualActionHost.Children.Add(button);
        }

        bool NodeIsReady(string nodeId) =>
            currentDetails?.Nodes.Any(node => node.Id == nodeId && node.State == "ready") ?? false;

        async Task ResolveGateAsync(bool approved)
        {
            string action = text.Resolve(approved ? MessageKeys.GateApproveAction : MessageKeys.GateRejectAction);
            bool confirmed = await DisplayAlertAsync(
                    action, sprintWorkspace.GatePrompt(sprintId, null), action, text.Resolve(MessageKeys.CancelAction))
                .ConfigureAwait(true);
            if (!confirmed)
            {
                gateResult.Text = text.Resolve(MessageKeys.GateConfirmationRequired);
                return;
            }

            string message = await sprintWorkspace
                .ResolveGateAsync(root, sprintId, null, approved, confirmed, CancellationToken.None)
                .ConfigureAwait(true);
            await RefreshAllAsync().ConfigureAwait(true);
            gateResult.Text = message;
        }

        async Task MoveToStageAsync(string targetStageId)
        {
            StageTransitionAssessment assessment = await sprintWorkspace.Actions
                .AssessMoveAsync(root, sprintId, targetStageId, CancellationToken.None)
                .ConfigureAwait(true);
            if (!assessment.Found)
            {
                moveResult.Text = assessment.DiagnosticCode;
                await RefreshAllAsync().ConfigureAwait(true);
                return;
            }

            if (!assessment.Allowed)
            {
                moveResult.Text = sprintWorkspace.Actions.MovePrompt(assessment);
                return;
            }

            bool isRewind = assessment.Direction == StageTransitionDirection.Rewind;
            if (isRewind && string.IsNullOrWhiteSpace(rewindReasonEntry.Text))
            {
                moveResult.Text = text.Resolve(MessageKeys.ActionRewindReasonRequired);
                return;
            }

            string action = text.Resolve(MessageKeys.MoveToStageAction);
            bool confirmed = await DisplayAlertAsync(
                    action, sprintWorkspace.Actions.MovePrompt(assessment), action, text.Resolve(MessageKeys.CancelAction))
                .ConfigureAwait(true);
            if (!confirmed)
            {
                moveResult.Text = text.Resolve(MessageKeys.MoveToStageConfirmationRequired);
                return;
            }

            string message = await sprintWorkspace.Actions
                // The dialog's own answer, never a literal `true`.
                .MoveAsync(
                    root, sprintId, assessment, isRewind ? rewindReasonEntry.Text : null, confirmed, CancellationToken.None)
                .ConfigureAwait(true);
            await RefreshAllAsync().ConfigureAwait(true);
            moveResult.Text = message;
        }

        async Task RefreshAllAsync()
        {
            await RefreshHeaderAsync().ConfigureAwait(true);
            await RefreshActionsAsync().ConfigureAwait(true);
        }

        await RefreshHeaderAsync().ConfigureAwait(true);
        await InitializeTimelineAsync().ConfigureAwait(true);
        await RefreshActionsAsync().ConfigureAwait(true);

        string? SelectedFilterOrNull()
        {
            string? selected = filterPicker.SelectedItem as string;
            return selected == text.Resolve(MessageKeys.TimelineFilterAllOption) ? null : selected;
        }

        filterPicker.SelectedIndexChanged += (_, _) =>
        {
            if (!timelineInitialized)
            {
                return;
            }

            RenderTimelineItems(sprintWorkspace.Timeline.SetFilter(SelectedFilterOrNull()));
        };
        loadMoreButton.Clicked += (_, _) => _ = RunAsync(async () =>
            RenderTimelineItems(await sprintWorkspace.Timeline.LoadMoreAsync(root, CancellationToken.None).ConfigureAwait(true)));
        Button markAllReadButton = new() { Text = text.Resolve(MessageKeys.TimelineMarkAllReadAction) };
        markAllReadButton.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            await sprintWorkspace.Timeline.MarkAllReadAsync(CancellationToken.None).ConfigureAwait(true);
            RenderTimelineItems(sprintWorkspace.Timeline.SetFilter(SelectedFilterOrNull()));
        });
        rewindReasonEntry.Unfocused += (_, _) => _ =
            sprintWorkspace.Timeline.SaveDraftAsync(rewindReasonEntry.Text, CancellationToken.None);

        ContentHost.Children.Add(Describe(new Label { Text = text.Resolve(MessageKeys.TimelineTitle), FontAttributes = FontAttributes.Bold }));
        ContentHost.Children.Add(new HorizontalStackLayout { Children = { filterPicker, markAllReadButton } });
        ContentHost.Children.Add(timelineStatusLabel);
        ContentHost.Children.Add(timelineItemsHost);
        ContentHost.Children.Add(loadMoreButton);
        ContentHost.Children.Add(copyNoticeLabel);

        // ADR 0005's project-wide `control.events` capability is distinct from the sprint-scoped
        // Timeline above (it spans every sprint in the project, not just this one) -- kept reachable
        // here rather than dropped, matching plan 12.1's "every current Desktop capability remains
        // reachable."
        Label rawEventsResult = new();
        Button pollRawEvents = new() { Text = text.Resolve(MessageKeys.EventsPollAction) };
        pollRawEvents.Clicked += (_, _) => _ = RunAsync(async () =>
            rawEventsResult.Text = await sprintWorkspace.PollEventsAsync(root, CancellationToken.None).ConfigureAwait(true));
        ContentHost.Children.Add(pollRawEvents);
        ContentHost.Children.Add(rawEventsResult);

        scrollTrackedSprintId = sprintId;
        if (ContentHost.Parent is ScrollView scrollView)
        {
            // The ScrollView itself is a fixed XAML element that survives every render (only its
            // child's Children are rebuilt), so this handler is attached exactly once per page
            // instance -- not once per navigation -- and always reads the *current*
            // scrollTrackedSprintId rather than closing over a stale one.
            if (!scrollHandlerAttached)
            {
                scrollHandlerAttached = true;
                scrollView.Scrolled += (_, args) => sprintScrollPositions[scrollTrackedSprintId] = args.ScrollY;
            }

            if (sprintScrollPositions.TryGetValue(sprintId, out double previousScrollY) && previousScrollY > 0)
            {
                _ = scrollView.ScrollToAsync(0, previousScrollY, false);
            }
            else
            {
                _ = scrollView.ScrollToAsync(0, 0, false);
            }
        }

        timelinePollTimer = Dispatcher.CreateTimer();
        timelinePollTimer.Interval = TimelinePollInterval;
        timelinePollTimer.Tick += (_, _) => _ = RunAsync(async () =>
            RenderTimelineItems(await sprintWorkspace.Timeline.LoadMoreAsync(root, CancellationToken.None).ConfigureAwait(true)));
        timelinePollTimer.Start();
    }
}
