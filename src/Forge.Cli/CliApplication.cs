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
        Func<CancellationToken, ValueTask<UpdateResult>>? update = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(application);
        TextWriter diagnostics = error ?? output;

        RootCommand root = new(text.Resolve(MessageKeys.AppDescription));
        root.Subcommands.Add(CreateDoctorCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateInitCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateStatusCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateNextCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateEventsCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateModelsCommand(text, output, diagnostics, application));
        root.Subcommands.Add(CreateConfigCommand(text, output, diagnostics, application));
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
        ForgeApplication application)
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
                RecoverStartupResult recovered = await application
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
            SnapshotDetail requestedDetail = string.Equals(
                parseResult.GetValue(detail), "full", StringComparison.OrdinalIgnoreCase)
                ? SnapshotDetail.Full
                : SnapshotDetail.Summary;
            Guid? sprintId = Guid.TryParse(parseResult.GetValue(sprint), out Guid parsedSprintId)
                ? parsedSprintId
                : null;
            ProjectOverview overview = await application
                .GetOverviewAsync(parseResult.GetValue(projectRoot), requestedDetail, sprintId, cancellationToken)
                .ConfigureAwait(false);
            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(overview.Snapshot));
                return ExitCodes.Ok;
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
            return ExitCodes.Ok;
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
            Description = "Install or update any provider that is not ready.",
        };
        Command command = new("models", text.Resolve(MessageKeys.ModelsDescription));
        command.Options.Add(json);
        command.Options.Add(refresh);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            ProviderToolchainStatus status = parseResult.GetValue(refresh)
                ? await application.RefreshProviderHealthAsync(cancellationToken).ConfigureAwait(false)
                : await application.GetProviderHealthAsync(cancellationToken).ConfigureAwait(false);
            string diagnosticCode = status.SharedDiagnosticCode;
            if (parseResult.GetValue(json))
            {
                output.WriteLine(StatusJson.Serialize(status));
                return Report(diagnostics, diagnosticCode);
            }

            output.WriteLine(text.Resolve(MessageKeys.ProviderToolchainTitle));
            foreach (ProviderStatus provider in status.Providers)
            {
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {SurfaceFormatting.Machine(provider.Kind)} {SurfaceFormatting.Machine(provider.State)} {provider.Version ?? "-"}"));
            }

            return Report(diagnostics, diagnosticCode);
        });
        return command;
    }

    private static Command CreateConfigCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application)
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
        command.Subcommands.Add(CreateConfigSetCommand(
            text,
            output,
            diagnostics,
            application,
            ConfigurationScope.User));
        command.Subcommands.Add(CreateConfigSetCommand(
            text,
            output,
            diagnostics,
            application,
            ConfigurationScope.Project));
        return command;
    }

    private static Command CreateConfigSetCommand(
        SurfaceText text,
        TextWriter output,
        TextWriter diagnostics,
        ForgeApplication application,
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
            ConfigurationWriteResult result = await application
                .SetConfigurationAsync(
                    scope,
                    parseResult.GetValue(projectRoot),
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
