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

    /// <summary>ADR 0059: the structured payload is a brand-new string-bearing field on
    /// <see cref="SprintTimelineItem"/>, and <see cref="Forge.Infrastructure.SecretRedactor.RedactProperties"/>
    /// -- which is all pass 1 applied before this -- only ever walks
    /// <see cref="SprintTimelineItem.Arguments"/>. A payload that bypassed redaction is exactly the
    /// class of gap the two review rounds above already caught once for
    /// <see cref="SprintTimelineItem.MessageKey"/>, so it is pinned here before it can recur: a
    /// credential-shaped file path inside the payload must be gone from BOTH the projector's own
    /// pass-1 output and the shared <see cref="ForgeApplication.GetSprintTimelineAsync"/> read path
    /// every surface uses, including that page's serialized `--json`/wire form.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ADiffPayloadsFilePathIsRedactedByBothPassesAndNeverReachesASerializedPage()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        Assert.True(started.Succeeded, started.DiagnosticCode);

        const string secret = "password=Sup3rSecretValue";
        ISprintStore store = environment.Resolve<ISprintStore>();
        await store.AppendAttemptDiffRecordedAsync(
            environment.ProjectRoot,
            sprintId,
            started.AttemptId!,
            new DiffPayload(1, 1, 0, [new DiffFileStat($"config/{secret}.env", 1, 0, DiffChangeKinds.Added)], 0),
            cancellationToken);

        // Pass 1, on its own: a projector built directly on the real store, with no Apply call.
        SprintTimelinePage pass1Only = await new SprintTimelineProjector(store)
            .CreateAsync(environment.ProjectRoot, sprintId.Value, null, cancellationToken);
        Assert.Contains(pass1Only.Items, item => item.Payload?.Diff is not null);
        Assert.DoesNotContain(secret, StatusJson.Serialize(pass1Only), StringComparison.Ordinal);

        // The shared read path every surface actually calls (pass 1 then pass 2).
        SprintTimelinePage page = await environment.Application.GetSprintTimelineAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);
        SprintTimelineItem recorded = Assert.Single(
            page.Items, item => item.Type == WorkflowEvent.AttemptDiffRecordedType);
        DiffFileStat file = Assert.Single(recorded.Payload!.Diff!.Files);
        Assert.DoesNotContain(secret, file.Path, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:credential]", file.Path, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, StatusJson.Serialize(page), StringComparison.Ordinal);
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
        Assert.True(full.Items.Count > 3, "the scenario needs at least one system item alongside the messages");

        // Round of PR #104 review, finding 4: at the projector's real, production 500-item bound this
        // ~7-item fixture never actually pages -- the do/while below would exit after one fetch and
        // every assertion would be tautological. A directly-constructed projector with a page size of
        // 1 (the smallest possible page, mirroring this test's own original intent) forces a genuine
        // multi-fetch walk over the exact same durable data, so the union-of-pages assertions below
        // actually exercise MergeAndPage's page-boundary behavior.
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintTimelineProjector pagedProjector = new(store, maxItemsPerPage: 1);
        List<SprintTimelineItem> paged = [];
        string? cursor = null;
        int fetches = 0;
        do
        {
            SprintTimelinePage page = await pagedProjector.CreateAsync(
                environment.ProjectRoot, sprintId.Value, cursor, cancellationToken);
            Assert.True(page.Items.Count <= 1);
            paged.AddRange(page.Items);
            cursor = page.Cursor;
            fetches++;
        }
        while (paged.Count < full.Items.Count && fetches <= full.Items.Count);

        Assert.Equal(full.Items.Count, fetches);
        Assert.Equal(full.Items.Select(item => item.Id), paged.Select(item => item.Id));
        Assert.Equal(paged.Count, paged.Select(item => item.Id).Distinct().Count());
        Assert.Equal(full.Items.Select(item => item.Sequence), paged.Select(item => item.Sequence));
    }

    /// <summary>Round of PR #104 review, finding 4: the paging test above proves ordinary system
    /// events and user messages page correctly, but neither it nor
    /// <see cref="AHandoffSummaryIsProjectedAsAnAgentTimelineItemNamingItsProducingNode"/> ever forced
    /// an agent-summary item to actually sit at a real page boundary. This scenario interleaves a
    /// recorded handoff between two posted messages and pages one item at a time, so the summary lands
    /// mid-sequence and must survive a page boundary landing immediately before or after it without
    /// being skipped or duplicated.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task PagingNeverSkipsOrDuplicatesAnAgentSummaryAtAPageBoundary()
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
        await environment.Application.PostSprintMessageAsync(
            environment.ProjectRoot, sprintId.Value, "before the summary", cancellationToken);
        RecordHandoffResult handoff = await scheduler.RecordHandoffAsync(
            environment.ProjectRoot, sprintId, "a", new string('a', 40), "implemented the widget",
            decisions: [], openRisks: [], nextNodeIds: null, cancellationToken);
        Assert.True(handoff.Succeeded);
        await environment.Application.PostSprintMessageAsync(
            environment.ProjectRoot, sprintId.Value, "after the summary", cancellationToken);

        SprintTimelineProjector fullProjector = new(store);
        SprintTimelinePage full = await fullProjector.CreateAsync(
            environment.ProjectRoot, sprintId.Value, null, cancellationToken);
        Assert.Contains(full.Items, item => item.Type == SprintTimelineProjector.AgentSummaryRecordedType);

        SprintTimelineProjector pagedProjector = new(store, maxItemsPerPage: 1);
        List<SprintTimelineItem> paged = [];
        string? cursor = null;
        int fetches = 0;
        do
        {
            SprintTimelinePage page = await pagedProjector.CreateAsync(
                environment.ProjectRoot, sprintId.Value, cursor, cancellationToken);
            Assert.True(page.Items.Count <= 1);
            paged.AddRange(page.Items);
            cursor = page.Cursor;
            fetches++;
        }
        while (paged.Count < full.Items.Count && fetches <= full.Items.Count);

        Assert.Equal(full.Items.Count, fetches);
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
        // Round of PR #104 review, finding 1: the summary now gets its own real, dense
        // WorkflowEvent.Sequence, assigned atomically at append time -- unlike the original
        // borrowed-sequence design, it never shares a sequence with the event it followed. Every item
        // in a page has a distinct sequence, and the summary's own sequence is the highest (it was
        // appended last).
        Assert.DoesNotContain(page.Items, item => item.Sequence == summary.Sequence && item != summary);
        Assert.Equal(summary.Sequence, page.Items.Max(item => item.Sequence));
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
