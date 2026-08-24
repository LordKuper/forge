using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>Plan section 6.3's versioned, cursor-paged timeline projection over the existing
/// append-only workflow journal. Confirms real incremental-loading and redaction behavior before the
/// smallest risk-based tests were added.</summary>
public sealed class SprintTimelineTests
{
    private static readonly IReadOnlyList<NodeDefinition> OneNodeGraph = [new("a", NodeKind.Work, [])];

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheFirstPageReportsTheSprintsOwnCreationAsASystemItem()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);

        Assert.Equal(DiagnosticCodes.None, page.DiagnosticCode);
        Assert.Equal(sprintId.Value, page.SprintId);
        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, item => Assert.Equal(TimelineActor.System, item.Actor));
        Assert.Contains(page.Items, item => item.TargetKind == "sprint");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnUnknownSprintIdReportsSprintNotFoundWithAnEmptyPage()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);

        SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, Guid.NewGuid(), null, cancellationToken);

        Assert.Equal(DiagnosticCodes.SprintNotFound, page.DiagnosticCode);
        Assert.Empty(page.Items);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RepeatingTheSameCursorNeverRedeliversAnAlreadySeenItemAndANewEventArrivesExactlyOnce()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        SprintTimelinePage firstPage = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);
        Assert.NotEmpty(firstPage.Items);

        SprintTimelinePage caughtUp = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, firstPage.Cursor, cancellationToken);
        Assert.Empty(caughtUp.Items);

        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        SprintTimelinePage nextPage = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, firstPage.Cursor, cancellationToken);

        Assert.NotEmpty(nextPage.Items);
        Assert.Empty(nextPage.Items.Select(item => item.Id).Intersect(firstPage.Items.Select(item => item.Id)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARawCredentialInAnOperatorInstructionNeverAppearsInAProjectedTimelineItem()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        const string secret = "password=Sup3rSecretValue!!";
        await store.AppendAttemptSupersededAsync(
            environment.ProjectRoot, sprintId, started.AttemptId!, $"Retry, credential was {secret}",
            cancellationToken);

        SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);

        SprintTimelineItem supersession =
            Assert.Single(page.Items, item => item.Type == WorkflowEvent.AttemptSupersededType);
        Assert.Equal(TimelineActor.Operator, supersession.Actor);
        Assert.All(
            supersession.Arguments.Values,
            value => Assert.DoesNotContain(secret, value ?? string.Empty, StringComparison.Ordinal));
        string serialized = StatusJson.Serialize(page);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
    }

    // Regression (PR #97 review, finding 6): a cursor's Watermark is a per-sprint, independent,
    // dense counter -- reusing sprint A's cursor to page sprint B must never silently apply A's
    // watermark to B's own stream (which could skip B's early items unnoticed). The codec's own
    // documented contract already treats a malformed or future-versioned token as "foreign,
    // decodes to Empty, never a silent rebaseline"; a cursor whose bound sprint id does not match
    // the sprint being paged is exactly as foreign and now gets the same rejection.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACursorIssuedForOneSprintIsRejectedRatherThanSilentlyAppliedToAnotherSprint()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintA = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        SprintId sprintB = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        SprintTimelinePage pageA = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintA.Value, null, cancellationToken);
        Assert.NotEmpty(pageA.Items);

        SprintTimelinePage crossSprintPage = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintB.Value, pageA.Cursor, cancellationToken);

        Assert.Equal(DiagnosticCodes.ControlCursorStale, crossSprintPage.DiagnosticCode);
        Assert.Empty(crossSprintPage.Items);

        // Recoverable, not wedged: retrying sprint B fresh (no cursor) still reports every one of
        // its own items -- nothing was silently skipped by the rejected foreign watermark.
        SprintTimelinePage freshPageB = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintB.Value, null, cancellationToken);
        Assert.Equal(DiagnosticCodes.None, freshPageB.DiagnosticCode);
        Assert.NotEmpty(freshPageB.Items);
    }

    // Regression (PR #97 review, finding 2): the original test above only proves *some* pass
    // redacted the planted secret -- pass 1 (SprintTimelineProjector.ToItem) already redacts every
    // WorkflowEvent.Arguments value on its own, so that test passes even with pass 2 deleted
    // entirely and proves nothing about it. This test bypasses pass 1's actual coverage instead of
    // its output: TamperingSprintStore injects a raw secret into WorkflowEvent.MessageKey, a field
    // pass 1 copies straight through with no redaction at all (only Arguments is ever redacted by
    // pass 1). The raw page below (pass 1 only, taken directly from a projector built on the same
    // tampering store) is asserted to still contain the secret -- proving pass 1 alone cannot catch
    // it -- and only SprintTimelineRedaction.Apply (pass 2) removes it before the page is
    // serialized.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RedactionPass2CatchesASecretInAFieldPass1NeverTouches()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        const string secret = "password=Sup3rSecretValue!!";
        TamperingSprintStore tamperingStore = new(environment.Resolve<ISprintStore>(), secret);
        SprintTimelineProjector projector = new(tamperingStore);

        SprintTimelinePage rawPage = await projector.CreateAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);
        Assert.Contains(rawPage.Items, item => item.MessageKey.Contains(secret, StringComparison.Ordinal));

        SprintTimelinePage renderedPage = SprintTimelineRedaction.Apply(rawPage);

        Assert.DoesNotContain(renderedPage.Items, item => item.MessageKey.Contains(secret, StringComparison.Ordinal));
        string serialized = StatusJson.Serialize(renderedPage);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
    }

    // Regression (PR #97 review round 2, finding 1): the test above proves the Apply *method*
    // redacts MessageKey, but calls it directly on a locally-built page -- it never exercises
    // ForgeApplication.GetSprintTimelineAsync, so it cannot observe whether that method still calls
    // Apply at all. That is precisely the defect this fix (db485d7) closed: the call was missing
    // from the shared read path every surface (CLI text, CLI --json, Host wire response) actually
    // uses. This test instead resolves a real ForgeApplication (via
    // TestEnvironment.ResolveApplicationWithSprintStore, sharing every other real dependency from
    // the container) whose SprintTimelineProjector is backed by the same TamperingSprintStore, and
    // asserts the secret is gone from GetSprintTimelineAsync's own returned page -- and from its
    // StatusJson.Serialize form, covering the --json/Host wire surfaces too. Verified by targeted
    // mutation: changing ForgeApplication.cs's `return SprintTimelineRedaction.Apply(page);` (line
    // ~1385) to `return page;` fails this test (the secret survives), while the test above and every
    // other existing test stay green -- confirming this is the only test that actually pins pass 2
    // being wired into the shared read path, not merely that the Apply method itself redacts.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSprintTimelineAsyncRedactsASecretInAFieldPass1NeverTouches()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        const string secret = "password=Sup3rSecretValue!!";
        ForgeApplication application =
            environment.ResolveApplicationWithSprintStore(store => new TamperingSprintStore(store, secret));

        SprintTimelinePage page = await application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);

        Assert.DoesNotContain(page.Items, item => item.MessageKey.Contains(secret, StringComparison.Ordinal));
        string serialized = StatusJson.Serialize(page);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
    }

    // ADR 0054, post-release timeline gap closure: user messages and agent summaries.

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PostingAUserMessageAppendsItToTheTimelineAsAnOperatorItemWithADenseSequence()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        PostSprintMessageResult posted = await environment.Application.PostSprintMessageAsync(
            environment.ProjectRoot, sprintId.Value, "please hold off on merging", cancellationToken);
        Assert.True(posted.Succeeded);

        SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);

        SprintTimelineItem item =
            Assert.Single(page.Items, candidate => candidate.Type == WorkflowEvent.UserMessagePostedType);
        Assert.Equal(TimelineActor.Operator, item.Actor);
        Assert.Equal("please hold off on merging", item.Arguments[WorkflowEvent.UserMessageTextArgument]);
        // A real, dense per-sprint sequence -- not a sentinel -- so it pages exactly like every
        // other item (PR #99 review finding 4's own "consumers... must use Sequence" discipline).
        Assert.True(item.Sequence >= 0);
        Assert.All(page.Items, other => Assert.True(other.Sequence <= item.Sequence || other == item));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARawCredentialInAPostedUserMessageNeverAppearsInAProjectedTimelineItem()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        const string secret = "password=Sup3rSecretValue!!";

        await environment.Application.PostSprintMessageAsync(
            environment.ProjectRoot, sprintId.Value, $"please retry, {secret}", cancellationToken);

        SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);

        SprintTimelineItem posted =
            Assert.Single(page.Items, item => item.Type == WorkflowEvent.UserMessagePostedType);
        Assert.All(
            posted.Arguments.Values,
            value => Assert.DoesNotContain(secret, value ?? string.Empty, StringComparison.Ordinal));
        string serialized = StatusJson.Serialize(page);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PostingAMessageOverTheLengthBoundIsRejectedWithoutAppendingAnything()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        PostSprintMessageResult result = await environment.Application.PostSprintMessageAsync(
            environment.ProjectRoot, sprintId.Value,
            new string('x', SprintScheduler.MaxUserMessageLength + 1), cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.UserMessageTooLong, result.DiagnosticCode);
        SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);
        Assert.DoesNotContain(page.Items, item => item.Type == WorkflowEvent.UserMessagePostedType);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PostingAWhitespaceOnlyMessageIsRejectedAsRequired()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        PostSprintMessageResult result = await environment.Application.PostSprintMessageAsync(
            environment.ProjectRoot, sprintId.Value, "   ", cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.UserMessageRequired, result.DiagnosticCode);
    }

    /// <summary>The store-level idempotency anchor (ADR 0054: dedup by the caller-supplied
    /// <c>WorkflowEvent.EventId</c>, not by sprint version) -- a retried append with the same message
    /// id is a safe no-op, never a duplicate timeline item.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RepostingTheSameMessageIdIsASafeNoOpThatNeverDuplicatesTheItem()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        Guid messageId = Guid.NewGuid();

        await scheduler.PostUserMessageAsync(environment.ProjectRoot, sprintId, messageId, "hello", cancellationToken);
        await scheduler.PostUserMessageAsync(environment.ProjectRoot, sprintId, messageId, "hello", cancellationToken);

        SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);
        Assert.Single(page.Items, item => item.Type == WorkflowEvent.UserMessagePostedType);
    }

    /// <summary>A page spanning both system events and user messages never skips or duplicates an
    /// item, mirroring <see cref="RepeatingTheSameCursorNeverRedeliversAnAlreadySeenItemAndANewEventArrivesExactlyOnce"/>
    /// but across every kind of item this timeline now projects.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task PagingAcrossInterleavedSystemEventsAndUserMessagesNeverSkipsOrDuplicatesAnItem()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        await environment.Application.PostSprintMessageAsync(
            environment.ProjectRoot, sprintId.Value, "message before running", cancellationToken);
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await environment.Application.PostSprintMessageAsync(
            environment.ProjectRoot, sprintId.Value, "message after ready", cancellationToken);
        await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        await environment.Application.PostSprintMessageAsync(
            environment.ProjectRoot, sprintId.Value, "message after start", cancellationToken);

        SprintTimelinePage full = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);
        Assert.Equal(3, full.Items.Count(item => item.Type == WorkflowEvent.UserMessagePostedType));

        // Page through one item at a time -- the smallest possible page -- and confirm the union of
        // every page is exactly the full set, with no id repeated and every sequence in the same
        // (non-decreasing) order the single-fetch page above reported.
        List<SprintTimelineItem> paged = [];
        string? cursor = null;
        do
        {
            SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
                environment.ProjectRoot, sprintId.Value, cursor, cancellationToken);
            paged.AddRange(page.Items);
            cursor = page.Cursor;
        }
        while (paged.Count < full.Items.Count);

        Assert.Equal(full.Items.Select(item => item.Id), paged.Select(item => item.Id));
        Assert.Equal(paged.Count, paged.Select(item => item.Id).Distinct().Count());
        Assert.Equal(full.Items.Select(item => item.Sequence), paged.Select(item => item.Sequence));
    }

    /// <summary>Investigation confirmed user-visible agent-summary content already exists
    /// (<see cref="Handoff.Summary"/>) -- ADR 0054 projects it rather than adding a new artifact
    /// type. <see cref="TimelineActor.Agent"/> is neither <see cref="TimelineActor.System"/> nor
    /// <see cref="TimelineActor.Operator"/>.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AHandoffSummaryIsProjectedAsAnAgentTimelineItemNamingItsProducingNode()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);

        RecordHandoffResult handoff = await scheduler.RecordHandoffAsync(
            environment.ProjectRoot, sprintId, "a", new string('a', 40), "implemented the widget",
            decisions: [], openRisks: [], nextNodeIds: null, cancellationToken);
        Assert.True(handoff.Succeeded);

        SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);

        SprintTimelineItem summary = Assert.Single(
            page.Items, item => item.Type == SprintTimelineProjector.AgentSummaryRecordedType);
        Assert.Equal(TimelineActor.Agent, summary.Actor);
        Assert.Equal("node", summary.TargetKind);
        Assert.Equal("a", summary.TargetId);
        Assert.Equal("implemented the widget", summary.Arguments["summary"]);
        // Ordered into the same dense sequence space as every system event -- never trailing behind
        // or duplicated relative to the node's own completion.
        Assert.Contains(page.Items, item => item.Sequence == summary.Sequence && item != summary);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARawCredentialInAnAgentSummaryNeverAppearsInAProjectedTimelineItem()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        const string secret = "password=Sup3rSecretValue!!";

        await scheduler.RecordHandoffAsync(
            environment.ProjectRoot, sprintId, "a", new string('a', 40), $"done, {secret}",
            decisions: [], openRisks: [], nextNodeIds: null, cancellationToken);

        SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);

        SprintTimelineItem summary = Assert.Single(
            page.Items, item => item.Type == SprintTimelineProjector.AgentSummaryRecordedType);
        Assert.All(
            summary.Arguments.Values,
            value => Assert.DoesNotContain(secret, value ?? string.Empty, StringComparison.Ordinal));
        string serialized = StatusJson.Serialize(page);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
    }

    /// <summary>A superseded handoff's summary is stale and must never appear -- the same exclusion
    /// <c>SprintScheduler.IsTestWorkEligibleAsync</c> already applies to a superseded artifact.
    /// Supersedes it directly through the store (no rewind saga needed to prove the projector's own
    /// filter) exactly like <c>MarkHandoffSupersededAsync</c>'s existing callers do.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ASupersededHandoffSummaryIsNeverProjected()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        RecordHandoffResult handoff = await scheduler.RecordHandoffAsync(
            environment.ProjectRoot, sprintId, "a", new string('a', 40), "implemented the widget",
            decisions: [], openRisks: [], nextNodeIds: null, cancellationToken);

        await store.MarkHandoffSupersededAsync(
            environment.ProjectRoot, sprintId, handoff.Handoff!.HandoffId, new(new(1), DateTimeOffset.UtcNow),
            cancellationToken);

        SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);
        Assert.DoesNotContain(page.Items, item => item.Type == SprintTimelineProjector.AgentSummaryRecordedType);
    }

    private static async Task RunToRunningAsync(
        SprintOrchestrator orchestrator,
        string root,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(root, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(root, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(root, sprintId, toReady.Sprint!.Version, SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
    }
}
