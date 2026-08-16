using System.CommandLine;
using System.Globalization;
using Forge.Application;
using Forge.Configuration;
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
        Func<string?, CancellationToken, Task<IForgeMutations>>? resolveMutations = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(application);
        TextWriter diagnostics = error ?? output;
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
        root.Subcommands.Add(CreateInitCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateStatusCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateNextCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateEventsCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateTreeCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateSprintCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateModelsCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateConfigCommand(text, output, diagnostics, application, effectiveResolver));
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
        Command command = new("doctor", text.Resolve(MessageKeys.DoctorDescription));
        command.Options.Add(projectRoot);
        command.Options.Add(startup);
        command.Options.Add(recover);
        command.Options.Add(confirm);
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
            bool sprintRequested = !string.IsNullOrWhiteSpace(rawSprint);
            Guid? sprintId = sprintRequested && Guid.TryParse(rawSprint, out Guid parsedSprintId)
                ? parsedSprintId
                : null;
            SnapshotDetail requestedDetail = string.Equals(
                parseResult.GetValue(detail), "full", StringComparison.OrdinalIgnoreCase)
                ? SnapshotDetail.Full
                : SnapshotDetail.Summary;
            ProjectOverview overview = await application
                .GetOverviewAsync(parseResult.GetValue(projectRoot), requestedDetail, sprintId, cancellationToken)
                .ConfigureAwait(false);
            // A malformed --sprint value never parses to a Guid, and a well-formed but unknown one
            // never resolves a Details section — both must be reported rather than silently treated
            // as "no sprint requested". This leaves every other exit-code case exactly as before
            // (the project startup diagnostic stays informational-only on this read command).
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
        ForgeApplication application)
    {
        Command command = new("sprint", text.Resolve(MessageKeys.SprintDescription));
        command.Subcommands.Add(CreateSprintInspectCommand(text, output, diagnostics, application));
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
            if (overview.Snapshot.Details is not { } details)
            {
                // A null Details section isn't always "no such sprint" — an uninitialized or missing
                // project reports it too. Surface that underlying diagnostic first, matching `status`
                // and `tree`, instead of masking it behind sprint_not_found.
                WriteDiagnostic(diagnostics, overview.Startup.Project.DiagnosticCode);
                return Report(diagnostics, DiagnosticCodes.SprintNotFound);
            }

            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(overview.Snapshot));
                return ExitCodes.Ok;
            }

            WriteSprintDetails(text, output, details);
            WriteDiagnostic(diagnostics, overview.Startup.Project.DiagnosticCode);
            return ExitCodes.Ok;
        });
        return command;
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

    private static void WriteSprints(
        SurfaceText text,
        TextWriter output,
        IReadOnlyList<SprintStatus> sprints,
        Guid? activeSprintId)
    {
        output.WriteLine(text.Resolve(MessageKeys.SprintsTitle));
        if (sprints.Count == 0)
        {
            output.WriteLine(text.Resolve(MessageKeys.NoSprints));
            return;
        }

        foreach (SprintStatus sprint in sprints)
        {
            string marker = sprint.Id == activeSprintId ? "*" : " ";
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {marker} {sprint.CreationSequence}. {sprint.Id} {SurfaceFormatting.Machine(sprint.State)}"));
        }
    }

    /// <summary>Same sprint list as <see cref="WriteSprints"/>, but nests the expanded sprint's
    /// attempts under their owning node instead of listing nodes and attempts as separate flat
    /// sections — kept as its own method so `status`'s existing flat output stays unchanged.</summary>
    private static void WriteSprintTree(
        SurfaceText text,
        TextWriter output,
        IReadOnlyList<SprintStatus> sprints,
        Guid? activeSprintId,
        SprintDetails? details)
    {
        output.WriteLine(text.Resolve(MessageKeys.SprintsTitle));
        if (sprints.Count == 0)
        {
            output.WriteLine(text.Resolve(MessageKeys.NoSprints));
            return;
        }

        foreach (SprintStatus sprint in sprints)
        {
            string marker = sprint.Id == activeSprintId ? "*" : " ";
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {marker} {sprint.CreationSequence}. {sprint.Id} {SurfaceFormatting.Machine(sprint.State)}"));
            if (details is { } sprintDetails && sprintDetails.SprintId == sprint.Id)
            {
                WriteNodeTree(text, output, sprintDetails);
            }
        }
    }

    private static void WriteNodeTree(SurfaceText text, TextWriter output, SprintDetails details)
    {
        foreach (EntityStatus node in details.Nodes)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"      {node.Id} {node.State}"));
            foreach (EntityStatus attempt in details.Attempts.Where(attempt =>
                string.Equals(attempt.OwnerId, node.Id, StringComparison.Ordinal)))
            {
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"        {attempt.Id} {attempt.State}"));
            }
        }

        if (details.Findings.Count > 0)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"      {text.Resolve(MessageKeys.FindingsLabel)}"));
            foreach (EntityStatus finding in details.Findings)
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        {finding.Id} {finding.State}"));
            }
        }
    }

    private static void WriteSprintDetails(SurfaceText text, TextWriter output, SprintDetails details)
    {
        output.WriteLine(text.Resolve(MessageKeys.SprintDetailsTitle));
        WriteEntities(text, output, MessageKeys.NodesLabel, details.Nodes);
        WriteEntities(text, output, MessageKeys.AttemptsLabel, details.Attempts);
        WriteEntities(text, output, MessageKeys.FindingsLabel, details.Findings);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {text.Resolve(MessageKeys.RoutingLabel)} retry_remaining={details.Routing.RetryRemaining}"));
    }

    private static void WriteEntities(
        SurfaceText text,
        TextWriter output,
        string titleKey,
        IReadOnlyList<EntityStatus> entities)
    {
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {text.Resolve(titleKey)}"));
        foreach (EntityStatus entity in entities)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {entity.Id} {entity.State}"));
        }
    }

    private static void WriteEvents(SurfaceText text, TextWriter output, ControlEventsPage page)
    {
        output.WriteLine(text.Resolve(MessageKeys.EventsTitle));
        if (page.Events.Count == 0)
        {
            output.WriteLine(text.Resolve(MessageKeys.NoEvents));
            return;
        }

        foreach (ControlEventRecord record in page.Events)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {record.SprintId} {record.Event.Type} {SurfaceFormatting.Machine(record.Event.Aggregate.Kind)}:{record.Event.Aggregate.Id} {record.Event.MessageKey}"));
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
