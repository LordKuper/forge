using System.CommandLine;
using System.Globalization;
using Forge.Application;
using Forge.Compiler;
using Forge.Configuration;
using Forge.Domain;
using Forge.Infrastructure;
using Forge.Localization;
using Forge.Providers;
using Forge.Updater;

namespace Forge.Cli;

public static class CliApplication
{
    public static RootCommand CreateRootCommand(
        SurfaceText text,
        TextWriter output,
        ForgeApplication application,
        TextWriter? error = null,
        Func<CancellationToken, ValueTask<InstallationResult>>? install = null,
        Func<CancellationToken, ValueTask<UpdateResult>>? update = null,
        Func<string?, CancellationToken, Task<IForgeMutations>>? resolveMutations = null,
        TextReader? input = null,
        Func<bool>? isInteractive = null,
        ProjectCatalogStore? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(application);
        TextWriter diagnostics = error ?? output;
        TextReader effectiveInput = input ?? Console.In;
        // ADR 0023: deliberately checks *output*, not input, redirection. `forge attempt supersede`
        // reads its replacement instruction from `--instruction-file -` (standard input) as a
        // documented, ordinary invocation shape — checking `Console.IsInputRedirected` would refuse
        // that exact shape unconditionally, since piping an instruction always redirects stdin
        // regardless of whether a human or an agent is doing the piping. Output redirection is the
        // signal every one of this command tree's real callers actually varies on: a human at an
        // interactive shell has an attached terminal for stdout even when piping instruction text
        // in, while an agent subprocess invoked through `.forge/rules` has both its streams
        // redirected so its host tool can capture them. This lambda, not a value computed here, is
        // what each command action calls -- see ADR 0023 for why a human deliberately redirecting
        // their OWN output (e.g. `| tee log.txt`) is an accepted, named false-refusal, not a case
        // this signal can distinguish from a non-interactive agent.
        Func<bool> effectiveIsInteractive = isInteractive ?? (() => !Console.IsOutputRedirected);
        // ADR 0005: every `.forge/` mutation routes through the project's Host once one is
        // reachable. `resolveMutations` receives the SAME `--project-root` value the invoking
        // command resolved (never a value fixed before argument parsing), so a Host connection is
        // always scoped to the project the command actually targets, matching every read command's
        // own resolution. A caller that supplied none (every existing test, and any bootstrap path
        // where no project is initialized yet) falls back to the local ForgeApplication, which
        // implements the same interface directly and ignores the root it's handed the same way.
        Func<string?, CancellationToken, Task<IForgeMutations>> effectiveResolver =
            resolveMutations ?? ((_, _) => Task.FromResult<IForgeMutations>(application));

        RootCommand root = new(text.Resolve(MessageKeys.AppDescription));
        root.Subcommands.Add(CreateDoctorCommand(text, output, diagnostics, application, effectiveResolver));
        root.Subcommands.Add(CreateEvaluateCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateInitCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateStatusCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateNextCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateEventsCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateTreeCommand(text, output, diagnostics, application));
        root.Subcommands.Add(
            CreateSprintCommand(text, output, diagnostics, application, effectiveResolver, effectiveIsInteractive));
        root.Subcommands.Add(CreateWorkspaceCommand(text, output, diagnostics, application, catalog));
        if (catalog is not null)
        {
            root.Subcommands.Add(CreateProjectCommand(text, output, diagnostics, catalog));
        }

        root.Subcommands.Add(CreateModelsCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateConfigCommand(text, output, diagnostics, application, effectiveResolver));
        root.Subcommands.Add(CreateIntegrationCommand(text, output, diagnostics, application, effectiveResolver));
        root.Subcommands.Add(
            CreateGateCommand(text, output, diagnostics, effectiveResolver, effectiveIsInteractive));
        root.Subcommands.Add(CreateAttemptCommand(
            text, output, diagnostics, effectiveResolver, effectiveInput, effectiveIsInteractive));
        root.Subcommands.Add(
            CreateConfirmCommand(text, output, diagnostics, effectiveResolver, effectiveIsInteractive));
        root.Subcommands.Add(
            CreateTestWorkCommand(text, output, diagnostics, effectiveResolver, effectiveIsInteractive));
        root.Subcommands.Add(
            CreateFinalizeCommand(text, output, diagnostics, effectiveResolver, effectiveIsInteractive));
        if (install is not null)
        {
            root.Subcommands.Add(CreateInstallCommand(text, output, install));
        }

        if (update is not null)
        {
            root.Subcommands.Add(CreateUpdateCommand(text, output, update));
        }

        return root;
    }

    private static Option<string?> CreateProjectRootOption() =>
        new("--project-root") { Description = "Absolute project directory." };

    private static Option<bool> CreateJsonOption() =>
        new("--json") { Description = "Emit the culture-invariant machine contract." };

    private static Command CreateDoctorCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<bool> startup = new("--startup") { Description = "Show the startup checks." };
        Option<bool> recover = new("--recover") { Description = "Quarantine unreadable configuration." };
        Option<bool> confirm = new("--yes") { Description = "Confirm the recovery." };
        Option<bool> bundle = new("--bundle")
        {
            Description = "Emit an allowlisted, redacted diagnostic bundle (ADR 0005/0038) as JSON.",
        };
        Command command = new("doctor", text.Resolve(MessageKeys.DoctorDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(startup);
        command.Options.Add(recover);
        command.Options.Add(confirm);
        command.Options.Add(bundle);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string? root = parseResult.GetValue(projectRoot);
            if (parseResult.GetValue(recover))
            {
                IForgeMutations mutations = await resolveMutations(root, cancellationToken).ConfigureAwait(false);
                RecoverStartupResult recovered = await mutations
                    .RecoverStartupAsync(root, parseResult.GetValue(confirm), cancellationToken)
                    .ConfigureAwait(false);
                output.WriteLine(text.Resolve(RecoveryMessage(recovered)));
                return Report(diagnostics, recovered.DiagnosticCode);
            }

            if (parseResult.GetValue(bundle))
            {
                DiagnosticBundle diagnosticBundle = await application
                    .CollectDiagnosticBundleAsync(root, cancellationToken)
                    .ConfigureAwait(false);
                output.WriteLine(StatusJson.Serialize(diagnosticBundle));
                return ExitCodes.Ok;
            }

            StartupStatus status = await application
                .GetStartupStatusAsync(root, cancellationToken)
                .ConfigureAwait(false);
            output.WriteLine(text.Resolve(SurfaceFormatting.StartupMessageKey(status.State)));
            if (parseResult.GetValue(startup))
            {
                output.WriteLine(text.Resolve(MessageKeys.StartupChecksTitle));
                foreach (StartupCheck check in status.Checks)
                {
                    output.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {SurfaceFormatting.Machine(check.Id)} {SurfaceFormatting.Machine(check.State)} {check.DiagnosticCode}"));
                }
            }

            WriteProject(text, output, status.Project);
            return status.FirstFailure is { } failure
                ? Report(diagnostics, failure.DiagnosticCode)
                : ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>ADR 0042's `forge eval`: pass/fail evaluation of the updater, provider, bootstrap,
    /// and workflow subsystems plus the project model-policy gate, printed as JSON like
    /// `forge doctor --bundle`. Only a `Failed` check moves the exit code off <see cref="ExitCodes.Ok"/>
    /// -- a `Blocked` check (e.g. an uninitialized project) is reported but not itself a failure,
    /// matching <c>StartupStatus.FirstFailure</c>'s own Failed-only convention.</summary>
    private static Command CreateEvaluateCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Command command = new("eval", text.Resolve(MessageKeys.EvalDescription));
        command.Options.Add(projectRoot);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            EvaluationReport report = await application
                .RunEvaluationAsync(parseResult.GetValue(projectRoot), cancellationToken)
                .ConfigureAwait(false);
            output.WriteLine(StatusJson.Serialize(report));
            EvaluationCheck? failure = report.Checks
                .FirstOrDefault(check => check.State == EvaluationState.Failed);
            return failure is not null ? Report(diagnostics, failure.DiagnosticCode) : ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateInitCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<bool> confirm = new("--yes") { Description = "Confirm the displayed directory." };
        Command command = new("init", text.Resolve(MessageKeys.InitDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(confirm);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string? root = parseResult.GetValue(projectRoot);
            // The command dispatches exactly what the recommendation exposes, including its
            // expected state version and idempotency key.
            ProjectSnapshot snapshot = await application
                .GetProjectSnapshotAsync(root, cancellationToken)
                .ConfigureAwait(false);
            SuggestedAction? suggestion = snapshot.SuggestedActions.FirstOrDefault(
                action => action.ActionId == ForgeApplication.InitializeProjectAction);
            InitializeProjectResult result = await application
                .InitializeProjectAsync(
                    new(
                        root,
                        parseResult.GetValue(confirm),
                        snapshot.StateVersion,
                        suggestion?.Command.IdempotencyKey ??
                            ForgeApplication.InitializationKey(snapshot)),
                    cancellationToken)
                .ConfigureAwait(false);
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{text.Resolve(MessageKeys.ProjectRootLabel)} {result.Root}"));
            output.WriteLine(text.Resolve(InitMessage(result)));
            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    private static Command CreateStatusCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<bool> json = CreateJsonOption();
        Option<string?> detail = new("--detail")
        {
            Description = "Snapshot detail: summary (default) or full.",
        };
        Option<string?> sprint = new("--sprint")
        {
            Description = "Sprint id whose node/attempt/finding/routing detail to include (implies full detail).",
        };
        Command command = new("status", text.Resolve(MessageKeys.StatusDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(json);
        command.Options.Add(detail);
        command.Options.Add(sprint);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string? rawSprint = parseResult.GetValue(sprint);
            // Any explicitly supplied value — including "" or whitespace — must go through the
            // Guid.TryParse guard below; treating a blank value as "not requested" would let it
            // silently fall back to GetOverviewAsync's own active-sprint resolution instead.
            bool sprintRequested = rawSprint is not null;
            // A malformed --sprint value must never reach GetOverviewAsync as "no explicit id" —
            // with --detail full that resolves to the active sprint instead of being reported.
            Guid parsedSprintId = default;
            if (sprintRequested && !Guid.TryParse(rawSprint, out parsedSprintId))
            {
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            Guid? sprintId = sprintRequested ? parsedSprintId : null;
            SnapshotDetail requestedDetail = string.Equals(
                parseResult.GetValue(detail), "full", StringComparison.OrdinalIgnoreCase)
                ? SnapshotDetail.Full
                : SnapshotDetail.Summary;
            ProjectOverview overview = await application
                .GetOverviewAsync(parseResult.GetValue(projectRoot), requestedDetail, sprintId, cancellationToken)
                .ConfigureAwait(false);
            // A well-formed but unknown sprint id never resolves a Details section — that must be
            // reported too, not silently treated as "no sprint requested". This leaves every other
            // exit-code case exactly as before (the project startup diagnostic stays
            // informational-only on this read command).
            bool sprintNotFound = sprintRequested && overview.Snapshot.Details is null;
            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(overview.Snapshot));
                return sprintNotFound ? Report(diagnostics, DiagnosticCodes.SprintNotFound) : ExitCodes.Ok;
            }

            output.WriteLine(text.Resolve(SurfaceFormatting.StartupMessageKey(overview.Snapshot.Startup)));
            WriteProject(text, output, overview.Startup.Project);
            WriteSprints(text, output, overview.Snapshot.Sprints, overview.Snapshot.ActiveSprintId);
            if (overview.Snapshot.Details is { } sprintDetails)
            {
                WriteSprintDetails(text, output, sprintDetails);
            }

            WriteActions(text, output, overview.Snapshot.SuggestedActions);
            WriteDiagnostic(diagnostics, overview.Startup.Project.DiagnosticCode);
            return sprintNotFound ? Report(diagnostics, DiagnosticCodes.SprintNotFound) : ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateEventsCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<string?> after = new("--after") { Description = "Resume from a previously returned cursor." };
        Option<bool> follow = new("--follow") { Description = "Keep polling for new events until canceled." };
        Option<bool> json = CreateJsonOption();
        Command command = new("events", text.Resolve(MessageKeys.EventsDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(after);
        command.Options.Add(follow);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string? root = parseResult.GetValue(projectRoot);
            bool jsonOutput = parseResult.GetValue(json);
            bool followMode = parseResult.GetValue(follow);
            string? cursor = parseResult.GetValue(after);
            while (true)
            {
                ControlEventsPage page = await application
                    .ReadControlEventsAsync(root, cursor, cancellationToken)
                    .ConfigureAwait(false);
                cursor = page.Cursor;
                if (jsonOutput)
                {
                    output.WriteLine(ControlEventsJson.Serialize(page));
                }
                else
                {
                    WriteEvents(text, output, page);
                }

                if (page.DiagnosticCode != DiagnosticCodes.None)
                {
                    return Report(diagnostics, page.DiagnosticCode);
                }

                if (!followMode || cancellationToken.IsCancellationRequested)
                {
                    return ExitCodes.Ok;
                }

                // Bounded short polling (ADR 0005): no subscriber registry, no streaming socket.
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        });
        return command;
    }

    /// <summary>ADR 0005: "`forge status`, `next`, `tree`, `sprint inspect`... are local projections
    /// of this DTO; they are not separate Host queries." `tree` nests attempts under their owning
    /// node (via <see cref="EntityStatus.OwnerId"/>), which the flat lists in `status` don't show.</summary>
    private static Command CreateTreeCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<string?> sprint = new("--sprint")
        {
            Description = "Sprint id to expand (default: active sprint).",
        };
        Option<bool> json = CreateJsonOption();
        Command command = new("tree", text.Resolve(MessageKeys.TreeDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(sprint);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string? rawSprint = parseResult.GetValue(sprint);
            // Any explicitly supplied value — including "" or whitespace — must go through the
            // Guid.TryParse guard below; treating a blank value as "not requested" (like
            // string.IsNullOrWhiteSpace would) let it silently fall back to the active sprint too.
            bool sprintRequested = rawSprint is not null;
            // Unlike `status` (which only reaches SnapshotDetail.Full on an explicit --detail full),
            // `tree` always requests Full — so a malformed --sprint value must never silently fall
            // back to GetOverviewAsync's own "no explicit id" active-sprint resolution.
            Guid parsedSprintId = default;
            if (sprintRequested && !Guid.TryParse(rawSprint, out parsedSprintId))
            {
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            Guid? sprintId = sprintRequested ? parsedSprintId : null;
            ProjectOverview overview = await application
                .GetOverviewAsync(parseResult.GetValue(projectRoot), SnapshotDetail.Full, sprintId, cancellationToken)
                .ConfigureAwait(false);
            bool sprintNotFound = sprintRequested && overview.Snapshot.Details is null;
            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(overview.Snapshot));
                return sprintNotFound ? Report(diagnostics, DiagnosticCodes.SprintNotFound) : ExitCodes.Ok;
            }

            WriteProject(text, output, overview.Startup.Project);
            WriteSprintTree(text, output, overview.Snapshot.Sprints, overview.Snapshot.ActiveSprintId, overview.Snapshot.Details);
            WriteDiagnostic(diagnostics, overview.Startup.Project.DiagnosticCode);
            return sprintNotFound ? Report(diagnostics, DiagnosticCodes.SprintNotFound) : ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateSprintCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        Func<bool> isInteractive)
    {
        Command command = new("sprint", text.Resolve(MessageKeys.SprintDescription));
        command.Subcommands.Add(CreateSprintInspectCommand(text, output, diagnostics, application));
        command.Subcommands.Add(CreateSprintCreateCommand(text, output, diagnostics, resolveMutations));
        command.Subcommands.Add(CreateSprintRunCommand(text, output, diagnostics, resolveMutations));
        command.Subcommands.Add(CreateSprintResumeCommand(text, output, diagnostics, resolveMutations));
        command.Subcommands.Add(CreateSprintCancelCommand(text, output, diagnostics, resolveMutations));
        command.Subcommands.Add(CreateSprintAssessStageCommand(text, output, diagnostics, application));
        command.Subcommands.Add(
            CreateSprintMoveStageCommand(text, output, diagnostics, application, resolveMutations, isInteractive));
        command.Subcommands.Add(CreateSprintTimelineCommand(text, output, diagnostics, application));
        return command;
    }

    /// <summary>Plan section 6.3's reserved `sprint.timeline` query. Reads directly against
    /// <paramref name="application"/> like <see cref="CreateSprintAssessStageCommand"/> -- the
    /// durable, file-based journal is the sole source of truth regardless of whether a separate Host
    /// process is also running.</summary>
    private static Command CreateSprintTimelineCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<string?> after = new("--after") { Description = "Opaque cursor from a prior page." };
        Option<bool> json = CreateJsonOption();
        Argument<string> id = new("id") { Description = "Sprint id." };
        Command command = new("timeline", text.Resolve(MessageKeys.SprintTimelineDescription));
        command.Arguments.Add(id);
        command.Options.Add(projectRoot);
        command.Options.Add(after);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (!Guid.TryParse(parseResult.GetValue(id), out Guid sprintId))
            {
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            SprintTimelinePage page = await application
                .GetSprintTimelineAsync(
                    parseResult.GetValue(projectRoot), sprintId, parseResult.GetValue(after), cancellationToken)
                .ConfigureAwait(false);
            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(page));
                return Report(diagnostics, page.DiagnosticCode);
            }

            WriteTimeline(output, page);
            return Report(diagnostics, page.DiagnosticCode);
        });
        return command;
    }

    private static Command CreateSprintAssessStageCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<string> targetStage = new("--target-stage") { Description = "Target stage id.", Required = true };
        Option<bool> json = CreateJsonOption();
        Argument<string> id = new("id") { Description = "Sprint id." };
        Command command = new("assess-stage", text.Resolve(MessageKeys.SprintAssessStageDescription));
        command.Arguments.Add(id);
        command.Options.Add(projectRoot);
        command.Options.Add(targetStage);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (!Guid.TryParse(parseResult.GetValue(id), out Guid sprintId))
            {
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            StageTransitionAssessment assessment = await application
                .AssessStageTransitionAsync(
                    parseResult.GetValue(projectRoot), sprintId, parseResult.GetValue(targetStage)!, cancellationToken)
                .ConfigureAwait(false);
            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(assessment));
            }
            else if (assessment.Found)
            {
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{assessment.SourceStageId} -> {assessment.TargetStageId}: {assessment.Direction}, allowed={assessment.Allowed}"));
                foreach (StagePrerequisite prerequisite in assessment.UnsatisfiedPrerequisites)
                {
                    output.WriteLine(string.Create(
                        CultureInfo.InvariantCulture, $"  blocked: {prerequisite.Id} ({prerequisite.MessageKey})"));
                }
            }

            return Report(diagnostics, assessment.Found ? DiagnosticCodes.None : assessment.DiagnosticCode);
        });
        return command;
    }

    /// <summary>ADR 0046's human-only `sprint.move_stage` capability. Same interactive-session
    /// technical control and mandatory, never-bypassed confirmation as
    /// <see cref="CreateAttemptStopCommand"/> -- moving a sprint to another stage is as
    /// irreversible-in-effect as stopping its active operation. The assessment read below always
    /// runs locally against <paramref name="application"/>, exactly like every other read command in
    /// this file (<see cref="CreateSprintInspectCommand"/>'s own "queries run directly against the
    /// durable event log" convention) -- a query needs no Host-routing indirection, since the
    /// durable, file-based journal is the sole source of truth either way. The commit itself still
    /// goes through <paramref name="resolveMutations"/> so a reachable Host remains the one process
    /// that ever mutates `.forge/` (ADR 0005). The Host recomputes this exact assessment fresh
    /// immediately before committing and rejects a mismatch (plan section 8.5); this CLI-side read
    /// only supplies the expected version/token the mutation call presents, never trusted on its
    /// own.</summary>
    private static Command CreateSprintMoveStageCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        Func<bool> isInteractive)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<string> targetStage = new("--target-stage") { Description = "Target stage id.", Required = true };
        Option<string?> reason = new("--reason") { Description = "Why the sprint is moving to this stage." };
        Option<bool> confirm = new("--yes") { Description = "Confirm the move." };
        Argument<string> id = new("id") { Description = "Sprint id." };
        Command command = new("move-stage", text.Resolve(MessageKeys.SprintMoveStageDescription));
        command.Arguments.Add(id);
        command.Options.Add(projectRoot);
        command.Options.Add(targetStage);
        command.Options.Add(reason);
        command.Options.Add(confirm);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // ADR 0023: same earliest-reachable, unconditional refusal as attempt stop/supersede.
            if (!isInteractive())
            {
                return Report(diagnostics, DiagnosticCodes.PermissionDenied);
            }

            if (!Guid.TryParse(parseResult.GetValue(id), out Guid sprintId))
            {
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            string? root = parseResult.GetValue(projectRoot);
            string targetStageId = parseResult.GetValue(targetStage)!;
            StageTransitionAssessment assessment = await application
                .AssessStageTransitionAsync(root, sprintId, targetStageId, cancellationToken)
                .ConfigureAwait(false);
            if (!assessment.Found)
            {
                return Report(diagnostics, assessment.DiagnosticCode);
            }

            IForgeMutations mutations = await resolveMutations(root, cancellationToken).ConfigureAwait(false);
            MoveStageResult result = await mutations.MoveSprintToStageAsync(
                root, sprintId, targetStageId, assessment.ExpectedStateVersion, assessment.AssessmentToken,
                parseResult.GetValue(reason), parseResult.GetValue(confirm), Guid.NewGuid(), cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                output.WriteLine(text.Resolve(MessageKeys.SprintStageMoved));
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    private static Command CreateSprintCreateCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Command command = new("create", text.Resolve(MessageKeys.SprintCreateDescription));
        command.Options.Add(projectRoot);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string? root = parseResult.GetValue(projectRoot);
            IForgeMutations mutations = await resolveMutations(root, cancellationToken).ConfigureAwait(false);
            CreateSprintResult result =
                await mutations.CreateSprintAsync(root, cancellationToken).ConfigureAwait(false);
            if (SurfaceFormatting.SprintCreatedMessage(text, result) is { } message)
            {
                output.WriteLine(message);
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    private static Command CreateSprintRunCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations) =>
        CreateSprintTransitionCommand(
            text, output, diagnostics, resolveMutations, "run", MessageKeys.SprintRunDescription,
            MessageKeys.SprintAdvanced, includeResultingState: true,
            (mutations, root, sprintId, cancellationToken) =>
                mutations.RunSprintAsync(root, sprintId, cancellationToken));

    private static Command CreateSprintResumeCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations) =>
        CreateSprintTransitionCommand(
            text, output, diagnostics, resolveMutations, "resume", MessageKeys.SprintResumeDescription,
            MessageKeys.SprintResumed, includeResultingState: false,
            (mutations, root, sprintId, cancellationToken) =>
                mutations.ResumeSprintAsync(root, sprintId, cancellationToken));

    private static Command CreateSprintTransitionCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        string name,
        string descriptionKey,
        string successKey,
        bool includeResultingState,
        Func<IForgeMutations, string?, Guid, CancellationToken, Task<SprintTransitionResult>> transition)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<string> sprint = new("--sprint") { Description = "Sprint id.", Required = true };
        Command command = new(name, text.Resolve(descriptionKey));
        command.Options.Add(projectRoot);
        command.Options.Add(sprint);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string? root = parseResult.GetValue(projectRoot);
            if (!Guid.TryParse(parseResult.GetValue(sprint), out Guid sprintId))
            {
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            IForgeMutations mutations = await resolveMutations(root, cancellationToken).ConfigureAwait(false);
            SprintTransitionResult result =
                await transition(mutations, root, sprintId, cancellationToken).ConfigureAwait(false);
            if (SurfaceFormatting.SprintTransitionMessage(text, result, successKey, includeResultingState) is
                { } message)
            {
                output.WriteLine(message);
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    private static Command CreateSprintCancelCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<string> sprint = new("--sprint") { Description = "Sprint id.", Required = true };
        Option<bool> confirm = new("--yes") { Description = "Confirm cancellation." };
        Command command = new("cancel", text.Resolve(MessageKeys.SprintCancelDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(sprint);
        command.Options.Add(confirm);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string? root = parseResult.GetValue(projectRoot);
            if (!Guid.TryParse(parseResult.GetValue(sprint), out Guid sprintId))
            {
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            IForgeMutations mutations = await resolveMutations(root, cancellationToken).ConfigureAwait(false);
            SprintTransitionResult result = await mutations
                .CancelSprintAsync(root, sprintId, parseResult.GetValue(confirm), cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                output.WriteLine(text.Resolve(MessageKeys.SprintCancelled));
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    private static Command CreateSprintInspectCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<bool> json = CreateJsonOption();
        Argument<string> id = new("id") { Description = "Sprint id." };
        Command command = new("inspect", text.Resolve(MessageKeys.SprintInspectDescription));
        command.Arguments.Add(id);
        command.Options.Add(projectRoot);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (!Guid.TryParse(parseResult.GetValue(id), out Guid sprintId))
            {
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            ProjectOverview overview = await application
                .GetOverviewAsync(parseResult.GetValue(projectRoot), SnapshotDetail.Full, sprintId, cancellationToken)
                .ConfigureAwait(false);
            bool sprintNotFound = overview.Snapshot.Details is null;
            if (parseResult.GetValue(json))
            {
                // Matching `tree`/`status`: the machine contract always comes back well-formed, even
                // on a not-found id, instead of collapsing to empty stdout.
                output.WriteLine(StatusJson.Serialize(overview.Snapshot));
                return sprintNotFound ? Report(diagnostics, DiagnosticCodes.SprintNotFound) : ExitCodes.Ok;
            }

            if (sprintNotFound)
            {
                // A null Details section isn't always "no such sprint" — an uninitialized or missing
                // project reports it too. Surface that underlying diagnostic first, matching `status`
                // and `tree`, instead of masking it behind sprint_not_found.
                WriteDiagnostic(diagnostics, overview.Startup.Project.DiagnosticCode);
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            WriteSprintDetails(text, output, overview.Snapshot.Details!);
            WriteDiagnostic(diagnostics, overview.Startup.Project.DiagnosticCode);
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>ADR 0005/0018's human-only `workflow.review` capability. ADR 0019 originally
    /// recorded this honestly as policy-only ("no technical caller-identity control, only
    /// mandatory, non-bypassable confirmation"); ADR 0023 adds the first real technical control
    /// (the interactive-session check below) on top of that confirmation, still not claiming
    /// unforgeable caller identity — see ADR 0023 for what it does and does not close.</summary>
    private static Command CreateGateCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        Func<bool> isInteractive)
    {
        Command command = new("gate", text.Resolve(MessageKeys.GateDescription));
        command.Subcommands.Add(CreateGateResolveCommand(
            text, output, diagnostics, resolveMutations, isInteractive, "approve",
            MessageKeys.GateApproveDescription, approved: true));
        command.Subcommands.Add(CreateGateResolveCommand(
            text, output, diagnostics, resolveMutations, isInteractive, "reject",
            MessageKeys.GateRejectDescription, approved: false));
        return command;
    }

    private static Command CreateGateResolveCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        Func<bool> isInteractive,
        string name,
        string descriptionKey,
        bool approved)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<string> sprint = new("--sprint") { Description = "Sprint id.", Required = true };
        Option<string> node = new("--node")
        {
            Description = "Gate node id. Defaults to the canonical human_approval node.",
        };
        Option<bool> confirm = new("--yes") { Description = "Confirm the decision." };
        Command command = new(name, text.Resolve(descriptionKey));
        command.Options.Add(projectRoot);
        command.Options.Add(sprint);
        command.Options.Add(node);
        command.Options.Add(confirm);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // ADR 0023: refused before any of this action's OWN validation runs -- the earliest
            // point reachable once System.CommandLine has already parsed `--sprint`/`--node`/`--yes`
            // into `parseResult` -- and unconditional: `--yes` cannot substitute for an interactive
            // session.
            if (!isInteractive())
            {
                return Report(diagnostics, DiagnosticCodes.PermissionDenied);
            }

            string? root = parseResult.GetValue(projectRoot);
            if (!Guid.TryParse(parseResult.GetValue(sprint), out Guid sprintId))
            {
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            string nodeId = parseResult.GetValue(node) ?? ImplementationCriticalGraphBuilder.HumanApprovalNodeId;
            IForgeMutations mutations = await resolveMutations(root, cancellationToken).ConfigureAwait(false);
            NodeActionResult result = await mutations
                .ResolveGateAsync(
                    root, sprintId, nodeId, approved, parseResult.GetValue(confirm), cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                output.WriteLine(text.Resolve(MessageKeys.GateResolved));
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    /// <summary>The human-only `workflow.confirm` capability. Deliberately shaped like
    /// <see cref="CreateGateCommand"/>: one noun, two verb subcommands (<c>confirmed</c>/
    /// <c>not-confirmed</c>, matching <see cref="ConfirmationOutcome"/>'s own two values rather than
    /// gate's approve/reject vocabulary, since this records a definition-of-done judgment, not a
    /// gate decision), mandatory <c>--yes</c> with no config-driven bypass, and the same ADR 0023
    /// interactive-session check every human-only command shares. Only a single evidence entry is
    /// supported this slice (`--evidence-kind`/`--evidence`) — `confirmation-result.schema.json`
    /// allows more than one, but nothing in this CLI's own vocabulary yet needs it; deferred rather
    /// than building repeatable-option parsing this codebase has no other precedent for.</summary>
    private static Command CreateConfirmCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        Func<bool> isInteractive)
    {
        Command command = new("confirm", text.Resolve(MessageKeys.ConfirmDescription));
        command.Subcommands.Add(CreateConfirmRecordCommand(
            text, output, diagnostics, resolveMutations, isInteractive, "confirmed",
            MessageKeys.ConfirmConfirmedDescription, outcome: ConfirmationOutcome.Confirmed));
        command.Subcommands.Add(CreateConfirmRecordCommand(
            text, output, diagnostics, resolveMutations, isInteractive, "not-confirmed",
            MessageKeys.ConfirmNotConfirmedDescription, outcome: ConfirmationOutcome.NotConfirmed));
        return command;
    }

    private static Command CreateConfirmRecordCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        Func<bool> isInteractive,
        string name,
        string descriptionKey,
        ConfirmationOutcome outcome)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<string> sprint = new("--sprint") { Description = "Sprint id.", Required = true };
        Option<string> node = new("--node")
        {
            Description = "Confirmation node id. Defaults to the canonical confirmation node.",
        };
        Option<string> definitionOfDone = new("--definition-of-done")
        {
            Description = "What was checked against the definition of done.",
            Required = true,
        };
        Option<string> evidenceKind = new("--evidence-kind")
        {
            Description = "One of: inspection, execution, existing-check.",
            Required = true,
        };
        Option<string> evidence = new("--evidence") { Description = "Evidence description.", Required = true };
        Option<bool> confirm = new("--yes") { Description = "Confirm the decision." };
        Command command = new(name, text.Resolve(descriptionKey));
        command.Options.Add(projectRoot);
        command.Options.Add(sprint);
        command.Options.Add(node);
        command.Options.Add(definitionOfDone);
        command.Options.Add(evidenceKind);
        command.Options.Add(evidence);
        command.Options.Add(confirm);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // ADR 0023: same earliest-reachable, unconditional refusal every human-only command
            // shares -- before this action's own sprint-id/evidence-kind validation.
            if (!isInteractive())
            {
                return Report(diagnostics, DiagnosticCodes.PermissionDenied);
            }

            string? root = parseResult.GetValue(projectRoot);
            if (!Guid.TryParse(parseResult.GetValue(sprint), out Guid sprintId))
            {
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            if (ParseEvidenceKind(parseResult.GetValue(evidenceKind)) is not { } kind)
            {
                return Report(diagnostics, DiagnosticCodes.ConfirmationEvidenceKindInvalid);
            }

            string definitionOfDoneValue = parseResult.GetValue(definitionOfDone) ?? string.Empty;
            string evidenceValue = parseResult.GetValue(evidence) ?? string.Empty;
            // Checked here, before StartAttemptAsync ever runs (inside ConfirmNodeAsync below) --
            // `confirmation-result.schema.json`'s own `minLength: 1` would eventually reject an
            // empty value too, but only after the node was already durably moved to `running`.
            if (string.IsNullOrWhiteSpace(definitionOfDoneValue) || string.IsNullOrWhiteSpace(evidenceValue))
            {
                return Report(diagnostics, DiagnosticCodes.ConfirmationTextRequired);
            }

            string nodeId = parseResult.GetValue(node) ?? ImplementationCriticalGraphBuilder.ConfirmationNodeId;
            IForgeMutations mutations = await resolveMutations(root, cancellationToken).ConfigureAwait(false);
            RecordConfirmationResult result = await mutations
                .ConfirmNodeAsync(
                    root, sprintId, nodeId, outcome, definitionOfDoneValue,
                    [new ConfirmationEvidence(kind, evidenceValue)],
                    parseResult.GetValue(confirm), cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                output.WriteLine(text.Resolve(MessageKeys.ConfirmRecorded));
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    private static ConfirmationEvidenceKind? ParseEvidenceKind(string? value) => value switch
    {
        "inspection" => ConfirmationEvidenceKind.Inspection,
        "execution" => ConfirmationEvidenceKind.Execution,
        "existing-check" => ConfirmationEvidenceKind.ExistingCheck,
        _ => null,
    };

    /// <summary>The human-only `workflow.test_work` capability. Deliberately shaped exactly like
    /// <see cref="CreateConfirmCommand"/>: one noun, two verb subcommands matching
    /// <see cref="TestWorkOutcome"/>'s own two values, mandatory `--yes` with no config-driven
    /// bypass, and the same ADR 0023 interactive-session check every human-only command
    /// shares.</summary>
    private static Command CreateTestWorkCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        Func<bool> isInteractive)
    {
        Command command = new("test-work", text.Resolve(MessageKeys.TestWorkDescription));
        command.Subcommands.Add(CreateTestWorkRecordCommand(
            text, output, diagnostics, resolveMutations, isInteractive, "added",
            MessageKeys.TestWorkAddedDescription, outcome: TestWorkOutcome.TestsAdded));
        command.Subcommands.Add(CreateTestWorkRecordCommand(
            text, output, diagnostics, resolveMutations, isInteractive, "no-new-tests",
            MessageKeys.TestWorkNoNewTestsDescription, outcome: TestWorkOutcome.NoNewTestsJustified));
        return command;
    }

    private static Command CreateTestWorkRecordCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        Func<bool> isInteractive,
        string name,
        string descriptionKey,
        TestWorkOutcome outcome)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<string> sprint = new("--sprint") { Description = "Sprint id.", Required = true };
        Option<string> node = new("--node")
        {
            Description = "Test-work node id. Defaults to the canonical test_work node.",
        };
        Option<string> justification = new("--justification")
        {
            Description = "Why new tests were added, or why none were needed.",
            Required = true,
        };
        Option<bool> confirm = new("--yes") { Description = "Confirm the decision." };
        Command command = new(name, text.Resolve(descriptionKey));
        command.Options.Add(projectRoot);
        command.Options.Add(sprint);
        command.Options.Add(node);
        command.Options.Add(justification);
        command.Options.Add(confirm);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // ADR 0023: same earliest-reachable, unconditional refusal every human-only command
            // shares -- before this action's own sprint-id/justification validation.
            if (!isInteractive())
            {
                return Report(diagnostics, DiagnosticCodes.PermissionDenied);
            }

            string? root = parseResult.GetValue(projectRoot);
            if (!Guid.TryParse(parseResult.GetValue(sprint), out Guid sprintId))
            {
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            string justificationValue = parseResult.GetValue(justification) ?? string.Empty;
            // Checked here, before StartAttemptAsync ever runs (inside RecordTestWorkAsync below) --
            // `test-work-result.schema.json`'s own `minLength: 1` would eventually reject an empty
            // value too, but only after the node was already durably moved to `running`.
            if (string.IsNullOrWhiteSpace(justificationValue))
            {
                return Report(diagnostics, DiagnosticCodes.TestWorkJustificationRequired);
            }

            string nodeId = parseResult.GetValue(node) ?? ImplementationCriticalGraphBuilder.TestWorkNodeId;
            IForgeMutations mutations = await resolveMutations(root, cancellationToken).ConfigureAwait(false);
            RecordTestWorkResult result = await mutations
                .RecordTestWorkAsync(
                    root, sprintId, nodeId, outcome, justificationValue, parseResult.GetValue(confirm),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                output.WriteLine(text.Resolve(MessageKeys.TestWorkRecorded));
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    /// <summary>The human-only `workflow.finalize` capability (ADR 0036). Unlike
    /// <see cref="CreateConfirmCommand"/>/<see cref="CreateTestWorkCommand"/>, there is no outcome
    /// choice to make — finalization only ever attempts the same merge — so this is a single command
    /// rather than a noun with two verb subcommands. Same ADR 0023 interactive-session check and
    /// mandatory, never-bypassed `--yes` every other human-only command shares.</summary>
    private static Command CreateFinalizeCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        Func<bool> isInteractive)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<string> sprint = new("--sprint") { Description = "Sprint id.", Required = true };
        Option<string> node = new("--node")
        {
            Description = "Finalization node id. Defaults to the canonical finalization node.",
        };
        Option<bool> confirm = new("--yes") { Description = "Confirm the merge." };
        Command command = new("finalize", text.Resolve(MessageKeys.FinalizeDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(sprint);
        command.Options.Add(node);
        command.Options.Add(confirm);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // ADR 0023: same earliest-reachable, unconditional refusal every human-only command
            // shares -- before this action's own sprint-id validation.
            if (!isInteractive())
            {
                return Report(diagnostics, DiagnosticCodes.PermissionDenied);
            }

            string? root = parseResult.GetValue(projectRoot);
            if (!Guid.TryParse(parseResult.GetValue(sprint), out Guid sprintId))
            {
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            string nodeId = parseResult.GetValue(node) ?? ImplementationCriticalGraphBuilder.FinalizationNodeId;
            IForgeMutations mutations = await resolveMutations(root, cancellationToken).ConfigureAwait(false);
            FinalizeSprintResult result = await mutations
                .FinalizeSprintAsync(root, sprintId, nodeId, parseResult.GetValue(confirm), cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                output.WriteLine(text.Resolve(MessageKeys.SprintFinalized));
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    /// <summary>ADR 0005/0018's human-only `attempt.supersede` capability. See
    /// <see cref="CreateGateCommand"/>'s remark — both commands now share ADR 0023's same
    /// interactive-session technical control, on top of the mandatory, never-bypassed confirmation
    /// this remark used to describe as the only control.</summary>
    private static Command CreateAttemptCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        TextReader input,
        Func<bool> isInteractive)
    {
        Command command = new("attempt", text.Resolve(MessageKeys.AttemptDescription));
        command.Subcommands.Add(
            CreateAttemptSupersedeCommand(text, output, diagnostics, resolveMutations, input, isInteractive));
        command.Subcommands.Add(
            CreateAttemptStopCommand(text, output, diagnostics, resolveMutations, isInteractive));
        return command;
    }

    private static Command CreateAttemptSupersedeCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        TextReader input,
        Func<bool> isInteractive)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<string> sprint = new("--sprint") { Description = "Sprint id.", Required = true };
        Option<string> instructionFile = new("--instruction-file")
        {
            Description = "Path to the replacement instruction, or '-' to read it from standard input.",
            Required = true,
        };
        Option<bool> confirm = new("--yes") { Description = "Confirm the supersession." };
        Argument<string> id = new("attempt-id") { Description = "Attempt id." };
        Command command = new("supersede", text.Resolve(MessageKeys.AttemptSupersedeDescription));
        command.Arguments.Add(id);
        command.Options.Add(projectRoot);
        command.Options.Add(sprint);
        command.Options.Add(instructionFile);
        command.Options.Add(confirm);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // ADR 0023: same earliest-reachable, unconditional refusal as the gate commands -- before
            // this action's own sprint-id/attempt-id validation and before reading the instruction
            // file/stdin.
            if (!isInteractive())
            {
                return Report(diagnostics, DiagnosticCodes.PermissionDenied);
            }

            if (!Guid.TryParse(parseResult.GetValue(sprint), out Guid sprintId))
            {
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            if (!Guid.TryParse(parseResult.GetValue(id), out Guid attemptId))
            {
                return Report(diagnostics, DiagnosticCodes.WorkflowEventConflict);
            }

            InstructionReadResult read = await ReadInstructionAsync(
                    parseResult.GetValue(instructionFile)!, input, cancellationToken)
                .ConfigureAwait(false);
            if (read.DiagnosticCode != DiagnosticCodes.None)
            {
                return Report(diagnostics, read.DiagnosticCode);
            }

            string? root = parseResult.GetValue(projectRoot);
            IForgeMutations mutations = await resolveMutations(root, cancellationToken).ConfigureAwait(false);
            CompleteAttemptResult result = await mutations
                .SupersedeAttemptAsync(
                    root, sprintId, attemptId, read.Instruction!, parseResult.GetValue(confirm), cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                output.WriteLine(text.Resolve(MessageKeys.AttemptSuperseded));
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    /// <summary>ADR 0044's human-only `workflow.stop_operation` capability. Same interactive-session
    /// technical control and mandatory, never-bypassed confirmation as <see cref="CreateAttemptSupersedeCommand"/>
    /// -- stopping the exact active operation is as irreversible-in-effect as superseding it.</summary>
    private static Command CreateAttemptStopCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        Func<bool> isInteractive)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<string> sprint = new("--sprint") { Description = "Sprint id.", Required = true };
        Option<bool> confirm = new("--yes") { Description = "Confirm stopping the active operation." };
        Argument<string> id = new("attempt-id") { Description = "Attempt id." };
        Command command = new("stop", text.Resolve(MessageKeys.AttemptStopDescription));
        command.Arguments.Add(id);
        command.Options.Add(projectRoot);
        command.Options.Add(sprint);
        command.Options.Add(confirm);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // ADR 0023: same earliest-reachable, unconditional refusal as attempt supersede/gate.
            if (!isInteractive())
            {
                return Report(diagnostics, DiagnosticCodes.PermissionDenied);
            }

            if (!Guid.TryParse(parseResult.GetValue(sprint), out Guid sprintId))
            {
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            if (!Guid.TryParse(parseResult.GetValue(id), out Guid attemptId))
            {
                return Report(diagnostics, DiagnosticCodes.WorkflowEventConflict);
            }

            string? root = parseResult.GetValue(projectRoot);
            IForgeMutations mutations = await resolveMutations(root, cancellationToken).ConfigureAwait(false);
            StopOperationResult result = await mutations
                .StopCurrentOperationAsync(root, sprintId, attemptId, parseResult.GetValue(confirm), cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                output.WriteLine(text.Resolve(MessageKeys.AttemptStopped));
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    private sealed record InstructionReadResult(string? Instruction, string DiagnosticCode)
    {
        public static InstructionReadResult Success(string instruction) => new(instruction, DiagnosticCodes.None);

        public static InstructionReadResult Failure(string diagnosticCode) => new(null, diagnosticCode);
    }

    /// <summary>Reads the replacement instruction from standard input (<c>-</c>) or a file, bounded
    /// to <see cref="SprintScheduler.MaxSupersessionInstructionLength"/> characters read either way
    /// — never buffers an unbounded stream just to reject it afterward. Distinguishes three failure
    /// modes <c>SupersedeAttemptAsync</c> itself does not need to (it only ever sees a string): the
    /// source could not be read at all (missing file, permission denied, an invalid path),
    /// the content that *was* read is over the bound, or it is empty/whitespace-only — a
    /// human-initiated supersession with nothing actually said defeats its own purpose.</summary>
    private static async Task<InstructionReadResult> ReadInstructionAsync(
        string path, TextReader input, CancellationToken cancellationToken)
    {
        string content;
        try
        {
            if (path == "-")
            {
                content = await ReadBoundedAsync(input, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using FileStream stream = File.OpenRead(path);
                using StreamReader reader = new(stream);
                content = await ReadBoundedAsync(reader, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return InstructionReadResult.Failure(DiagnosticCodes.SupersessionInstructionUnreadable);
        }

        if (content.Length > SprintScheduler.MaxSupersessionInstructionLength)
        {
            return InstructionReadResult.Failure(DiagnosticCodes.SupersessionInstructionTooLong);
        }

        return string.IsNullOrWhiteSpace(content)
            ? InstructionReadResult.Failure(DiagnosticCodes.SupersessionInstructionRequired)
            : InstructionReadResult.Success(content);
    }

    /// <summary>Reads at most <see cref="SprintScheduler.MaxSupersessionInstructionLength"/> + 1
    /// characters — enough to detect an over-length source without reading further, whether that
    /// source is a bounded file or an unbounded pipe on standard input.</summary>
    private static async Task<string> ReadBoundedAsync(TextReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[SprintScheduler.MaxSupersessionInstructionLength + 1];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await reader
                .ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return new string(buffer, 0, totalRead);
    }

    private static Command CreateNextCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<bool> json = CreateJsonOption();
        Command command = new("next", text.Resolve(MessageKeys.NextDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            ProjectOverview overview = await application
                .GetOverviewAsync(parseResult.GetValue(projectRoot), cancellationToken)
                .ConfigureAwait(false);
            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(overview.Snapshot.SuggestedActions));
                return ExitCodes.Ok;
            }

            WriteActions(text, output, overview.Snapshot.SuggestedActions);
            WriteDiagnostic(diagnostics, overview.Startup.Project.DiagnosticCode);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateModelsCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application)
    {
        Option<bool> json = CreateJsonOption();
        Option<bool> refresh = new("--refresh")
        {
            Description = "Re-check every enabled provider against the latest release and " +
                "install or update only when needed.",
        };
        Command command = new("models", text.Resolve(MessageKeys.ModelsDescription));
        command.Options.Add(json);
        command.Options.Add(refresh);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            ProviderToolchainStatus status = parseResult.GetValue(refresh)
                ? await application.RefreshProviderHealthAsync(cancellationToken).ConfigureAwait(false)
                : await application.GetProviderHealthAsync(cancellationToken).ConfigureAwait(false);
            // The aggregate diagnostic is driven purely by enabled providers (ADR 0008: a disabled
            // provider is never part of readiness) — computed from `status`, not the entries below.
            string diagnosticCode = status.SharedDiagnosticCode;
            IReadOnlyList<ProviderHealthEntry> entries = application.ProjectProviderHealth(status);
            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(new ProviderHealth(ProviderHealth.ContractVersion, entries)));
                return Report(diagnostics, diagnosticCode);
            }

            output.WriteLine(text.Resolve(MessageKeys.ProviderToolchainTitle));
            foreach (ProviderHealthEntry entry in entries)
            {
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {SurfaceFormatting.ProviderRow(entry)}"));
            }

            return Report(diagnostics, diagnosticCode);
        });
        return command;
    }

    private static Command CreateConfigCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Command command = new("config", text.Resolve(MessageKeys.ConfigDescription));
        Command show = new("show", text.Resolve(MessageKeys.ConfigurationTitle));
        show.Options.Add(projectRoot);
        show.SetAction(async (parseResult, cancellationToken) =>
        {
            ConfigurationView user = await application
                .GetUserConfigurationAsync(cancellationToken)
                .ConfigureAwait(false);
            ConfigurationView project = await application
                .GetProjectConfigurationAsync(parseResult.GetValue(projectRoot), cancellationToken)
                .ConfigureAwait(false);
            output.WriteLine(text.Resolve(MessageKeys.ConfigurationTitle));
            WriteValues(output, user.Values);
            WriteValues(output, project.Values);
            WriteDiagnostic(diagnostics, project.DiagnosticCode);
            return Report(diagnostics, user.DiagnosticCode);
        });
        command.Subcommands.Add(show);
        // User-scope configuration is not project state and stays local (see IForgeMutations'
        // remarks); ForgeApplication implements IForgeMutations directly, so resolving to it
        // unconditionally is the same call it already made. Only project scope resolves through
        // `resolveMutations`.
        command.Subcommands.Add(CreateConfigSetCommand(
            text,
            output,
            diagnostics,
            (_, _) => Task.FromResult<IForgeMutations>(application),
            ConfigurationScope.User));
        command.Subcommands.Add(CreateConfigSetCommand(
            text,
            output,
            diagnostics,
            resolveMutations,
            ConfigurationScope.Project));
        return command;
    }

    private static Command CreateConfigSetCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        ConfigurationScope scope)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Argument<string> key = new("key");
        Argument<string> value = new("value");
        Command command = new(
            scope == ConfigurationScope.User ? "user" : "project",
            text.Resolve(MessageKeys.ConfigDescription));
        command.Arguments.Add(key);
        command.Arguments.Add(value);
        command.Options.Add(projectRoot);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string? root = parseResult.GetValue(projectRoot);
            IForgeMutations mutations = await resolveMutations(root, cancellationToken).ConfigureAwait(false);
            ConfigurationWriteResult result = await mutations
                .SetConfigurationAsync(
                    scope,
                    root,
                    parseResult.GetValue(key)!,
                    parseResult.GetValue(value),
                    cancellationToken)
                .ConfigureAwait(false);
            output.WriteLine(text.Resolve(result.Succeeded
                ? MessageKeys.ConfigurationUpdated
                : MessageKeys.ConfigurationRejected));
            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    private static Command CreateIntegrationCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations)
    {
        Command command = new("integration", text.Resolve(MessageKeys.IntegrationDescription));
        Command skill = new("skill", text.Resolve(MessageKeys.IntegrationSkillDescription));
        skill.Subcommands.Add(CreateIntegrationGenerateCommand(text, output, diagnostics, application));
        skill.Subcommands.Add(CreateIntegrationWriteCommand(
            text, output, diagnostics, resolveMutations, "install",
            MessageKeys.IntegrationInstallDescription,
            (mutations, root, confirmed, ct) => mutations.InstallIntegrationAsync(root, confirmed, ct)));
        skill.Subcommands.Add(CreateIntegrationWriteCommand(
            text, output, diagnostics, resolveMutations, "remove",
            MessageKeys.IntegrationRemoveDescription,
            (mutations, root, confirmed, ct) => mutations.RemoveIntegrationAsync(root, confirmed, ct)));
        command.Subcommands.Add(skill);
        return command;
    }

    private static Command CreateIntegrationGenerateCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<bool> json = CreateJsonOption();
        Command command = new("generate", text.Resolve(MessageKeys.IntegrationGenerateDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            IntegrationInspectionResult result = await application
                .InspectIntegrationAsync(parseResult.GetValue(projectRoot), cancellationToken)
                .ConfigureAwait(false);
            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(result));
                return Report(diagnostics, result.DiagnosticCode);
            }

            foreach (string line in SurfaceFormatting.IntegrationInspectionLines(text, result))
            {
                output.WriteLine(line);
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    private static Command CreateIntegrationWriteCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations,
        string name,
        string descriptionKey,
        Func<IForgeMutations, string?, bool, CancellationToken, Task<IntegrationWriteResult>> write)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<bool> confirm = new("--yes") { Description = "Confirm the write." };
        Command command = new(name, text.Resolve(descriptionKey));
        command.Options.Add(projectRoot);
        command.Options.Add(confirm);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string? root = parseResult.GetValue(projectRoot);
            IForgeMutations mutations = await resolveMutations(root, cancellationToken).ConfigureAwait(false);
            IntegrationWriteResult result = await write(
                    mutations, root, parseResult.GetValue(confirm), cancellationToken)
                .ConfigureAwait(false);
            foreach (string line in SurfaceFormatting.IntegrationWriteLines(text, result))
            {
                output.WriteLine(line);
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    private static Command CreateInstallCommand(
        SurfaceText text,
        TextWriter output,
        Func<CancellationToken, ValueTask<InstallationResult>> install)
    {
        Command command = new("install", text.Resolve(MessageKeys.InstallDescription));
        command.SetAction(async (_, cancellationToken) =>
        {
            InstallationResult result = await install(cancellationToken).ConfigureAwait(false);
            output.WriteLine(result.Succeeded
                ? text.Resolve(MessageKeys.InstallCompleted)
                : text.Resolve(MessageKeys.InstallFailed));
            return result.Succeeded ? ExitCodes.Ok : ExitCodes.Update;
        });
        return command;
    }

    private static Command CreateUpdateCommand(
        SurfaceText text,
        TextWriter output,
        Func<CancellationToken, ValueTask<UpdateResult>> update)
    {
        Command command = new("update", text.Resolve(MessageKeys.UpdateDescription));
        command.SetAction(async (_, cancellationToken) =>
        {
            UpdateResult result = await update(cancellationToken).ConfigureAwait(false);
            output.WriteLine(result.Diagnostic.Code == UpdateDiagnosticCode.None
                ? text.Resolve(MessageKeys.UpdateCompleted)
                : text.Resolve(MessageKeys.UpdateFailed));
            return result.Diagnostic.Code == UpdateDiagnosticCode.None
                ? ExitCodes.Ok
                : ExitCodes.Update;
        });
        return command;
    }

    /// <summary>Diagnostics go to standard error; machine stdout carries only the contract.</summary>
    /// <summary>Plan section 6.2/6.4's reserved `workspace.summary`/`workspace.available_actions`
    /// queries. `summary` fans out across every catalog entry itself (a Host is scoped to one
    /// project -- ADR 0005/0049) rather than through any one project's Host; `actions` targets one
    /// already-resolved project, optionally one sprint within it, exactly like every other read
    /// command in this file.</summary>
    private static Command CreateWorkspaceCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application,
        ProjectCatalogStore? catalog)
    {
        Command command = new("workspace", text.Resolve(MessageKeys.WorkspaceDescription));
        command.Subcommands.Add(CreateWorkspaceSummaryCommand(text, output, diagnostics, application, catalog));
        command.Subcommands.Add(CreateWorkspaceActionsCommand(text, output, diagnostics, application));
        return command;
    }

    private static Command CreateWorkspaceSummaryCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application,
        ProjectCatalogStore? catalog)
    {
        Option<bool> json = CreateJsonOption();
        Command command = new("summary", text.Resolve(MessageKeys.WorkspaceSummaryDescription));
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (catalog is null)
            {
                return Report(diagnostics, DiagnosticCodes.ProjectCatalogEntryNotFound);
            }

            ProjectCatalogListing listing = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
            if (listing.DiagnosticCode != DiagnosticCodes.None)
            {
                return Report(diagnostics, listing.DiagnosticCode);
            }

            IReadOnlyList<ProjectCatalogEntry> entries = listing.Entries;
            List<(ProjectCatalogEntry Entry, ProjectWorkspaceSummary Summary)> rows = new(entries.Count);
            foreach (ProjectCatalogEntry entry in entries)
            {
                ProjectWorkspaceSummary summary = await application
                    .GetWorkspaceSummaryAsync(entry.Root, cancellationToken)
                    .ConfigureAwait(false);
                rows.Add((entry, summary));
            }

            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(rows.Select(row => row.Summary).ToList()));
                return ExitCodes.Ok;
            }

            if (rows.Count == 0)
            {
                output.WriteLine(text.Resolve(MessageKeys.NoProjects));
                return ExitCodes.Ok;
            }

            foreach ((ProjectCatalogEntry entry, ProjectWorkspaceSummary summary) in rows)
            {
                string label = entry.Alias ?? entry.Root;
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{label} [{entry.Root}] available={summary.Available} " +
                        $"startup={SurfaceFormatting.Machine(summary.StartupState)} " +
                        $"active_sprints={summary.ActiveSprints.Count} attention={summary.AttentionSprintIds.Count}"));
                foreach (SprintWorkspaceSummary sprint in summary.ActiveSprints)
                {
                    output.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {sprint.SprintId} {SurfaceFormatting.Machine(sprint.State)} " +
                            $"stage={sprint.CurrentStageId ?? "-"} progress={sprint.StagesCompleted}/{sprint.StagesTotal} " +
                            $"active_operation={sprint.HasActiveOperation}"));
                }
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateWorkspaceActionsCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application)
    {
        Option<string?> projectRoot = CreateProjectRootOption();
        Option<string?> sprint = new("--sprint") { Description = "Sprint id. Omit for project-level actions." };
        Option<bool> json = CreateJsonOption();
        Command command = new("actions", text.Resolve(MessageKeys.WorkspaceActionsDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(sprint);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string? sprintText = parseResult.GetValue(sprint);
            Guid? sprintId = null;
            if (sprintText is not null)
            {
                if (!Guid.TryParse(sprintText, out Guid parsed))
                {
                    return Report(diagnostics, DiagnosticCodes.SprintNotFound);
                }

                sprintId = parsed;
            }

            IReadOnlyList<AvailableAction> actions = await application
                .GetAvailableActionsAsync(parseResult.GetValue(projectRoot), sprintId, cancellationToken)
                .ConfigureAwait(false);
            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(actions));
                return ExitCodes.Ok;
            }

            if (actions.Count == 0)
            {
                output.WriteLine(text.Resolve(MessageKeys.NoSuggestedActions));
                return ExitCodes.Ok;
            }

            foreach (AvailableAction action in actions)
            {
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {action.ActionId} enabled={action.Enabled} " +
                        $"confirm={action.ConfirmationRequired} safety={SurfaceFormatting.Machine(action.SafetyClass)}"));
                foreach (string blocker in action.Blockers)
                {
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    blocked: {blocker}"));
                }
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>Plan section 6.1's user-scoped project catalog (ADR 0043/0049) -- entirely local,
    /// with no capability id and no Host protocol surface: every subcommand here reads or writes
    /// only the catalog file, never a project's own `.forge/` directory.</summary>
    private static Command CreateProjectCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ProjectCatalogStore catalog)
    {
        Command command = new("project", text.Resolve(MessageKeys.ProjectDescription));
        command.Subcommands.Add(CreateProjectAddCommand(text, output, diagnostics, catalog));
        command.Subcommands.Add(CreateProjectRemoveCommand(text, output, diagnostics, catalog));
        command.Subcommands.Add(CreateProjectRelinkCommand(text, output, diagnostics, catalog));
        command.Subcommands.Add(CreateProjectAliasCommand(text, output, diagnostics, catalog));
        command.Subcommands.Add(CreateProjectListCommand(text, output, diagnostics, catalog));
        command.Subcommands.Add(CreateProjectSelectCommand(text, output, diagnostics, catalog));
        return command;
    }

    private static Command CreateProjectAddCommand(
        SurfaceText text, TextWriter output, TextWriter diagnostics, ProjectCatalogStore catalog)
    {
        Argument<string> root = new("root") { Description = "Absolute path of an already-initialized project." };
        Command command = new("add", text.Resolve(MessageKeys.ProjectAddDescription));
        command.Arguments.Add(root);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            ProjectCatalogResult result = await catalog
                .AddAsync(parseResult.GetValue(root), cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                output.WriteLine(text.Resolve(MessageKeys.ProjectAdded));
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    private static Command CreateProjectRemoveCommand(
        SurfaceText text, TextWriter output, TextWriter diagnostics, ProjectCatalogStore catalog)
    {
        Argument<string> id = new("id") { Description = "Project id." };
        Command command = new("remove", text.Resolve(MessageKeys.ProjectRemoveDescription));
        command.Arguments.Add(id);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (!Guid.TryParse(parseResult.GetValue(id), out Guid projectId))
            {
                return Report(diagnostics, DiagnosticCodes.ProjectCatalogEntryNotFound);
            }

            ProjectCatalogResult result = await catalog.RemoveAsync(projectId, cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                output.WriteLine(text.Resolve(MessageKeys.ProjectRemoved));
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    private static Command CreateProjectRelinkCommand(
        SurfaceText text, TextWriter output, TextWriter diagnostics, ProjectCatalogStore catalog)
    {
        Argument<string> id = new("id") { Description = "Project id." };
        Argument<string> newRoot = new("new-root") { Description = "The project's new absolute root." };
        Command command = new("relink", text.Resolve(MessageKeys.ProjectRelinkDescription));
        command.Arguments.Add(id);
        command.Arguments.Add(newRoot);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (!Guid.TryParse(parseResult.GetValue(id), out Guid projectId))
            {
                return Report(diagnostics, DiagnosticCodes.ProjectCatalogEntryNotFound);
            }

            ProjectCatalogResult result = await catalog
                .RelinkAsync(projectId, parseResult.GetValue(newRoot), cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                output.WriteLine(text.Resolve(MessageKeys.ProjectRelinked));
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    private static Command CreateProjectAliasCommand(
        SurfaceText text, TextWriter output, TextWriter diagnostics, ProjectCatalogStore catalog)
    {
        Argument<string> id = new("id") { Description = "Project id." };
        Argument<string?> alias = new("alias") { Description = "New display alias, or empty to clear it." };
        Command command = new("alias", text.Resolve(MessageKeys.ProjectAliasDescription));
        command.Arguments.Add(id);
        command.Arguments.Add(alias);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (!Guid.TryParse(parseResult.GetValue(id), out Guid projectId))
            {
                return Report(diagnostics, DiagnosticCodes.ProjectCatalogEntryNotFound);
            }

            ProjectCatalogResult result = await catalog
                .SetAliasAsync(projectId, parseResult.GetValue(alias), cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                output.WriteLine(text.Resolve(MessageKeys.ProjectAliasSet));
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    private static Command CreateProjectListCommand(
        SurfaceText text, TextWriter output, TextWriter diagnostics, ProjectCatalogStore catalog)
    {
        Option<bool> json = CreateJsonOption();
        Command command = new("list", text.Resolve(MessageKeys.ProjectListDescription));
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            ProjectCatalogListing listing = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
            if (listing.DiagnosticCode != DiagnosticCodes.None)
            {
                return Report(diagnostics, listing.DiagnosticCode);
            }

            IReadOnlyList<ProjectCatalogEntry> entries = listing.Entries;
            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(entries));
                return ExitCodes.Ok;
            }

            if (entries.Count == 0)
            {
                output.WriteLine(text.Resolve(MessageKeys.NoProjects));
                return ExitCodes.Ok;
            }

            foreach (ProjectCatalogEntry entry in entries)
            {
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {entry.ProjectId} {entry.Alias ?? "-"} {entry.Root} " +
                        $"last_opened={entry.LastOpenedAt:O} last_sprint={entry.LastSelectedSprintId?.ToString("D") ?? "-"} " +
                        $"last_route={entry.LastRoute ?? "-"}"));
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateProjectSelectCommand(
        SurfaceText text, TextWriter output, TextWriter diagnostics, ProjectCatalogStore catalog)
    {
        Argument<string> id = new("id") { Description = "Project id." };
        Option<string?> sprint = new("--sprint") { Description = "Sprint id last selected in this project." };
        Option<string?> route = new("--route") { Description = "Last selected route within this project." };
        Command command = new("select", text.Resolve(MessageKeys.ProjectSelectDescription));
        command.Arguments.Add(id);
        command.Options.Add(sprint);
        command.Options.Add(route);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (!Guid.TryParse(parseResult.GetValue(id), out Guid projectId))
            {
                return Report(diagnostics, DiagnosticCodes.ProjectCatalogEntryNotFound);
            }

            string? sprintText = parseResult.GetValue(sprint);
            Guid? sprintId = null;
            if (sprintText is not null)
            {
                if (!Guid.TryParse(sprintText, out Guid parsed))
                {
                    return Report(diagnostics, DiagnosticCodes.SprintNotFound);
                }

                sprintId = parsed;
            }

            ProjectCatalogResult result = await catalog
                .SelectAsync(projectId, sprintId, parseResult.GetValue(route), cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                output.WriteLine(text.Resolve(MessageKeys.ProjectSelected));
            }

            return Report(diagnostics, result.DiagnosticCode);
        });
        return command;
    }

    /// <summary>Plan section 12.3's redaction guarantee is enforced twice: pass 1 when
    /// <see cref="SprintTimelineProjector"/> builds each item (before it could ever be persisted by a
    /// future cache), and pass 2 -- <see cref="SprintTimelineRedaction.Apply"/> -- inside
    /// <see cref="ForgeApplication.GetSprintTimelineAsync"/>, the single method both this text render
    /// and the `--json` branch above already call to obtain <paramref name="page"/>. This method
    /// re-runs <see cref="SecretRedactor"/> over the fully formatted line as a third, independent,
    /// belt-and-braces check specific to the exact bytes this surface renders, so a redaction gap in
    /// either upstream pass alone still cannot leak a raw secret to the terminal.</summary>
    private static void WriteTimeline(TextWriter output, SprintTimelinePage page)
    {
        if (page.Items.Count == 0)
        {
            return;
        }

        foreach (SprintTimelineItem item in page.Items)
        {
            string argumentText = string.Join(
                ' ',
                item.Arguments
                    .OrderBy(argument => argument.Key, StringComparer.Ordinal)
                    .Select(argument => $"{argument.Key}={argument.Value}"));
            string line = string.Create(
                CultureInfo.InvariantCulture,
                $"{item.OccurredAt:O} {SurfaceFormatting.Machine(item.Actor)} {item.TargetKind}:{item.TargetId} " +
                    $"{item.MessageKey} {argumentText}");
            output.WriteLine(SecretRedactor.Redact(line));
        }
    }

    private static int Report(TextWriter diagnostics, string diagnosticCode)
    {
        WriteDiagnostic(diagnostics, diagnosticCode);
        return ExitCodes.For(diagnosticCode);
    }

    private static void WriteDiagnostic(TextWriter diagnostics, string diagnosticCode)
    {
        if (diagnosticCode != DiagnosticCodes.None)
        {
            diagnostics.WriteLine(diagnosticCode);
        }
    }

    private static void WriteProject(SurfaceText text, TextWriter output, ProjectRootStatus project)
    {
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{text.Resolve(MessageKeys.ProjectRootLabel)} {project.Root}"));
        output.WriteLine(text.Resolve(project.Initialized
            ? MessageKeys.ProjectInitialized
            : MessageKeys.ProjectNotInitialized));
    }

    private static void WriteActions(
        SurfaceText text,
        TextWriter output,
        IReadOnlyList<SuggestedAction> actions)
    {
        if (actions.Count == 0)
        {
            output.WriteLine(text.Resolve(MessageKeys.NoSuggestedActions));
            return;
        }

        output.WriteLine(text.Resolve(MessageKeys.SuggestedActionsTitle));
        foreach (SuggestedAction action in actions)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {action.Rank}. {action.ActionId} - {text.Resolve(action.RationaleKey)}"));
        }
    }

    /// <summary>The flat sprint list `status` shows: the same projection as
    /// <see cref="WriteSprintTree"/> with no sprint expanded.</summary>
    private static void WriteSprints(
        SurfaceText text,
        TextWriter output,
        IReadOnlyList<SprintStatus> sprints,
        Guid? activeSprintId) =>
        WriteSprintTree(text, output, sprints, activeSprintId, null);

    /// <summary>Same sprint list as <see cref="WriteSprints"/>, but nests the expanded sprint's
    /// attempts under their owning node instead of listing nodes and attempts as separate flat
    /// sections — kept as its own method so `status`'s existing flat output stays unchanged. The
    /// lines themselves come from <see cref="SurfaceFormatting"/>, shared with the Desktop sprint
    /// view.</summary>
    private static void WriteSprintTree(
        SurfaceText text,
        TextWriter output,
        IReadOnlyList<SprintStatus> sprints,
        Guid? activeSprintId,
        SprintDetails? details) =>
        WriteLines(output, SurfaceFormatting.SprintTreeLines(text, sprints, activeSprintId, details));

    private static void WriteSprintDetails(SurfaceText text, TextWriter output, SprintDetails details) =>
        WriteLines(output, SurfaceFormatting.SprintDetailLines(text, details));

    private static void WriteLines(TextWriter output, IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            output.WriteLine(line);
        }
    }

    private static void WriteEvents(SurfaceText text, TextWriter output, ControlEventsPage page)
    {
        foreach (string line in SurfaceFormatting.EventLines(text, page))
        {
            output.WriteLine(line);
        }
    }

    private static void WriteValues(
        TextWriter output,
        IReadOnlyList<EffectiveConfigurationValue> values)
    {
        foreach (EffectiveConfigurationValue value in values)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {value.Key} = {value.Value.GetRawText()} ({SurfaceFormatting.Machine(value.Provenance)})"));
        }
    }

    private static string RecoveryMessage(RecoverStartupResult result) =>
        result switch
        {
            { Succeeded: true, Check: null } => MessageKeys.RecoveryNotNeeded,
            { Succeeded: true } => MessageKeys.RecoveryCompleted,
            _ => MessageKeys.RecoveryFailed,
        };

    private static string InitMessage(InitializeProjectResult result) =>
        result.DiagnosticCode switch
        {
            DiagnosticCodes.ProjectAlreadyInitialized => MessageKeys.InitAlreadyInitialized,
            DiagnosticCodes.ConfirmationRequired => MessageKeys.InitConfirmationRequired,
            DiagnosticCodes.None => MessageKeys.InitCompleted,
            _ => MessageKeys.InitFailed,
        };

}
