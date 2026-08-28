using Forge.Application;
using Forge.Desktop.Presentation;
using Forge.Domain;
using Forge.Localization;
using Forge.Providers;

namespace Forge.UnitTests;

/// <summary>
/// ADR 0064: the rules a timeline payload card exists to preserve, tested against the pure projector
/// rather than against a rendered control (ADR 0050 -- no MAUI control can be instantiated headlessly
/// in this suite, which is exactly why the projection is a MAUI-free class in
/// <c>Forge.Desktop.Presentation</c>).
/// </summary>
public sealed class TimelineCardProjectorTests
{
    private static SurfaceText English() => new(new ResourceLocalizationCatalog(), new("en-US"));

    private static SurfaceText Russian() => new(new ResourceLocalizationCatalog(), new("ru-RU"));

    private static TimelineItemView Item(string type, WorkflowEventPayload? payload) =>
        new(
            Guid.NewGuid(),
            7,
            DateTimeOffset.UnixEpoch,
            type,
            "agent",
            "summary",
            new Dictionary<string, string?>(StringComparer.Ordinal),
            null,
            null,
            false,
            "copy",
            payload);

    private static TimelineItemView DiffItem(DiffPayload diff) =>
        Item(WorkflowEvent.AttemptDiffRecordedType, new(diff, null, null));

    private static TimelineItemView ToolUseItem(ToolUsePayload toolUse) =>
        Item(WorkflowEvent.AttemptToolUseRecordedType, new(null, toolUse, null));

    private static TimelineItemView UsageItem(UsagePayload usage) =>
        Item(WorkflowEvent.AttemptUsageRecordedType, new(null, null, usage));

    /// <summary>An item with no structured payload -- the overwhelming majority of timeline items --
    /// produces no card at all, so the renderer's per-row cost stays one null check.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void AnItemWithNoPayloadProducesNoCard()
    {
        Assert.Null(TimelineCardProjector.Build(Item(WorkflowEvent.UserMessagePostedType, null), English()));
        Assert.Null(TimelineCardProjector.Build(Item(WorkflowEvent.UserMessagePostedType, new(null, null, null)), English()));
    }

    /// <summary>ADR 0059's three totals become three chips, and every one is flagged as restating the
    /// item's own localized summary sentence ("Changed {0} file(s): +{1}/-{2} lines.") -- which is
    /// what excludes them from the accessible tree, since the described summary label already speaks
    /// them (ADR 0064).</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ADiffPayloadProducesExactlyThreeSummaryRestatingCountChips()
    {
        TimelineCardContent card = TimelineCardProjector.Build(
            DiffItem(new(3, 120, 8, [], 0)), English())!;

        Assert.Equal(3, card.Stats.Count);
        Assert.All(card.Stats, stat => Assert.True(stat.RestatesSummary));
        Assert.Equal(["3", "+120", "-8"], card.Stats.Select(stat => stat.Value));
    }

    /// <summary>One detail row per recorded file, carrying the LOCALIZED change kind rather than the
    /// raw closed-set code the durable envelope stores -- the same rule
    /// <c>TimelineMessageFormatter.BlockedReasonLabel</c> already applies to its own machine
    /// vocabulary.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void EachChangedFileBecomesADetailRowWithALocalizedChangeKind()
    {
        TimelineCardContent card = TimelineCardProjector.Build(
            DiffItem(new(
                2,
                12,
                3,
                [
                    new("src/App.cs", 10, 3, DiffChangeKinds.Modified),
                    new("src/New.cs", 2, 0, DiffChangeKinds.Added),
                ],
                0)),
            English())!;

        Assert.Equal(2, card.DetailRows.Count);
        Assert.Equal("src/App.cs", card.DetailRows[0].PrimaryText);
        Assert.Equal("+10 -3 modified", card.DetailRows[0].SecondaryText);
        Assert.Equal("+2 -0 added", card.DetailRows[1].SecondaryText);
        Assert.All(
            card.DetailRows,
            row => Assert.DoesNotContain(DiffChangeKinds.Binary, row.SecondaryText!, StringComparison.Ordinal));
    }

    /// <summary>ADR 0059 caps the per-file rows a single journal line may carry and counts the
    /// remainder, so a reader is never shown a short list that looks complete.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ACappedFileListReportsHowManyRowsAreNotShown()
    {
        TimelineCardContent capped = TimelineCardProjector.Build(
            DiffItem(new(60, 500, 100, [new("src/App.cs", 5, 1, DiffChangeKinds.Modified)], 59)), English())!;
        TimelineCardContent uncapped = TimelineCardProjector.Build(
            DiffItem(new(1, 5, 1, [new("src/App.cs", 5, 1, DiffChangeKinds.Modified)], 0)), English())!;

        Assert.NotNull(capped.ElidedText);
        Assert.Contains("59", capped.ElidedText, StringComparison.Ordinal);
        Assert.Null(uncapped.ElidedText);
    }

    /// <summary>ADR 0060: a command never carries a target (the only text identifying which command
    /// is the command line itself, which is never persisted), while an edit does. Neither ever gains a
    /// placeholder standing in for the missing one.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ACommandRowCarriesNoTargetWhileAnEditRowDoes()
    {
        TimelineCardContent card = TimelineCardProjector.Build(
            ToolUseItem(new(
                2,
                1,
                1,
                [
                    new(ProviderToolCallKinds.Command, null, 40, 0, true),
                    new(ProviderToolCallKinds.Edit, "src/App.cs", null, null, null),
                ],
                0,
                0)),
            English())!;

        Assert.Equal("command", card.DetailRows[0].PrimaryText);
        Assert.Equal("edit src/App.cs", card.DetailRows[1].PrimaryText);
    }

    /// <summary>A null duration or exit code is OMITTED from the row, never rendered as "0 ms" or
    /// "exit code 0" -- ADR 0060 leaves each null precisely when nothing was observed, and a call with
    /// nothing observed has no secondary text at all.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void AnUnobservedDurationOrExitCodeIsOmittedRatherThanRenderedAsZero()
    {
        TimelineCardContent card = TimelineCardProjector.Build(
            ToolUseItem(new(
                3,
                2,
                1,
                [
                    new(ProviderToolCallKinds.Edit, "src/App.cs", null, null, null),
                    new(ProviderToolCallKinds.Command, null, 40, null, null),
                    new(ProviderToolCallKinds.Command, null, null, 1, false),
                ],
                0,
                0)),
            English())!;

        Assert.Null(card.DetailRows[0].SecondaryText);
        Assert.Equal("40 ms", card.DetailRows[1].SecondaryText);
        Assert.Equal("exit code 1, failed", card.DetailRows[2].SecondaryText);
        Assert.Equal(TimelineStatTone.Negative, card.DetailRows[2].Tone);
        Assert.Equal(TimelineStatTone.Neutral, card.DetailRows[0].Tone);
    }

    /// <summary>ADR 0060's drift counter gets a chip only when it is non-zero, and that chip is the
    /// one tool-use chip flagged as NOT restating the summary sentence -- because
    /// `workflow.attempt_tool_use_recorded` never mentions drift, so it must stay a real accessible
    /// stop (ADR 0064).</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TheDriftChipAppearsOnlyWhenSomethingWasUnmappedAndIsNotASummaryRestatement()
    {
        TimelineCardContent quiet = TimelineCardProjector.Build(ToolUseItem(new(2, 1, 1, [], 0, 0)), English())!;
        TimelineCardContent drifting = TimelineCardProjector.Build(ToolUseItem(new(2, 1, 1, [], 0, 4)), English())!;

        Assert.Equal(3, quiet.Stats.Count);
        Assert.All(quiet.Stats, stat => Assert.True(stat.RestatesSummary));
        Assert.Equal(4, drifting.Stats.Count);
        TimelineStat drift = drifting.Stats[3];
        Assert.Equal("4", drift.Value);
        Assert.False(drift.RestatesSummary);
        Assert.Equal(TimelineStatTone.Caution, drift.Tone);
    }

    /// <summary>THE assertion this whole card family exists for (ADR 0061 via ADR 0064): a counter the
    /// provider did not report produces NO chip at all -- not a chip reading "0", not an empty one.
    /// The localized summary line has to substitute 0 for an unreported counter, so this card is the
    /// only surface on which "not reported" and "reported as zero" stay different facts.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void AnUnreportedTokenCounterProducesNoChipAtAllRatherThanAZero()
    {
        TimelineCardContent card = TimelineCardProjector.Build(
            UsageItem(new(InputTokens: 6, OutputTokens: null, CacheReadTokens: null, CacheCreationTokens: null,
                ContextWindow: null)),
            English())!;

        TimelineStat only = Assert.Single(card.Stats);
        Assert.Equal("input", only.Label);
        Assert.Equal("6", only.Value);
        Assert.False(only.RestatesSummary);
        Assert.DoesNotContain(card.Stats, stat => stat.Value == "0");
        Assert.Empty(card.DetailRows);
        Assert.Null(card.ElidedText);
    }

    /// <summary>A reported zero is still reported: "0" is a real observation and must not be confused
    /// with the omission the sibling test pins.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void AGenuinelyReportedZeroStillProducesItsOwnChip()
    {
        TimelineCardContent card = TimelineCardProjector.Build(
            UsageItem(new(InputTokens: 0, OutputTokens: null, CacheReadTokens: null, CacheCreationTokens: null,
                ContextWindow: null)),
            English())!;

        Assert.Equal("0", Assert.Single(card.Stats).Value);
    }

    /// <summary>ADR 0061/0064: the context window is a value in its own right, never a denominator.
    /// Nothing in the card's rendered output may read as a computed ratio -- Codex publishes no
    /// context window at all, and which counters would belong over that denominator is a decision no
    /// layer of this codebase has made.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TheContextWindowIsItsOwnChipAndNothingInTheCardReadsAsARatio()
    {
        TimelineCardContent card = TimelineCardProjector.Build(
            UsageItem(new(6, 265, 75_666, 38_581, 200_000)), English())!;

        Assert.Equal(5, card.Stats.Count);
        Assert.Contains(card.Stats, stat => stat.Label == "context window" && stat.Value == "200000");
        Assert.All(card.Stats, stat => Assert.All(
            new[] { stat.Label, stat.Value },
            part =>
            {
                Assert.DoesNotContain('/', part);
                Assert.DoesNotContain('%', part);
            }));
    }

    /// <summary>Plan 12.6 parity: the projector owns every string on a card, so the same payload
    /// renders fully localized in both catalog languages -- no raw closed-set code and no untranslated
    /// English fragment survives into a Russian render.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TheSamePayloadRendersLocalizedInBothCatalogLanguages()
    {
        TimelineItemView item = DiffItem(new(1, 4, 0, [new("src/App.cs", 4, 0, DiffChangeKinds.Renamed)], 2));

        TimelineCardContent english = TimelineCardProjector.Build(item, English())!;
        TimelineCardContent russian = TimelineCardProjector.Build(item, Russian())!;

        Assert.Equal("files", english.Stats[0].Label);
        Assert.Equal("файлов", russian.Stats[0].Label);
        Assert.Equal("+4 -0 renamed", english.DetailRows[0].SecondaryText);
        Assert.Equal("+4 -0 переименован", russian.DetailRows[0].SecondaryText);
        Assert.DoesNotContain(DiffChangeKinds.Renamed, russian.DetailRows[0].SecondaryText!, StringComparison.Ordinal);
        Assert.NotEqual(english.ElidedText, russian.ElidedText);
        // The path itself is data, not prose -- it must survive both renders verbatim.
        Assert.Equal("src/App.cs", russian.DetailRows[0].PrimaryText);
    }

    /// <summary>ADR 0059 records a binary file with zero counts because "how many lines changed" has
    /// no answer for it. Rendering "+0 -0" would be a claim that nothing in it changed, so the row
    /// shows its kind alone.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ABinaryFileShowsItsKindWithoutFabricatedLineCounts()
    {
        TimelineCardContent card = TimelineCardProjector.Build(
            DiffItem(new(1, 0, 0, [new("docs/logo.png", 0, 0, DiffChangeKinds.Binary)], 0)), English())!;

        Assert.Equal("binary", card.DetailRows[0].SecondaryText);
    }
}

/// <summary>
/// ADR 0064's linkage rule: a gate decision may be rendered inline only when the event that requested
/// it is actually on the loaded timeline page, so the bottom action panel stays the honest fallback
/// for every other case.
/// </summary>
public sealed class TimelineGateLinksTests
{
    private static AvailableAction GateAction(string prefix, string nodeId, long? timelineSequence) =>
        new(
            AvailableAction.ContractVersion,
            prefix + nodeId,
            "workspace_action.approve_gate",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new(null, Guid.NewGuid(), nodeId, null, null, timelineSequence),
            9,
            SafetyClass.HumanApproval,
            true,
            [],
            true,
            [],
            Guid.NewGuid(),
            StaleBehavior.RejectWithoutSideEffect);

    private static IReadOnlyList<AvailableAction> GatePair(string nodeId, long? timelineSequence) =>
    [
        GateAction(AvailableActionProjector.ApproveGateActionPrefix, nodeId, timelineSequence),
        GateAction(AvailableActionProjector.RejectGateActionPrefix, nodeId, timelineSequence),
    ];

    private static TimelineItemView ItemAt(long sequence) =>
        new(
            Guid.NewGuid(),
            sequence,
            DateTimeOffset.UnixEpoch,
            // A node transition carries no WorkflowEvent.*Type constant of its own -- the type string
            // is whatever the store appended. Only Sequence matters to this projection.
            "NodeChanged",
            "system",
            "awaiting human",
            new Dictionary<string, string?>(StringComparer.Ordinal),
            null,
            null,
            false,
            "copy",
            null);

    [Fact]
    [Trait("Category", "Unit")]
    public void AGateWhoseRequestingEventIsOnTheLoadedPageProducesALink()
    {
        IReadOnlyList<TimelineGateLink> links =
            TimelineGateLinks.Resolve(GatePair("gate", 12), [ItemAt(11), ItemAt(12)]);

        TimelineGateLink link = Assert.Single(links);
        Assert.Equal(12, link.Sequence);
        Assert.Equal("gate", link.NodeId);
        Assert.Equal(AvailableActionProjector.ApproveGateActionPrefix + "gate", link.Approve.ActionId);
        Assert.Equal(AvailableActionProjector.RejectGateActionPrefix + "gate", link.Reject.ActionId);
    }

    /// <summary>The timeline is paged: a decision requested before the loaded window has no honest
    /// inline anchor, so no link is emitted and `ContextualActionHost` remains the surface that offers
    /// it. This is the specific reason the inline card is additive rather than a replacement.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void AGateWhoseRequestingEventHasScrolledOutOfTheLoadedPageProducesNoLink()
    {
        Assert.Empty(TimelineGateLinks.Resolve(GatePair("gate", 3), [ItemAt(11), ItemAt(12)]));
    }

    /// <summary>ADR 0058 node-scoped the action ids because `SprintScheduler.AdvanceGraphAsync`
    /// promotes every eligible gate at once. Two concurrently pending gates must each anchor to their
    /// own requesting event and carry their own node id -- never cross.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TwoConcurrentGatesEachLinkToTheirOwnEvent()
    {
        IReadOnlyList<AvailableAction> actions = [.. GatePair("review-gate", 40), .. GatePair("release-gate", 55)];

        IReadOnlyList<TimelineGateLink> links =
            TimelineGateLinks.Resolve(actions, [ItemAt(40), ItemAt(50), ItemAt(55)]);

        Assert.Equal(2, links.Count);
        Assert.Equal(("review-gate", 40L), (links[0].NodeId, links[0].Sequence));
        Assert.Equal(("release-gate", 55L), (links[1].NodeId, links[1].Sequence));
        Assert.All(
            links,
            link => Assert.EndsWith(
                link.NodeId, link.Approve.ActionId, StringComparison.Ordinal));
    }

    /// <summary>A half-projected gate (only one of the two rows, or no requesting sequence at all)
    /// produces no inline affordance rather than a card that can only half-answer the decision.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void AnIncompleteOrUnanchoredGateProducesNoLink()
    {
        Assert.Empty(TimelineGateLinks.Resolve(
            [GateAction(AvailableActionProjector.ApproveGateActionPrefix, "gate", 12)], [ItemAt(12)]));
        Assert.Empty(TimelineGateLinks.Resolve(GatePair("gate", null), [ItemAt(12)]));
        Assert.Empty(TimelineGateLinks.Resolve([], [ItemAt(12)]));
    }
}
