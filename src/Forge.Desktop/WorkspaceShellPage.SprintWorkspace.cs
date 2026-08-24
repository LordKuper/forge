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
        SemanticProperties.SetDescription(filterPicker, text.Resolve(MessageKeys.TimelineFilterLabel));
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
        // ADR 0054, post-release timeline gap closure: the message composer, hoisted here like
        // rewindReasonEntry already is for the same reason (a re-render triggered by a failed
        // mutation must reuse the same Entry instance rather than replace it with a blank one).
        Entry messageEntry = Describe(new Entry(), text.Resolve(MessageKeys.TimelineMessageLabel));
        Label messageResult = new();
        // PR #99 review finding 7: hoisted out of RefreshActionsAsync (like rewindReasonEntry already
        // was) so a re-render triggered by a FAILED mutation -- every mutation handler calls
        // RefreshAllAsync unconditionally on completion -- reuses the same Entry instance instead of
        // replacing it with a blank one. Only a genuinely successful, completed action clears the
        // corresponding field's text (matching this codebase's one-shot-state convention).
        Entry instructionEntry = Describe(new Entry(), text.Resolve(MessageKeys.AttemptInstructionLabel));
        Entry definitionOfDoneEntry = Describe(new Entry(), text.Resolve(MessageKeys.ConfirmDefinitionOfDoneLabel));
        Entry evidenceEntry = Describe(new Entry(), text.Resolve(MessageKeys.ConfirmEvidenceLabel));
        Entry justificationEntry = Describe(new Entry(), text.Resolve(MessageKeys.TestWorkJustificationLabel));
        SprintDetails? currentDetails = null;
        // PR #99 review finding 3: RenderTimelineItems below re-assigns filterPicker.ItemsSource/
        // SelectedItem, either of which can raise the picker's own SelectedIndexChanged event
        // synchronously (a fresh ItemsSource resets SelectedIndex first) -- re-entering
        // RenderTimelineItems from inside itself on every poll tick / load-more / mark-all-read.
        // This flag is the real suppression: set around exactly those two assignments, not a stale
        // one-shot "not initialized yet" check that could never be false once the handler existed.
        bool suppressFilterChanged = false;

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
            suppressFilterChanged = true;
            try
            {
                filterPicker.ItemsSource = options;
                filterPicker.SelectedItem = state.ActiveFilterType ?? options[0];
            }
            finally
            {
                suppressFilterChanged = false;
            }
        }

        async Task InitializeTimelineAsync()
        {
            TimelineState state = await sprintWorkspace.Timeline
                .InitializeAsync(workspace.Route.ProjectId!.Value, root, sprintId, CancellationToken.None)
                .ConfigureAwait(true);
            rewindReasonEntry.Text = await sprintWorkspace.Timeline.LoadDraftAsync(CancellationToken.None).ConfigureAwait(true);
            messageEntry.Text =
                await sprintWorkspace.Timeline.LoadMessageDraftAsync(CancellationToken.None).ConfigureAwait(true);
            RenderTimelineItems(state);
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
                    if (!dialogConfirmed)
                    {
                        // PR #99 review finding 5: every other gated action in this file aborts on a
                        // declined confirmation before touching the Host -- this was the only one
                        // that fell through to the mutation call regardless of the dialog's answer.
                        lifecycleResult.Text = text.Resolve(MessageKeys.SprintCancelConfirmationRequired);
                        return;
                    }

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
                ContextualActionHost.Children.Add(BuildRationale(stop));
            }

            ContextualActionHost.Children.Add(stopResult);

            IReadOnlyList<AvailableAction> moveActions =
                [.. actions.Where(action => action.ActionId.StartsWith(AvailableActionProjector.MoveToStageActionPrefix, StringComparison.Ordinal))];
            if (moveActions.Count > 0)
            {
                // PR #99 review finding 10: the Host already declares this field's own bound
                // (AvailableActionProjector.BuildMoveToStage) -- apply it client-side so the input
                // itself prevents composing a reason the commit would reject anyway, rather than only
                // reporting the rejection after the fact.
                AvailableActionInputField? reasonField = moveActions
                    .SelectMany(action => action.InputFields)
                    .FirstOrDefault(field => field.Name == AvailableActionProjector.RewindReasonField);
                if (reasonField?.MaxLength is { } maxReasonLength)
                {
                    rewindReasonEntry.MaxLength = maxReasonLength;
                }

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
                    ContextualActionHost.Children.Add(BuildRationale(moveAction));
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
                Button supersede = new() { Text = text.Resolve(MessageKeys.AttemptSupersedeAction) };
                supersede.Clicked += (_, _) => _ = RunAsync(async () =>
                {
                    if (string.IsNullOrWhiteSpace(instructionEntry.Text))
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
                            root, sprintId, attemptId.ToString("D"), instructionEntry.Text, confirmed, CancellationToken.None)
                        .ConfigureAwait(true);
                    // A genuinely successful, completed action -- clear the typed input only now,
                    // never on a failed attempt (PR #99 review finding 7). Every result here is a
                    // human-readable message, not a typed outcome, but a Host success always resolves
                    // to exactly this fixed text with no diagnostic-code suffix (MainPageViewModel's
                    // own Message helper), so an exact match is a reliable success signal.
                    if (string.Equals(message, text.Resolve(MessageKeys.AttemptSuperseded), StringComparison.Ordinal))
                    {
                        instructionEntry.Text = null;
                    }

                    await RefreshAllAsync().ConfigureAwait(true);
                    supersedeResult.Text = message;
                });
                ContextualActionHost.Children.Add(instructionEntry);
                ContextualActionHost.Children.Add(supersede);
            }

            ContextualActionHost.Children.Add(supersedeResult);

            if (NodeIsReady(ImplementationCriticalGraphBuilder.ConfirmationNodeId))
            {
                Picker evidenceKind = new() { ItemsSource = new List<string> { "inspection", "execution", "existing-check" } };
                evidenceKind.SelectedIndex = 0;
                SemanticProperties.SetDescription(evidenceKind, text.Resolve(MessageKeys.ConfirmEvidenceKindLabel));
                Button confirmed = new() { Text = text.Resolve(MessageKeys.ConfirmConfirmedAction) };
                Button notConfirmed = new() { Text = text.Resolve(MessageKeys.ConfirmNotConfirmedAction) };
                async Task ConfirmAsync(ConfirmationOutcome outcome)
                {
                    if (string.IsNullOrWhiteSpace(definitionOfDoneEntry.Text) || string.IsNullOrWhiteSpace(evidenceEntry.Text))
                    {
                        confirmResult.Text = text.Resolve(MessageKeys.ConfirmDefinitionOfDoneRequired);
                        return;
                    }

                    string action = text.Resolve(outcome == ConfirmationOutcome.Confirmed
                        ? MessageKeys.ConfirmConfirmedAction
                        : MessageKeys.ConfirmNotConfirmedAction);
                    bool dialogConfirmed = await DisplayAlertAsync(
                            action, sprintWorkspace.ConfirmPrompt(sprintId, null, definitionOfDoneEntry.Text, evidenceEntry.Text),
                            action, text.Resolve(MessageKeys.CancelAction))
                        .ConfigureAwait(true);
                    if (!dialogConfirmed)
                    {
                        confirmResult.Text = text.Resolve(MessageKeys.ConfirmConfirmationRequired);
                        return;
                    }

                    string message = await sprintWorkspace
                        .ConfirmNodeAsync(
                            root, sprintId, null, outcome, definitionOfDoneEntry.Text, evidenceKind.SelectedItem as string,
                            evidenceEntry.Text, dialogConfirmed, CancellationToken.None)
                        .ConfigureAwait(true);
                    // Genuinely successful and completed only -- see the supersede handler's own
                    // remarks (PR #99 review finding 7).
                    if (string.Equals(message, text.Resolve(MessageKeys.ConfirmRecorded), StringComparison.Ordinal))
                    {
                        definitionOfDoneEntry.Text = null;
                        evidenceEntry.Text = null;
                    }

                    await RefreshAllAsync().ConfigureAwait(true);
                    confirmResult.Text = message;
                }

                confirmed.Clicked += (_, _) => _ = RunAsync(() => ConfirmAsync(ConfirmationOutcome.Confirmed));
                notConfirmed.Clicked += (_, _) => _ = RunAsync(() => ConfirmAsync(ConfirmationOutcome.NotConfirmed));
                ContextualActionHost.Children.Add(definitionOfDoneEntry);
                ContextualActionHost.Children.Add(evidenceEntry);
                ContextualActionHost.Children.Add(evidenceKind);
                ContextualActionHost.Children.Add(new HorizontalStackLayout { Children = { confirmed, notConfirmed } });
            }

            ContextualActionHost.Children.Add(confirmResult);

            if (NodeIsReady(ImplementationCriticalGraphBuilder.TestWorkNodeId))
            {
                Button added = new() { Text = text.Resolve(MessageKeys.TestWorkAddedAction) };
                Button noNewTests = new() { Text = text.Resolve(MessageKeys.TestWorkNoNewTestsAction) };
                async Task TestWorkAsync(TestWorkOutcome outcome)
                {
                    if (string.IsNullOrWhiteSpace(justificationEntry.Text))
                    {
                        testWorkResult.Text = text.Resolve(MessageKeys.TestWorkJustificationRequired);
                        return;
                    }

                    string action = text.Resolve(outcome == TestWorkOutcome.TestsAdded
                        ? MessageKeys.TestWorkAddedAction
                        : MessageKeys.TestWorkNoNewTestsAction);
                    bool dialogConfirmed = await DisplayAlertAsync(
                            action, sprintWorkspace.TestWorkPrompt(sprintId, null, justificationEntry.Text), action,
                            text.Resolve(MessageKeys.CancelAction))
                        .ConfigureAwait(true);
                    if (!dialogConfirmed)
                    {
                        testWorkResult.Text = text.Resolve(MessageKeys.TestWorkConfirmationRequired);
                        return;
                    }

                    string message = await sprintWorkspace
                        .RecordTestWorkAsync(root, sprintId, null, outcome, justificationEntry.Text, dialogConfirmed, CancellationToken.None)
                        .ConfigureAwait(true);
                    // Genuinely successful and completed only -- see the supersede handler's own
                    // remarks (PR #99 review finding 7).
                    if (string.Equals(message, text.Resolve(MessageKeys.TestWorkRecorded), StringComparison.Ordinal))
                    {
                        justificationEntry.Text = null;
                    }

                    await RefreshAllAsync().ConfigureAwait(true);
                    testWorkResult.Text = message;
                }

                added.Clicked += (_, _) => _ = RunAsync(() => TestWorkAsync(TestWorkOutcome.TestsAdded));
                noNewTests.Clicked += (_, _) => _ = RunAsync(() => TestWorkAsync(TestWorkOutcome.NoNewTestsJustified));
                ContextualActionHost.Children.Add(justificationEntry);
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
            ContextualActionHost.Children.Add(BuildRationale(action));
        }

        // PR #99 review finding 9: AvailableActionProjector has computed a RationaleKey for every
        // AvailableAction since Slice 4, but no surface ever rendered it -- the Host's own stated
        // reason for offering an action (plan 4.3: "renders typed controls described by the Host")
        // was silently dropped. Every lifecycle/stop/move-to-stage row now shows it alongside its
        // button/verb label.
        Label BuildRationale(AvailableAction action) => Describe(new Label { Text = text.Resolve(action.RationaleKey) });

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
                // PR #99 review finding 9: this is exactly the "stale-assessment-rejected" case
                // ActionStaleRefreshed was authored for -- the fresh re-assessment no longer finds the
                // target the on-screen row was built from, so the view is refreshed and the user is
                // told plainly why, with the machine diagnostic still available parenthetically.
                moveResult.Text = Message(text.Resolve(MessageKeys.ActionStaleRefreshed), assessment.DiagnosticCode);
                await RefreshAllAsync().ConfigureAwait(true);
                return;
            }

            // PR #101 review finding 3 (critical): an unconverged rewind already in progress reports
            // Allowed=false (StageTransitionAssessor.AssessAsync's own rewind-in-progress branch --
            // no other stage move is legal until it resumes and converges), but
            // StageTransitionCoordinator.MoveAsync's own resume path bypasses Allowed entirely for
            // exactly this diagnostic, the same way `forge sprint move-stage` already does
            // (CliApplication ignores Allowed for this call). Without this carve-out a Desktop user
            // could never reach that resume call: every render of this row would just keep
            // re-reporting "blocked, cannot proceed" forever, with no way to unstick a sprint a
            // conflict interrupted mid-rewind. Every other `!Allowed` reason still blocks exactly as
            // before -- only this specific, genuinely-resumable diagnostic is let through.
            bool isRewindInProgress = assessment.DiagnosticCode == DiagnosticCodes.StageTransitionRewindInProgress;
            if (!assessment.Allowed && !isRewindInProgress)
            {
                moveResult.Text = sprintWorkspace.Actions.MovePrompt(assessment);
                return;
            }

            bool isRewind = assessment.Direction == StageTransitionDirection.Rewind;
            // A resume carries no caller-supplied reason of its own (the coordinator's own resume
            // path reuses the reason already recorded when the rewind first committed, ignoring
            // whatever this call passes), so the ordinary reason requirement does not apply to it.
            if (isRewind && !isRewindInProgress && string.IsNullOrWhiteSpace(rewindReasonEntry.Text))
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
                // The dialog's own answer, never a literal `true`. No reason on a resume (see above).
                .MoveAsync(
                    root, sprintId, assessment, isRewind && !isRewindInProgress ? rewindReasonEntry.Text : null,
                    confirmed, CancellationToken.None)
                .ConfigureAwait(true);
            await RefreshAllAsync().ConfigureAwait(true);
            moveResult.Text = message;
        }

        async Task RefreshAllAsync()
        {
            await RefreshHeaderAsync().ConfigureAwait(true);
            // Plan section 10: the selected sprint refreshes immediately after a mutation -- the
            // timeline pane is not exempt just because it also has its own bounded poll (PR #99
            // review finding 2). Reuses the same LoadMoreAsync path the poll and "load more" already
            // call, so the event the user's own action just caused is visible right away instead of
            // waiting up to TimelinePollInterval.
            RenderTimelineItems(await sprintWorkspace.Timeline.LoadMoreAsync(root, CancellationToken.None).ConfigureAwait(true));
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
            if (suppressFilterChanged)
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
        rewindReasonEntry.Unfocused += (_, _) => _ = RunAsync(async () =>
        {
            // PR #99 review finding 10: previously fire-and-forget with the result discarded, which
            // made ProjectCatalogDraftTooLong unreachable by a user -- the draft would silently fail
            // to save past the length limit with no feedback at all.
            ProjectCatalogResult saveResult = await sprintWorkspace.Timeline
                .SaveDraftAsync(rewindReasonEntry.Text, CancellationToken.None)
                .ConfigureAwait(true);
            if (!saveResult.Succeeded)
            {
                moveResult.Text = Message(text.Resolve(MessageKeys.ActionRewindReasonDraftSaveFailed), saveResult.DiagnosticCode);
            }
        });
        messageEntry.Unfocused += (_, _) => _ = RunAsync(async () =>
        {
            ProjectCatalogResult saveResult = await sprintWorkspace.Timeline
                .SaveMessageDraftAsync(messageEntry.Text, CancellationToken.None)
                .ConfigureAwait(true);
            if (!saveResult.Succeeded)
            {
                messageResult.Text = Message(text.Resolve(MessageKeys.TimelineMessageDraftSaveFailed), saveResult.DiagnosticCode);
            }
        });
        Button sendMessageButton = new() { Text = text.Resolve(MessageKeys.TimelineMessageSendAction) };
        sendMessageButton.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            string message = await sprintWorkspace
                .PostMessageAsync(root, sprintId, messageEntry.Text, CancellationToken.None)
                .ConfigureAwait(true);
            // Only a genuinely successful post clears the composer and its saved draft -- matching
            // AttemptSupersedeAsync's own "exact match is a reliable success signal" convention
            // immediately above (a Host success always resolves to exactly this fixed text with no
            // diagnostic-code suffix).
            if (string.Equals(message, text.Resolve(MessageKeys.SprintMessagePosted), StringComparison.Ordinal))
            {
                messageEntry.Text = null;
                await sprintWorkspace.Timeline.SaveMessageDraftAsync(null, CancellationToken.None).ConfigureAwait(true);
            }

            await RefreshAllAsync().ConfigureAwait(true);
            messageResult.Text = message;
        });

        ContentHost.Children.Add(Describe(new Label { Text = text.Resolve(MessageKeys.TimelineTitle), FontAttributes = FontAttributes.Bold }));
        ContentHost.Children.Add(new HorizontalStackLayout { Children = { filterPicker, markAllReadButton } });
        ContentHost.Children.Add(timelineStatusLabel);
        ContentHost.Children.Add(timelineItemsHost);
        ContentHost.Children.Add(loadMoreButton);
        ContentHost.Children.Add(copyNoticeLabel);
        ContentHost.Children.Add(new HorizontalStackLayout { Children = { messageEntry, sendMessageButton } });
        ContentHost.Children.Add(messageResult);

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
                // PR #99 review finding 11: scrollTrackedSprintId is reset to Guid.Empty by
                // RenderContentAsync whenever a non-sprint-workspace route renders (see
                // WorkspaceShellPage.xaml.cs), so scrolling the project overview/settings/Forge
                // settings pages -- which share this same ScrollView -- can never overwrite a
                // sprint's saved position; only a scroll genuinely occurring while a sprint workspace
                // is the active route is ever recorded.
                scrollView.Scrolled += (_, args) =>
                {
                    if (scrollTrackedSprintId != Guid.Empty)
                    {
                        sprintScrollPositions[scrollTrackedSprintId] = args.ScrollY;
                    }
                };
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

        bool timelinePollInFlight = false;

        async Task PollTimelineAsync()
        {
            try
            {
                // Deliberately NOT routed through RunAsync/renderGate (PR #99 review finding 1): this
                // is an unattended background tick, never a user gesture, so it must not contend for
                // the same guard a user click's mutation takes -- doing so previously let a click
                // landing during this Host round-trip be silently dropped before it even started.
                // Only the fast, synchronous render step below goes through the gate, so it still
                // never races a concurrent mutation's own render.
                TimelineState state = await sprintWorkspace.Timeline
                    .LoadMoreAsync(root, CancellationToken.None)
                    .ConfigureAwait(true);
                renderGate.RequestRender(() => RenderTimelineItems(state));
            }
            finally
            {
                timelinePollInFlight = false;
            }
        }

        timelinePollTimer = Dispatcher.CreateTimer();
        timelinePollTimer.Interval = TimelinePollInterval;
        timelinePollTimer.Tick += (_, _) =>
        {
            if (timelinePollInFlight)
            {
                // The previous tick's Host round-trip has not returned yet -- skip this tick rather
                // than stacking overlapping fetches; the next tick will pick up wherever the cursor
                // is once the in-flight one completes.
                return;
            }

            timelinePollInFlight = true;
            _ = PollTimelineAsync();
        };
        timelinePollTimer.Start();
    }
}
