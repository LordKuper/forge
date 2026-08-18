using System.Globalization;
using System.Text.Json;
using Forge.Application;
using Forge.Compiler;
using Forge.Providers;

namespace Forge.Localization;

/// <summary>Formatting shared by every surface (CLI, Desktop) that renders durable state as localized/machine text.</summary>
public static class SurfaceFormatting
{
    public static string StartupMessageKey(StartupState state) => state switch
    {
        StartupState.Ready => MessageKeys.StartupReady,
        StartupState.Blocked => MessageKeys.StartupBlocked,
        _ => MessageKeys.StartupFailed,
    };

    /// <summary>Renders an enum as the culture-invariant snake_case token every machine contract uses.</summary>
    public static string Machine<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()!);

    /// <summary>Same as <see cref="Machine{TEnum}(TEnum)"/>, but renders <see langword="null"/>
    /// (e.g. a disabled provider's never-probed <c>state</c>) as <c>"-"</c> instead of throwing.</summary>
    public static string Machine<TEnum>(TEnum? value)
        where TEnum : struct, Enum =>
        value is { } resolved ? Machine(resolved) : "-";

    /// <summary>One provider's row, shared by every surface that lists provider health (`forge
    /// models`, Desktop) so the `provider-health-parity` capability can never drift between them —
    /// distinguishes every state ADR 0008 requires: id, enabled/disabled, install state, version,
    /// update availability, authentication, and diagnostic code.</summary>
    public static string ProviderRow(ProviderHealthEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string updateAvailable = entry.UpdateAvailable switch
        {
            true => "update_available",
            false => "current",
            null => "-",
        };
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.Id} {(entry.Enabled ? "enabled" : "disabled")} {Machine(entry.State)} " +
                $"{entry.Version ?? "-"} {updateAvailable} {Machine(entry.Authentication)} {entry.DiagnosticCode}");
    }

    /// <summary>One provider's row for `forge integration skill generate`'s preview: provider id,
    /// target path, and whether installing would write/no-op/refuse. Private -- reached only
    /// through <see cref="IntegrationInspectionLines"/>, the shared, tested no-drift surface (ADR
    /// 0026); nothing outside this file calls the per-row formatter directly.</summary>
    private static string IntegrationInspectionRow(IntegrationArtifactInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{inspection.Artifact.ProviderId.Value} {inspection.Artifact.RelativePath} {Machine(inspection.State)}");
    }

    /// <summary>One provider's row for `forge integration skill install|remove`'s result. Private
    /// for the same reason as <see cref="IntegrationInspectionRow"/> -- reached only through
    /// <see cref="IntegrationWriteLines"/>.</summary>
    private static string IntegrationWriteRow(IntegrationArtifactResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{result.ProviderId.Value} {result.RelativePath} {Machine(result.Outcome)}");
    }

    /// <summary>`forge integration skill generate`'s full projection as one ordered line list,
    /// shared with the Desktop control (ADR 0026) so the two can never drift.</summary>
    public static IReadOnlyList<string> IntegrationInspectionLines(SurfaceText text, IntegrationInspectionResult result)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(result);
        List<string> lines = [text.Resolve(MessageKeys.IntegrationTitle)];
        if (result.Artifacts.Count == 0 && result.DiagnosticCode == DiagnosticCodes.None)
        {
            lines.Add(text.Resolve(MessageKeys.NoIntegrationArtifacts));
        }

        foreach (IntegrationArtifactInspection inspection in result.Artifacts)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {IntegrationInspectionRow(inspection)}"));
        }

        AppendIntegrationDocumentErrors(lines, result.DocumentErrors);
        return lines;
    }

    /// <summary>`forge integration skill install|remove`'s full projection as one ordered line
    /// list, shared with the Desktop control (ADR 0026) so the two can never drift.</summary>
    public static IReadOnlyList<string> IntegrationWriteLines(SurfaceText text, IntegrationWriteResult result)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(result);
        List<string> lines = [text.Resolve(MessageKeys.IntegrationTitle)];
        foreach (IntegrationArtifactResult artifact in result.Artifacts)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {IntegrationWriteRow(artifact)}"));
        }

        AppendIntegrationDocumentErrors(lines, result.DocumentErrors);
        return lines;
    }

    /// <summary>Surfaces `.forge/rules`/`knowledge` parse failures (ADR 0009) alongside the
    /// artifact rows, in every output mode — a document error silently degrades what generation
    /// compiled, and dropping it would leave a user with no indication why some content was
    /// missing.</summary>
    private static void AppendIntegrationDocumentErrors(List<string> lines, IReadOnlyList<ForgeDocumentError> errors)
    {
        foreach (ForgeDocumentError error in errors)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture, $"  ! {error.RelativePath} {error.DiagnosticCode}"));
        }
    }

    /// <summary>`forge sprint create`'s success line, shared with the Desktop control (ADR 0027) so
    /// the two can never drift. <see langword="null"/> when the call did not succeed -- matching the
    /// CLI's own behavior of writing nothing but the diagnostic in that case.</summary>
    public static string? SprintCreatedMessage(SurfaceText text, CreateSprintResult result)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(result);
        return result is { Succeeded: true, SprintId: { } sprintId }
            ? string.Create(CultureInfo.InvariantCulture, $"{text.Resolve(MessageKeys.SprintCreated)} {sprintId.Value:D}")
            : null;
    }

    /// <summary>`forge sprint run|resume`'s success line, shared with the Desktop control (ADR
    /// 0027). <paramref name="includeResultingState"/> distinguishes `run` (the sprint's own
    /// `AdvanceGraphAsync` side effect can promote further than the one legal hop `run` itself
    /// performs, so the message reports whatever state the sprint actually settled at) from
    /// `resume` (always targets exactly one known state, so fixed text is enough).
    /// <see langword="null"/> when the call did not succeed.</summary>
    public static string? SprintTransitionMessage(
        SurfaceText text, SprintTransitionResult result, string successKey, bool includeResultingState)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            return null;
        }

        return includeResultingState
            ? result.Sprint is not null
                ? string.Create(CultureInfo.InvariantCulture, $"{text.Resolve(successKey)} {Machine(result.Sprint.State)}")
                // successKey (e.g. SprintAdvanced) is a sentence PREFIX for this branch, not a
                // complete sentence on its own -- falling back to it alone would print a dangling
                // fragment ("Sprint advanced to" with nothing after).
                : text.Resolve(MessageKeys.SprintAdvancedUnknownState)
            : text.Resolve(successKey);
    }

    /// <summary>ADR 0005's `project -> sprint -> node -> attempt` hierarchy as one ordered line
    /// list, shared by `forge tree` and the Desktop sprint view so the two projections of the same
    /// snapshot can never drift. <paramref name="details"/> expands exactly the sprint it names;
    /// every other sprint stays a single summary row.</summary>
    public static IReadOnlyList<string> SprintTreeLines(
        SurfaceText text,
        IReadOnlyList<SprintStatus> sprints,
        Guid? activeSprintId,
        SprintDetails? details)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sprints);
        List<string> lines = [text.Resolve(MessageKeys.SprintsTitle)];
        if (sprints.Count == 0)
        {
            lines.Add(text.Resolve(MessageKeys.NoSprints));
            return lines;
        }

        foreach (SprintStatus sprint in sprints)
        {
            string marker = sprint.Id == activeSprintId ? "*" : " ";
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  {marker} {sprint.CreationSequence}. {sprint.Id} {Machine(sprint.State)}"));
            if (details is { } sprintDetails && sprintDetails.SprintId == sprint.Id)
            {
                AppendNodeTree(text, lines, sprintDetails);
            }
        }

        return lines;
    }

    /// <summary>One sprint's flat node/attempt/finding/routing sections, shared by `forge sprint
    /// inspect`, `forge status --detail full`, and the Desktop sprint view.</summary>
    public static IReadOnlyList<string> SprintDetailLines(SurfaceText text, SprintDetails details)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(details);
        List<string> lines = [text.Resolve(MessageKeys.SprintDetailsTitle)];
        AppendEntities(text, lines, MessageKeys.NodesLabel, details.Nodes);
        AppendEntities(text, lines, MessageKeys.AttemptsLabel, details.Attempts);
        AppendEntities(text, lines, MessageKeys.FindingsLabel, details.Findings);
        lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"  {text.Resolve(MessageKeys.RoutingLabel)} retry_remaining={details.Routing.RetryRemaining}"));
        return lines;
    }

    /// <summary>ADR 0005's bounded event-log projection as one ordered line list, shared by `forge
    /// events` and the Desktop control-events view so the two can never drift.</summary>
    public static IReadOnlyList<string> EventLines(SurfaceText text, ControlEventsPage page)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(page);
        List<string> lines = [text.Resolve(MessageKeys.EventsTitle)];
        if (page.Events.Count == 0)
        {
            lines.Add(text.Resolve(MessageKeys.NoEvents));
            return lines;
        }

        foreach (ControlEventRecord record in page.Events)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  {record.SprintId} {record.Event.Type} {Machine(record.Event.Aggregate.Kind)}:" +
                    $"{record.Event.Aggregate.Id} {record.Event.MessageKey}"));
        }

        return lines;
    }

    private static void AppendNodeTree(SurfaceText text, List<string> lines, SprintDetails details)
    {
        foreach (EntityStatus node in details.Nodes)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"      {node.Id} {node.State}"));
            foreach (EntityStatus attempt in details.Attempts.Where(attempt =>
                string.Equals(attempt.OwnerId, node.Id, StringComparison.Ordinal)))
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"        {attempt.Id} {attempt.State}"));
            }
        }

        if (details.Findings.Count > 0)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"      {text.Resolve(MessageKeys.FindingsLabel)}"));
            foreach (EntityStatus finding in details.Findings)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"        {finding.Id} {finding.State}"));
            }
        }
    }

    private static void AppendEntities(
        SurfaceText text,
        List<string> lines,
        string titleKey,
        IReadOnlyList<EntityStatus> entities)
    {
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {text.Resolve(titleKey)}"));
        foreach (EntityStatus entity in entities)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"    {entity.Id} {entity.State}"));
        }
    }
}
