using System.Globalization;
using System.Text.Json;
using Forge.Application;
using Forge.Compiler;
using Forge.Infrastructure;
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

    /// <summary>One provider's quota row, shared by every surface that lists quota status (`forge
    /// models quota`) so the `provider-quota-parity` capability can never drift between them --
    /// mirrors <see cref="ProviderRow"/>'s shape and, like it, is never localized (CLI machine
    /// output). See ADR 0052: every row currently projects
    /// <see cref="ProviderQuotaAvailability.Unknown"/> with no remaining amount, unit, or reset
    /// time, since no provider integration in this codebase exposes a verified quota signal.</summary>
    public static string ProviderQuotaRow(ProviderQuotaSnapshot entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string remaining = entry.RemainingAmount is { } amount
            ? string.Create(CultureInfo.InvariantCulture, $"{amount}{entry.Unit ?? string.Empty}")
            : "-";
        string resetAt = entry.ResetAt is { } reset
            ? reset.ToString("O", CultureInfo.InvariantCulture)
            : "-";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.ProviderId} {entry.Model ?? "-"} {Machine(entry.Availability)} {remaining} {resetAt} " +
                $"{entry.DiagnosticCode}");
    }

    /// <summary>The sidebar/status-row aggregate across every provider's quota reading (plan 12.6:
    /// the global status row "distinguishes... quota, unknown quota... and stale data"). Reports the
    /// single most severe state present (<see cref="ProviderQuotaAggregation.Worst"/>) as one
    /// localized sentence plus its accessible counterpart, so a degraded provider's quota is never
    /// communicated by color alone and is never hidden behind an otherwise-unremarkable majority.
    /// <see cref="ProviderQuotaAvailability.Unknown"/> ("no verified signal yet") and
    /// <see cref="ProviderQuotaAvailability.Unavailable"/> ("quota is exhausted") are easy to
    /// conflate by name -- every named member below has its own explicit arm (no arm reached by more
    /// than one member), so this codebase's own single meeting point for the two vocabularies can no
    /// longer hide a mismatch behind a `_` wildcard. C# cannot make an enum switch expression
    /// exhaustive against a genuinely new named member at compile time (the CLR does not treat enums
    /// as closed types), so the remaining `_` arm throws instead of silently falling back to any one
    /// of the arms above -- a future <see cref="ProviderQuotaAvailability"/> member added without a
    /// matching arm here fails loudly at first use, not silently (PR #100 review finding 5).</summary>
    public static (string Text, string Accessible) QuotaStatusSummary(
        SurfaceText text, IReadOnlyList<ProviderQuotaSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(snapshots);
        ProviderQuotaAvailability worst = ProviderQuotaAggregation.Worst(snapshots);
        (string textKey, string accessibleKey) = worst switch
        {
            ProviderQuotaAvailability.Unknown => (MessageKeys.QuotaStatusUnknown, MessageKeys.QuotaStatusUnknownAccessible),
            ProviderQuotaAvailability.Ready => (MessageKeys.QuotaStatusReady, MessageKeys.QuotaStatusReadyAccessible),
            ProviderQuotaAvailability.Limited => (MessageKeys.QuotaStatusLimited, MessageKeys.QuotaStatusLimitedAccessible),
            ProviderQuotaAvailability.Unavailable => (MessageKeys.QuotaStatusDepleted, MessageKeys.QuotaStatusDepletedAccessible),
            ProviderQuotaAvailability.Stale => (MessageKeys.QuotaStatusStale, MessageKeys.QuotaStatusStaleAccessible),
            _ => throw new ArgumentOutOfRangeException(
                nameof(snapshots), worst, "Unmapped ProviderQuotaAvailability value."),
        };
        return (text.Resolve(textKey), text.Resolve(accessibleKey));
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
    /// events` and the Desktop control-events view so the two can never drift. PR #107 review
    /// finding 6: the message key is resolved through <see cref="TimelineMessageFormatter"/> --
    /// the same neutral formatter the sprint timeline uses -- instead of rendered as the raw
    /// `workflow.*`/`routing.*` journal key, so this shared surface is localized too rather than
    /// leaving a second, un-localized rendering path for the same key space.</summary>
    /// <remarks>PR #107 round 2 review finding 1 (security regression): unlike the sprint-timeline
    /// render path (<c>CliApplication.WriteTimeline</c>), which applies <see cref="SecretRedactor"/>
    /// three times (twice while <c>SprintTimelinePage</c> is built, once more over the fully
    /// formatted line), <see cref="ControlEventsReader"/> reads the journal directly and applies no
    /// redaction at all. <see cref="TimelineMessageFormatter.Format"/> substitutes raw, unredacted
    /// journal arguments (a posted message, an agent summary, a supersession instruction, a rewind
    /// reason) into the rendered text, so this method must redact the fully formatted line itself --
    /// the same belt-and-braces pass <c>WriteTimeline</c> already applies -- rather than ship a
    /// second rendering path for the same event data with weaker protection than the first.</remarks>
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
            // Round 2 review finding 2: the free text TimelineMessageFormatter substitutes in (e.g.
            // a posted message) is bounded in length but not in newline content, so an embedded
            // newline would otherwise split one event across multiple physical lines -- collapsed to
            // spaces so every event still renders as exactly one entry in this ordered line list.
            string messageText = SingleLine(
                TimelineMessageFormatter.Format(text, record.Event.MessageKey, record.Event.Arguments));
            string line = string.Create(
                CultureInfo.InvariantCulture,
                $"  {record.SprintId} {record.Event.Type} {Machine(record.Event.Aggregate.Kind)}:" +
                    $"{record.Event.Aggregate.Id} {messageText}");
            lines.Add(SecretRedactor.Redact(line));
        }

        return lines;
    }

    /// <summary>Collapses every line break to a single space so a free-text value can never split
    /// one rendered event across multiple physical lines (PR #107 round 2 review finding 2).</summary>
    private static string SingleLine(string value) =>
        value.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

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
