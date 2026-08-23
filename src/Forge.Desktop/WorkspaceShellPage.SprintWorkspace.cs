using Forge.Desktop.Presentation;
using Forge.Domain;
using Forge.Localization;

namespace Forge.Desktop;

/// <summary>
/// Plan section 4.3's sprint-workspace route. This slice keeps it deliberately unstyled -- the
/// sticky status header, virtualized timeline, and typed contextual-action renderer are Slice 6's
/// own deliverable -- but every gate/confirm/test-work/finalize/supersede/poll/lifecycle capability
/// <see cref="MainPageViewModel"/> already exposed remains reachable here through
/// <see cref="SprintWorkspaceViewModel"/>, scoped to the project/sprint the sidebar/overview already
/// selected (so unlike the previous monolithic page, this page never asks for the project root or
/// sprint id again). Every confirmation dialog here reproduces the previous monolithic page's own
/// safety rule exactly: ADR 0005/0018/0037's human-only capabilities (gate, supersede, confirm,
/// test-work, finalize) show a dialog naming the exact sprint/node/attempt it acts on and never call
/// the mutation at all when the user declines -- <c>confirmed</c> is always the dialog's own answer,
/// never a hardcoded bypass.
/// </summary>
public partial class WorkspaceShellPage
{
    private async Task RenderSprintWorkspaceAsync(string root, Guid sprintId)
    {
        Label snapshotLabel = new();
        async Task RefreshSnapshotAsync()
        {
            MainPageSnapshot snapshot = await sprintWorkspace.RefreshAsync(root, sprintId, CancellationToken.None)
                .ConfigureAwait(true);
            snapshotLabel.Text = string.Join(
                Environment.NewLine,
                snapshot.StatusText, snapshot.ProjectStateText, snapshot.SprintsText, snapshot.SprintDetailsText,
                snapshot.SuggestedActionsText, snapshot.DiagnosticsText);
        }

        await RefreshSnapshotAsync().ConfigureAwait(true);
        ContentHost.Children.Add(snapshotLabel);

        Entry nodeId = Describe(new Entry(), text.Resolve(MessageKeys.GateNodeIdLabel));
        Button approve = new() { Text = text.Resolve(MessageKeys.GateApproveAction) };
        Button reject = new() { Text = text.Resolve(MessageKeys.GateRejectAction) };
        Label gateResult = new();
        async Task ResolveGateAsync(bool approved)
        {
            string action = text.Resolve(approved ? MessageKeys.GateApproveAction : MessageKeys.GateRejectAction);
            bool confirmed = await DisplayAlertAsync(
                    action, sprintWorkspace.GatePrompt(sprintId, nodeId.Text), action, text.Resolve(MessageKeys.CancelAction))
                .ConfigureAwait(true);
            if (!confirmed)
            {
                gateResult.Text = text.Resolve(MessageKeys.GateConfirmationRequired);
                return;
            }

            gateResult.Text = await sprintWorkspace
                .ResolveGateAsync(root, sprintId, nodeId.Text, approved, true, CancellationToken.None)
                .ConfigureAwait(true);
            await RefreshSnapshotAsync().ConfigureAwait(true);
        }

        approve.Clicked += (_, _) => _ = RunAsync(() => ResolveGateAsync(true));
        reject.Clicked += (_, _) => _ = RunAsync(() => ResolveGateAsync(false));
        ContentHost.Children.Add(nodeId);
        ContentHost.Children.Add(new HorizontalStackLayout { Children = { approve, reject } });
        ContentHost.Children.Add(gateResult);

        Entry attemptId = Describe(new Entry(), text.Resolve(MessageKeys.AttemptIdLabel));
        Entry instruction = Describe(new Entry(), text.Resolve(MessageKeys.AttemptInstructionLabel));
        Button supersede = new() { Text = text.Resolve(MessageKeys.AttemptSupersedeAction) };
        Label supersedeResult = new();
        supersede.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(attemptId.Text))
            {
                supersedeResult.Text = text.Resolve(MessageKeys.AttemptIdRequired);
                return;
            }

            if (string.IsNullOrWhiteSpace(instruction.Text))
            {
                supersedeResult.Text = text.Resolve(MessageKeys.AttemptInstructionRequired);
                return;
            }

            string action = text.Resolve(MessageKeys.AttemptSupersedeAction);
            bool confirmed = await DisplayAlertAsync(
                    action, sprintWorkspace.AttemptSupersedePrompt(sprintId, attemptId.Text), action,
                    text.Resolve(MessageKeys.CancelAction))
                .ConfigureAwait(true);
            if (!confirmed)
            {
                supersedeResult.Text = text.Resolve(MessageKeys.AttemptSupersedeConfirmationRequired);
                return;
            }

            supersedeResult.Text = await sprintWorkspace
                .SupersedeAttemptAsync(root, sprintId, attemptId.Text, instruction.Text, true, CancellationToken.None)
                .ConfigureAwait(true);
            await RefreshSnapshotAsync().ConfigureAwait(true);
        });
        ContentHost.Children.Add(attemptId);
        ContentHost.Children.Add(instruction);
        ContentHost.Children.Add(supersede);
        ContentHost.Children.Add(supersedeResult);

        Entry confirmNodeId = Describe(new Entry(), text.Resolve(MessageKeys.ConfirmNodeIdLabel));
        Entry definitionOfDone = Describe(new Entry(), text.Resolve(MessageKeys.ConfirmDefinitionOfDoneLabel));
        Entry evidence = Describe(new Entry(), text.Resolve(MessageKeys.ConfirmEvidenceLabel));
        Picker evidenceKind = new() { ItemsSource = new List<string> { "inspection", "execution", "existing-check" } };
        evidenceKind.SelectedIndex = 0;
        SemanticProperties.SetDescription(evidenceKind, text.Resolve(MessageKeys.ConfirmEvidenceKindLabel));
        Button confirmed = new() { Text = text.Resolve(MessageKeys.ConfirmConfirmedAction) };
        Button notConfirmed = new() { Text = text.Resolve(MessageKeys.ConfirmNotConfirmedAction) };
        Label confirmResult = new();
        async Task ConfirmAsync(ConfirmationOutcome outcome)
        {
            if (string.IsNullOrWhiteSpace(definitionOfDone.Text))
            {
                confirmResult.Text = text.Resolve(MessageKeys.ConfirmDefinitionOfDoneRequired);
                return;
            }

            if (string.IsNullOrWhiteSpace(evidence.Text))
            {
                confirmResult.Text = text.Resolve(MessageKeys.ConfirmEvidenceRequired);
                return;
            }

            string action = text.Resolve(outcome == ConfirmationOutcome.Confirmed
                ? MessageKeys.ConfirmConfirmedAction
                : MessageKeys.ConfirmNotConfirmedAction);
            bool dialogConfirmed = await DisplayAlertAsync(
                    action,
                    sprintWorkspace.ConfirmPrompt(sprintId, confirmNodeId.Text, definitionOfDone.Text, evidence.Text),
                    action, text.Resolve(MessageKeys.CancelAction))
                .ConfigureAwait(true);
            if (!dialogConfirmed)
            {
                confirmResult.Text = text.Resolve(MessageKeys.ConfirmConfirmationRequired);
                return;
            }

            confirmResult.Text = await sprintWorkspace
                .ConfirmNodeAsync(
                    root, sprintId, confirmNodeId.Text, outcome, definitionOfDone.Text,
                    evidenceKind.SelectedItem as string, evidence.Text, true, CancellationToken.None)
                .ConfigureAwait(true);
            await RefreshSnapshotAsync().ConfigureAwait(true);
        }

        confirmed.Clicked += (_, _) => _ = RunAsync(() => ConfirmAsync(ConfirmationOutcome.Confirmed));
        notConfirmed.Clicked += (_, _) => _ = RunAsync(() => ConfirmAsync(ConfirmationOutcome.NotConfirmed));
        ContentHost.Children.Add(confirmNodeId);
        ContentHost.Children.Add(definitionOfDone);
        ContentHost.Children.Add(evidence);
        ContentHost.Children.Add(evidenceKind);
        ContentHost.Children.Add(new HorizontalStackLayout { Children = { confirmed, notConfirmed } });
        ContentHost.Children.Add(confirmResult);

        Entry testWorkNodeId = Describe(new Entry(), text.Resolve(MessageKeys.TestWorkNodeIdLabel));
        Entry justification = Describe(new Entry(), text.Resolve(MessageKeys.TestWorkJustificationLabel));
        Button added = new() { Text = text.Resolve(MessageKeys.TestWorkAddedAction) };
        Button noNewTests = new() { Text = text.Resolve(MessageKeys.TestWorkNoNewTestsAction) };
        Label testWorkResult = new();
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
                    action, sprintWorkspace.TestWorkPrompt(sprintId, testWorkNodeId.Text, justification.Text), action,
                    text.Resolve(MessageKeys.CancelAction))
                .ConfigureAwait(true);
            if (!dialogConfirmed)
            {
                testWorkResult.Text = text.Resolve(MessageKeys.TestWorkConfirmationRequired);
                return;
            }

            testWorkResult.Text = await sprintWorkspace
                .RecordTestWorkAsync(root, sprintId, testWorkNodeId.Text, outcome, justification.Text, true, CancellationToken.None)
                .ConfigureAwait(true);
            await RefreshSnapshotAsync().ConfigureAwait(true);
        }

        added.Clicked += (_, _) => _ = RunAsync(() => TestWorkAsync(TestWorkOutcome.TestsAdded));
        noNewTests.Clicked += (_, _) => _ = RunAsync(() => TestWorkAsync(TestWorkOutcome.NoNewTestsJustified));
        ContentHost.Children.Add(testWorkNodeId);
        ContentHost.Children.Add(justification);
        ContentHost.Children.Add(new HorizontalStackLayout { Children = { added, noNewTests } });
        ContentHost.Children.Add(testWorkResult);

        Entry finalizeNodeId = Describe(new Entry(), text.Resolve(MessageKeys.FinalizeNodeIdLabel));
        Button finalize = new() { Text = text.Resolve(MessageKeys.FinalizeAction) };
        Label finalizeResult = new();
        finalize.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            string action = text.Resolve(MessageKeys.FinalizeAction);
            bool dialogConfirmed = await DisplayAlertAsync(
                    action, sprintWorkspace.FinalizePrompt(sprintId, finalizeNodeId.Text), action,
                    text.Resolve(MessageKeys.CancelAction))
                .ConfigureAwait(true);
            if (!dialogConfirmed)
            {
                finalizeResult.Text = text.Resolve(MessageKeys.FinalizeConfirmationRequired);
                return;
            }

            finalizeResult.Text = await sprintWorkspace
                .FinalizeSprintAsync(root, sprintId, finalizeNodeId.Text, true, CancellationToken.None)
                .ConfigureAwait(true);
            await RefreshSnapshotAsync().ConfigureAwait(true);
        });
        ContentHost.Children.Add(finalizeNodeId);
        ContentHost.Children.Add(finalize);
        ContentHost.Children.Add(finalizeResult);

        Button poll = new() { Text = text.Resolve(MessageKeys.EventsPollAction) };
        Label events = new();
        poll.Clicked += (_, _) => _ = RunAsync(async () =>
            events.Text = await sprintWorkspace.PollEventsAsync(root, CancellationToken.None).ConfigureAwait(true));
        ContentHost.Children.Add(poll);
        ContentHost.Children.Add(events);

        Button run = new() { Text = text.Resolve(MessageKeys.SprintRunAction) };
        Button resume = new() { Text = text.Resolve(MessageKeys.SprintResumeAction) };
        Button cancel = new() { Text = text.Resolve(MessageKeys.SprintCancelAction) };
        Label lifecycleResult = new();
        run.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            lifecycleResult.Text = await sprintWorkspace.RunSprintAsync(root, sprintId, CancellationToken.None)
                .ConfigureAwait(true);
            await RefreshSnapshotAsync().ConfigureAwait(true);
        });
        resume.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            lifecycleResult.Text = await sprintWorkspace.ResumeSprintAsync(root, sprintId, CancellationToken.None)
                .ConfigureAwait(true);
            await RefreshSnapshotAsync().ConfigureAwait(true);
        });
        cancel.Clicked += (_, _) => _ = RunAsync(async () =>
        {
            string action = text.Resolve(MessageKeys.SprintCancelAction);
            bool dialogConfirmed = await DisplayAlertAsync(
                    action, sprintWorkspace.SprintCancelPrompt(sprintId), action, text.Resolve(MessageKeys.CancelAction))
                .ConfigureAwait(true);
            lifecycleResult.Text = await sprintWorkspace
                .CancelSprintAsync(root, sprintId, dialogConfirmed, CancellationToken.None)
                .ConfigureAwait(true);
            await RefreshSnapshotAsync().ConfigureAwait(true);
        });
        ContentHost.Children.Add(new HorizontalStackLayout { Children = { run, resume, cancel } });
        ContentHost.Children.Add(lifecycleResult);
    }
}
