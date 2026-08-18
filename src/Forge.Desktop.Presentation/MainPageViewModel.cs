using System.Globalization;
using System.Text;
using Forge.Application;
using Forge.Compiler;
using Forge.Configuration;
using Forge.Domain;
using Forge.Localization;

namespace Forge.Desktop.Presentation;

/// <summary>Everything the Desktop main page renders, resolved from durable application state.</summary>
public sealed record MainPageSnapshot(
    string StatusText,
    string ProjectRootText,
    string ProjectStateText,
    string StartupChecksText,
    string ProvidersText,
    string SuggestedActionsText,
    string SprintsText,
    string SprintDetailsText,
    string ConfigurationText,
    string DiagnosticsText,
    bool InitializeEnabled,
    bool RecoverEnabled);

/// <summary>
/// The Desktop main page's reusable orchestration, independent of any MAUI/WinUI control. The Windows host
/// (<c>Forge.Desktop</c>) assigns each resolved snapshot field to its controls; it owns no application logic itself.
/// </summary>
public sealed class MainPageViewModel(
    SurfaceText text,
    ForgeApplication application,
    Func<string?, CancellationToken, Task<IForgeMutations>>? resolveMutations = null)
{
    private readonly SurfaceText text = text ?? throw new ArgumentNullException(nameof(text));
    private readonly ForgeApplication application = application ?? throw new ArgumentNullException(nameof(application));
    // ADR 0005: every `.forge/` mutation routes through the project's Host once one is reachable.
    // A caller that supplied none (every existing test, and any bootstrap path where no project is
    // initialized yet) falls back to the local ForgeApplication, matching CliApplication's own
    // default.
    private readonly Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations =
        resolveMutations ?? ((_, _) => Task.FromResult<IForgeMutations>(application));
    // ADR 0005: cursor-driven, not a subscriber registry -- this page instance is the only place a
    // Desktop poll's progress lives, matching the CLI's own local `cursor` variable inside its
    // `--follow` loop. Reset on a project switch, since a cursor's watermarks are meaningless
    // against a different project's sprints.
    private string? eventsCursor;
    private string? eventsCursorProjectRoot;

    /// <summary>Restores the view from durable application state, never from serialized UI objects.
    /// <paramref name="sprintId"/> selects the sprint to expand; <see langword="null"/> or an empty
    /// value expands the active sprint, matching `forge tree` with no <c>--sprint</c>.</summary>
    public async Task<MainPageSnapshot> RefreshAsync(
        string? projectRoot,
        string? sprintId,
        CancellationToken cancellationToken)
    {
        // A supplied but malformed sprint id must never silently fall back to the active sprint —
        // the same edge case `forge status --detail full`/`tree` report as sprint_not_found. Only a
        // genuinely empty entry means "no sprint requested" here, because the Desktop entry is
        // always present and blank by default (unlike an omitted CLI option).
        bool sprintRequested = !string.IsNullOrWhiteSpace(sprintId);
        Guid requestedSprintId = default;
        bool sprintMalformed = sprintRequested && !Guid.TryParse(sprintId, out requestedSprintId);
        ProjectOverview overview = await application
            .GetOverviewAsync(
                projectRoot,
                // Summary for a malformed id: requesting Full with no id would resolve the active
                // sprint's detail section, i.e. a different sprint than the one asked for.
                sprintMalformed ? SnapshotDetail.Summary : SnapshotDetail.Full,
                sprintRequested && !sprintMalformed ? requestedSprintId : null,
                cancellationToken)
            .ConfigureAwait(false);
        StartupStatus startup = overview.Startup;
        ProjectSnapshot snapshot = overview.Snapshot;
        bool sprintNotFound = sprintRequested && snapshot.Details is null;
        ConfigurationView user = await application
            .GetUserConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);
        ConfigurationView project = await application
            .GetProjectConfigurationAsync(projectRoot, cancellationToken)
            .ConfigureAwait(false);
        return new(
            text.Resolve(SurfaceFormatting.StartupMessageKey(snapshot.Startup)),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{text.Resolve(MessageKeys.ProjectRootLabel)} {snapshot.Project.Root}"),
            text.Resolve(snapshot.Project.Initialized
                ? MessageKeys.ProjectInitialized
                : MessageKeys.ProjectNotInitialized),
            Render(
                text.Resolve(MessageKeys.StartupChecksTitle),
                startup.Checks.Select(check => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{SurfaceFormatting.Machine(check.Id)} {SurfaceFormatting.Machine(check.State)} {check.DiagnosticCode}"))),
            Render(
                text.Resolve(MessageKeys.ProviderToolchainTitle),
                snapshot.Providers.Select(SurfaceFormatting.ProviderRow)),
            snapshot.SuggestedActions.Count == 0
                ? text.Resolve(MessageKeys.NoSuggestedActions)
                : Render(
                    text.Resolve(MessageKeys.SuggestedActionsTitle),
                    snapshot.SuggestedActions.Select(action => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{action.Rank}. {action.ActionId} - {text.Resolve(action.RationaleKey)}"))),
            // Same shared projection `forge tree` renders (ADR 0005: both surfaces read one snapshot).
            Render(
                null,
                SurfaceFormatting.SprintTreeLines(
                    text,
                    snapshot.Sprints,
                    snapshot.ActiveSprintId,
                    snapshot.Details)),
            snapshot.Details is { } sprintDetails
                ? Render(null, SurfaceFormatting.SprintDetailLines(text, sprintDetails))
                : string.Empty,
            Render(
                null,
                user.Values
                    .Concat(project.Values)
                    .Select(value => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{value.Key} = {value.Value.GetRawText()} ({SurfaceFormatting.Machine(value.Provenance)})"))),
            Render(
                text.Resolve(MessageKeys.DiagnosticsTitle),
                new[]
                {
                    startup.Project.DiagnosticCode,
                    user.DiagnosticCode,
                    project.DiagnosticCode,
                    // The CLI reports an unusable --sprint value on its diagnostics channel; the
                    // Desktop equivalent is this section, not the sprint body — which stays empty
                    // rather than being overwritten with a raw machine code.
                    sprintNotFound ? DiagnosticCodes.SprintNotFound : DiagnosticCodes.None,
                }.Where(code => code != DiagnosticCodes.None).Distinct(StringComparer.Ordinal)),
            !snapshot.Project.Initialized && startup.AllowsProjectMutation,
            startup.FirstFailure is not null);
    }

    public Task<ProjectSnapshot> GetProjectSnapshotAsync(string? projectRoot, CancellationToken cancellationToken) =>
        application.GetProjectSnapshotAsync(projectRoot, cancellationToken);

    public string InitializePrompt(ProjectSnapshot snapshot) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{text.Resolve(MessageKeys.ProjectRootLabel)} {snapshot.Project.Root}");

    public async Task<string> InitializeAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SuggestedAction? suggestion = snapshot.SuggestedActions.FirstOrDefault(
            action => action.ActionId == ForgeApplication.InitializeProjectAction);
        InitializeProjectResult result = await application
            .InitializeProjectAsync(
                new(
                    snapshot.Project.Root,
                    true,
                    snapshot.StateVersion,
                    suggestion?.Command.IdempotencyKey ?? ForgeApplication.InitializationKey(snapshot)),
                cancellationToken)
            .ConfigureAwait(false);
        return Message(
            text.Resolve(result.DiagnosticCode switch
            {
                DiagnosticCodes.ProjectAlreadyInitialized => MessageKeys.InitAlreadyInitialized,
                DiagnosticCodes.None => MessageKeys.InitCompleted,
                _ => MessageKeys.InitFailed,
            }),
            result.DiagnosticCode);
    }

    public async Task<string> RecoverAsync(string? projectRoot, bool confirmed, CancellationToken cancellationToken)
    {
        IForgeMutations mutations = await resolveMutations(projectRoot, cancellationToken).ConfigureAwait(false);
        return await UseMutationsAsync(mutations, async () =>
        {
            RecoverStartupResult result = await mutations
                .RecoverStartupAsync(projectRoot, confirmed, cancellationToken)
                .ConfigureAwait(false);
            return Message(
                text.Resolve(result switch
                {
                    { Succeeded: true, Check: null } => MessageKeys.RecoveryNotNeeded,
                    { Succeeded: true } => MessageKeys.RecoveryCompleted,
                    _ => MessageKeys.RecoveryFailed,
                }),
                result.DiagnosticCode);
        }).ConfigureAwait(false);
    }

    public async Task<string> SetConfigurationAsync(
        ConfigurationScope scope,
        string? projectRoot,
        string key,
        string? value,
        CancellationToken cancellationToken)
    {
        // User-scope configuration is not `.forge/` project state (ADR 0005 protects the latter),
        // so it stays local even when a Host connection is available for project mutations.
        IForgeMutations mutations = scope == ConfigurationScope.Project
            ? await resolveMutations(projectRoot, cancellationToken).ConfigureAwait(false)
            : application;
        return await UseMutationsAsync(mutations, async () =>
        {
            ConfigurationWriteResult result = await mutations
                .SetConfigurationAsync(scope, projectRoot, key, value, cancellationToken)
                .ConfigureAwait(false);
            return Message(
                text.Resolve(result.Succeeded ? MessageKeys.ConfigurationUpdated : MessageKeys.ConfigurationRejected),
                result.DiagnosticCode);
        }).ConfigureAwait(false);
    }

    /// <summary>ADR 0005/0018's human-only `workflow.review` capability. <paramref name="sprintId"/>
    /// reuses the same entry the sprint-tree expansion uses: a blank value targets the active
    /// sprint, matching <see cref="RefreshAsync"/>'s own "blank means active sprint" rule (the page's
    /// default state — nothing typed yet — would otherwise always fail this action even while a
    /// gate is visibly `awaiting_human` in the tree above it). A non-blank, unparsable value is still
    /// reported the same way `forge gate approve|reject` reports an unparsable `--sprint`.
    /// <paramref name="nodeId"/> defaults to the canonical human-approval node only when
    /// <see langword="null"/>, matching the CLI's own `--node` default exactly — an empty or
    /// whitespace-only string is forwarded as-is, the same as the CLI would (see
    /// <see cref="GatePrompt"/>, which applies the identical rule for display).</summary>
    public async Task<string> ResolveGateAsync(
        string? projectRoot,
        string? sprintId,
        string? nodeId,
        bool approved,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        SprintTarget target = await ResolveSprintIdAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (target.SprintId is not { } resolvedSprintId)
        {
            // Two different reasons collapse to "no id" (StatusAdvisor.DetermineActiveSprint):
            // nothing non-terminal exists, or more than one does and Forge never silently picks
            // among them (ADR 0005). The latter needs a message that actually tells the user what
            // to do next -- "not found" would be wrong information, not merely terse, since the
            // candidate sprints are the ones already rendered in the tree above this action.
            return target.Ambiguous
                ? text.Resolve(MessageKeys.GateSprintAmbiguous)
                : Message(text.Resolve(MessageKeys.GateResolutionFailed), DiagnosticCodes.SprintNotFound);
        }

        string effectiveNodeId = nodeId ?? ImplementationCriticalGraphBuilder.HumanApprovalNodeId;
        IForgeMutations mutations = await resolveMutations(projectRoot, cancellationToken).ConfigureAwait(false);
        return await UseMutationsAsync(mutations, async () =>
        {
            NodeActionResult result = await mutations
                .ResolveGateAsync(projectRoot, resolvedSprintId, effectiveNodeId, approved, confirmed, cancellationToken)
                .ConfigureAwait(false);
            return Message(
                text.Resolve(result.Succeeded ? MessageKeys.GateResolved : MessageKeys.GateResolutionFailed),
                result.DiagnosticCode);
        }).ConfigureAwait(false);
    }

    /// <summary>A confirmation prompt naming the sprint/node a pending gate decision would act on —
    /// shown before <see cref="ResolveGateAsync"/> so the user can verify the target before an
    /// irreversible decision, using the same blank-means-active-sprint/human-approval defaulting
    /// rules that call itself applies (displayed as a placeholder rather than the resolved active
    /// sprint id, which would need its own round-trip just to render this prompt).</summary>
    public string GatePrompt(string? sprintId, string? nodeId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{text.Resolve(MessageKeys.SprintIdLabel)} " +
                $"{(string.IsNullOrWhiteSpace(sprintId) ? text.Resolve(MessageKeys.GateActiveSprintPlaceholder) : sprintId)}\n" +
                $"{text.Resolve(MessageKeys.GateNodeIdLabel)} " +
                $"{nodeId ?? ImplementationCriticalGraphBuilder.HumanApprovalNodeId}");

    /// <summary><paramref name="Ambiguous"/> distinguishes "more than one non-terminal sprint, Forge
    /// never silently picks one" (<see cref="SprintId"/> is <see langword="null"/> but a sprint id
    /// entry would resolve it) from "genuinely none" (entering one would not help either).</summary>
    private readonly record struct SprintTarget(Guid? SprintId, bool Ambiguous);

    private async Task<SprintTarget> ResolveSprintIdAsync(
        string? projectRoot, string? sprintId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(sprintId))
        {
            return new(Guid.TryParse(sprintId, out Guid parsed) ? parsed : null, false);
        }

        ProjectSnapshot snapshot = await application
            .GetProjectSnapshotAsync(projectRoot, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot.ActiveSprintId is { } activeSprintId)
        {
            return new(activeSprintId, false);
        }

        bool ambiguous = snapshot.Sprints.Count(sprint => !WorkflowStateMachines.IsTerminal(sprint.State)) > 1;
        return new(null, ambiguous);
    }

    /// <summary>ADR 0005/0018's human-only `attempt.supersede` capability. <paramref name="sprintId"/>
    /// shares <see cref="ResolveGateAsync"/>'s blank-means-active-sprint/ambiguity resolution exactly
    /// (see <see cref="ResolveSprintIdAsync"/>). An unparsable <paramref name="attemptId"/> is reported
    /// the same way `forge attempt supersede`'s own attempt-id argument is (`WorkflowEventConflict`,
    /// not a dedicated "not found" code). <paramref name="instruction"/> is validated to the same
    /// bound `SprintScheduler.MaxSupersessionInstructionLength` enforces server-side, checked
    /// client-side first only because the full text is already in memory — an Entry, unlike the CLI's
    /// file-or-stdin source, has nothing left to stream.</summary>
    public async Task<string> SupersedeAttemptAsync(
        string? projectRoot,
        string? sprintId,
        string? attemptId,
        string? instruction,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        SprintTarget target = await ResolveSprintIdAsync(projectRoot, sprintId, cancellationToken)
            .ConfigureAwait(false);
        if (target.SprintId is not { } resolvedSprintId)
        {
            return target.Ambiguous
                ? text.Resolve(MessageKeys.AttemptSupersedeSprintAmbiguous)
                : Message(text.Resolve(MessageKeys.AttemptSupersedeFailed), DiagnosticCodes.SprintNotFound);
        }

        if (!Guid.TryParse(attemptId, out Guid resolvedAttemptId))
        {
            return Message(text.Resolve(MessageKeys.AttemptSupersedeFailed), DiagnosticCodes.WorkflowEventConflict);
        }

        // Verbatim, never trimmed -- the CLI forwards its own instruction source (a file or stdin)
        // exactly as read, trailing whitespace included, and this must record the same durable text
        // for identical input.
        string effectiveInstruction = instruction ?? string.Empty;
        // Bound checked before emptiness, matching CliApplication.ReadInstructionAsync's own order
        // exactly: a whitespace-only instruction that is also over the bound must report the same
        // diagnostic on both surfaces (supersession_instruction_too_long), not
        // supersession_instruction_required on one and _too_long on the other.
        if (effectiveInstruction.Length > SprintScheduler.MaxSupersessionInstructionLength)
        {
            return Message(
                text.Resolve(MessageKeys.AttemptSupersedeFailed), DiagnosticCodes.SupersessionInstructionTooLong);
        }

        if (string.IsNullOrWhiteSpace(effectiveInstruction))
        {
            return Message(
                text.Resolve(MessageKeys.AttemptSupersedeFailed), DiagnosticCodes.SupersessionInstructionRequired);
        }

        IForgeMutations mutations = await resolveMutations(projectRoot, cancellationToken).ConfigureAwait(false);
        return await UseMutationsAsync(mutations, async () =>
        {
            CompleteAttemptResult result = await mutations
                .SupersedeAttemptAsync(
                    projectRoot, resolvedSprintId, resolvedAttemptId, effectiveInstruction, confirmed,
                    cancellationToken)
                .ConfigureAwait(false);
            return Message(
                text.Resolve(result.Succeeded ? MessageKeys.AttemptSuperseded : MessageKeys.AttemptSupersedeFailed),
                result.DiagnosticCode);
        }).ConfigureAwait(false);
    }

    /// <summary>A confirmation prompt naming the sprint/attempt a pending supersession would act on,
    /// mirroring <see cref="GatePrompt"/>'s own shape and defaulting rules exactly.</summary>
    public string AttemptSupersedePrompt(string? sprintId, string? attemptId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{text.Resolve(MessageKeys.SprintIdLabel)} " +
                $"{(string.IsNullOrWhiteSpace(sprintId) ? text.Resolve(MessageKeys.GateActiveSprintPlaceholder) : sprintId)}\n" +
                $"{text.Resolve(MessageKeys.AttemptIdLabel)} " +
                $"{(string.IsNullOrWhiteSpace(attemptId) ? text.Resolve(MessageKeys.AttemptIdMissingPlaceholder) : attemptId)}");

    /// <summary>ADR 0005's `control.events` read-only capability, sharing <see cref="ForgeApplication.ReadControlEventsAsync"/>
    /// with `forge events` directly -- a query, so this needs no confirmation and no Host round-trip
    /// through <see cref="resolveMutations"/>. Each call advances the stored cursor and renders the
    /// page via <see cref="SurfaceFormatting.EventLines"/>, the same lines `forge events` prints,
    /// so the two can never drift. A rejected cursor (<see cref="DiagnosticCodes.ControlCursorStale"/>)
    /// still stores the fresh anchor <see cref="ControlEventsReader"/> returns for it -- the next poll
    /// naturally starts over rather than needing its own recovery path, since redisplaying an event
    /// already shown has no side effect here (unlike a delivered notification, nothing to dedup
    /// against).</summary>
    public async Task<string> PollEventsAsync(string? projectRoot, CancellationToken cancellationToken)
    {
        if (!string.Equals(projectRoot, eventsCursorProjectRoot, StringComparison.Ordinal))
        {
            eventsCursor = null;
            eventsCursorProjectRoot = projectRoot;
        }

        ControlEventsPage page = await application
            .ReadControlEventsAsync(projectRoot, eventsCursor, cancellationToken)
            .ConfigureAwait(false);
        eventsCursor = page.Cursor;
        return Message(Render(null, SurfaceFormatting.EventLines(text, page)), page.DiagnosticCode);
    }

    /// <summary>Disposes <paramref name="mutations"/> after <paramref name="action"/> completes, whether
    /// it succeeds or throws — a resolved Host connection is scoped to one action, never kept alive
    /// across calls. A no-op for the local <see cref="ForgeApplication"/> fallback, which implements
    /// neither <see cref="IDisposable"/> nor <see cref="IAsyncDisposable"/>.</summary>
    private static async Task<T> UseMutationsAsync<T>(IForgeMutations mutations, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            if (mutations is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string Message(string message, string diagnosticCode) =>
        diagnosticCode == DiagnosticCodes.None
            ? message
            : string.Create(CultureInfo.InvariantCulture, $"{message} ({diagnosticCode})");

    private static string Render(string? title, IEnumerable<string> lines)
    {
        StringBuilder builder = new();
        if (title is not null)
        {
            builder.AppendLine(title);
        }

        foreach (string line in lines)
        {
            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

}
