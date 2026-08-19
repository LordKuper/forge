using System.Text.Json.Nodes;
using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class FileSprintEventLogTests
{
    // Round 6 review of PR #68: the null-LIST variant of round 5's null-element fix -- "graph": null
    // (the list itself, not an element inside it) reached Enumerable.Select's own null-check
    // directly, missed when round 5 fixed only the null-element case for this exact method.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadDefinitionAsyncWrapsAnExplicitNullGraphInAnInvalidDataException()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        string definitionPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "definition.json");
        JsonNode persisted = JsonNode.Parse(await File.ReadAllTextAsync(definitionPath, cancellationToken))!;
        persisted["graph"] = null;
        await File.WriteAllTextAsync(definitionPath, persisted.ToJsonString(), cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    // Deliberately asymmetric with the graph test above: unlike an empty graph (which would
    // silently pass SprintGraphValidator.IsValid and produce a frozen definition with no executable
    // nodes), an empty execution-profile set is already a legitimate, documented case -- a sprint
    // frozen before execution profiles existed has none. "execution_profiles": null is therefore
    // coalesced to empty rather than rejected, matching the pre-existing backward-compatibility
    // comment in LoadDefinitionAsync.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadDefinitionAsyncTreatsAnExplicitNullExecutionProfilesAsEmptyRatherThanThrowing()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        string definitionPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "definition.json");
        JsonNode persisted = JsonNode.Parse(await File.ReadAllTextAsync(definitionPath, cancellationToken))!;
        persisted["execution_profiles"] = null;
        await File.WriteAllTextAsync(definitionPath, persisted.ToJsonString(), cancellationToken);

        SprintDefinition definition =
            (await store.LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Empty(definition.ExecutionProfiles);
    }

    // Round 5 review of PR #68: self-identified while closing that round's two reported findings --
    // the same "null element inside an otherwise-present list" hazard also applies to
    // LoadDefinitionAsync's own "graph"/"dependencies"/"execution_profiles" arrays, and
    // LoadDefinitionAsync is IntakeExecutionHostedService.ExecuteIntakeAsync's own first read, ahead
    // of GetNodeResultsAsync/GetConfirmationsAsync entirely.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadDefinitionAsyncWrapsANullGraphElementInAnInvalidDataException()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        string definitionPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "definition.json");
        JsonNode persisted = JsonNode.Parse(await File.ReadAllTextAsync(definitionPath, cancellationToken))!;
        ((JsonArray)persisted["graph"]!).Add((JsonNode?)null);
        await File.WriteAllTextAsync(definitionPath, persisted.ToJsonString(), cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadDefinitionAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    // Round 4 review of PR #68: unlike Outputs/Diagnostics (round 2) and Evidence (round 3), an
    // explicit "attempt_id": null is not a legitimate empty value -- it is corrupt data, since
    // AttemptId is required and non-nullable in the domain. Guid.Parse(null) threw the same
    // unguarded ArgumentNullException round 3 already named and fixed for LoadValidatedEventsAsync's
    // own Guid.Parse, left uncovered here -- the fifth instance of the same defect class across
    // FileSprintEventLog, and the first found inside code this PR's own round 1 added.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetNodeResultsAsyncWrapsAnExplicitNullAttemptIdInAnInvalidDataException()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        NodeResult result = new(
            sprintId, new("intake"), new(Guid.NewGuid()), NodeOutcome.Succeeded, now, now,
            $"sha256:{new string('a', 64)}", [], []);
        await store.SaveNodeResultAsync(environment.ProjectRoot, result, cancellationToken);

        string resultPath = Directory.EnumerateFiles(
            Path.Combine(FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "results")).Single();
        JsonNode persisted = JsonNode.Parse(await File.ReadAllTextAsync(resultPath, cancellationToken))!;
        persisted["attempt_id"] = null;
        await File.WriteAllTextAsync(resultPath, persisted.ToJsonString(), cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    // Round 5 review of PR #68: round 2's `persisted.Diagnostics ?? []` only guards a null LIST --
    // "diagnostics": [null] (a null ELEMENT inside a present list) survives it untouched, and
    // FromPersisted(PersistedDiagnostic) dereferences the null element directly, throwing
    // NullReferenceException -- uncaught by every prior round's filter. The seventh instance of the
    // same defect class across FileSprintEventLog.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetNodeResultsAsyncWrapsANullDiagnosticElementInAnInvalidDataException()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        NodeResult result = new(
            sprintId, new("intake"), new(Guid.NewGuid()), NodeOutcome.Succeeded, now, now,
            $"sha256:{new string('a', 64)}", [], []);
        await store.SaveNodeResultAsync(environment.ProjectRoot, result, cancellationToken);

        string resultPath = Directory.EnumerateFiles(
            Path.Combine(FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "results")).Single();
        JsonNode persisted = JsonNode.Parse(await File.ReadAllTextAsync(resultPath, cancellationToken))!;
        persisted["diagnostics"] = new JsonArray((JsonNode?)null);
        await File.WriteAllTextAsync(resultPath, persisted.ToJsonString(), cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    // Round 2 review of PR #68: DefinitionJsonOptions' WhenWritingNull condition means the normal
    // write path (SaveNodeResultAsync always projects Outputs/Diagnostics through `[.. ...]`, never
    // null) can never itself produce "outputs": null / "diagnostics": null -- but nothing on the READ
    // side enforced that either, so a hand-edited or torn-write file with an explicit null there threw
    // ArgumentNullException from Enumerable.Select, escaping every caller's own catch filter (the
    // exact defect class round 1 already fixed once for a corrupt/unparsable file, reproduced here by
    // a different, syntactically-valid-JSON shape).
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetNodeResultsAsyncTreatsAnExplicitNullOutputsAndDiagnosticsAsEmptyRatherThanThrowing()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        NodeResult result = new(
            sprintId, new("intake"), new(Guid.NewGuid()), NodeOutcome.Succeeded, now, now,
            $"sha256:{new string('a', 64)}", [$"sha256:{new string('b', 64)}"],
            [new("some_code", "context", "diagnostic.some_code", new Dictionary<string, string?>())]);
        await store.SaveNodeResultAsync(environment.ProjectRoot, result, cancellationToken);

        string resultPath = Directory.EnumerateFiles(
            Path.Combine(FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "results")).Single();
        JsonNode persisted = JsonNode.Parse(await File.ReadAllTextAsync(resultPath, cancellationToken))!;
        persisted["outputs"] = null;
        persisted["diagnostics"] = null;
        await File.WriteAllTextAsync(resultPath, persisted.ToJsonString(), cancellationToken);

        IReadOnlyList<NodeResult> results =
            await store.GetNodeResultsAsync(environment.ProjectRoot, sprintId, cancellationToken);
        NodeResult read = Assert.Single(results);
        Assert.Empty(read.Outputs);
        Assert.Empty(read.Diagnostics);
    }

    // Round 3 review of PR #68: round 2's null-list fix (above) was applied to PersistedNodeResult
    // only. PersistedConfirmation.Evidence had the identical gap -- an explicit "evidence": null threw
    // ArgumentNullException from FromPersisted's `confirmation.Evidence.Select(...)`, unguarded by
    // GetConfirmationsAsync's own JsonException/FormatException/OverflowException filter, reachable
    // from AdvanceGraphAsync's own IsTestWorkEligibleAsync.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetConfirmationsAsyncTreatsAnExplicitNullEvidenceAsEmptyRatherThanThrowing()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        ConfirmationArtifact confirmation = new(
            Guid.NewGuid(), sprintId, new("confirmation"), ConfirmationOutcome.Confirmed, "Done.",
            [new(ConfirmationEvidenceKind.Inspection, "Read the diff.")], DateTimeOffset.UtcNow);
        await store.SaveConfirmationAsync(environment.ProjectRoot, confirmation, cancellationToken);

        string confirmationPath = Directory.EnumerateFiles(Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "confirmations")).Single();
        JsonNode persisted = JsonNode.Parse(await File.ReadAllTextAsync(confirmationPath, cancellationToken))!;
        persisted["evidence"] = null;
        await File.WriteAllTextAsync(confirmationPath, persisted.ToJsonString(), cancellationToken);

        IReadOnlyList<ConfirmationArtifact> confirmations =
            await store.GetConfirmationsAsync(environment.ProjectRoot, sprintId, cancellationToken);
        Assert.Empty(Assert.Single(confirmations).Evidence);
    }

    // Round 5 review of PR #68: the null-list case above (round 3's `?? []`) does not cover a null
    // ELEMENT inside a present list -- "evidence": [null] survives the coalesce untouched, and
    // FromPersisted(PersistedEvidence) dereferences the null element directly, throwing
    // NullReferenceException. The eighth instance of the same defect class across FileSprintEventLog.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetConfirmationsAsyncWrapsANullEvidenceElementInAnInvalidDataException()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        ConfirmationArtifact confirmation = new(
            Guid.NewGuid(), sprintId, new("confirmation"), ConfirmationOutcome.Confirmed, "Done.",
            [new(ConfirmationEvidenceKind.Inspection, "Read the diff.")], DateTimeOffset.UtcNow);
        await store.SaveConfirmationAsync(environment.ProjectRoot, confirmation, cancellationToken);

        string confirmationPath = Directory.EnumerateFiles(Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "confirmations")).Single();
        JsonNode persisted = JsonNode.Parse(await File.ReadAllTextAsync(confirmationPath, cancellationToken))!;
        persisted["evidence"] = new JsonArray((JsonNode?)null);
        await File.WriteAllTextAsync(confirmationPath, persisted.ToJsonString(), cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetConfirmationsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    // Round 2 review of PR #68: GetFindingsAsync/GetConfirmationsAsync had the same unwrapped
    // JsonException gap round 1 fixed for GetNodeResultsAsync -- latent today only because no
    // executor can yet drive a sprint far enough to make AdvanceGraphAsync's own
    // IsTestWorkEligibleAsync (reads confirmations) or CompleteAttemptAsync's own
    // EvaluateCompletionAsync (reads findings) actually reach a corrupt file, but exactly the same
    // permanent-ExecuteTask-fault shape once one does.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetFindingsAsyncWrapsACorruptFindingFileInAnInvalidDataException()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        string findingsDirectory = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "findings");
        Directory.CreateDirectory(findingsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(findingsDirectory, "corrupt.json"), "{ not valid json", cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetFindingsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetConfirmationsAsyncWrapsACorruptConfirmationFileInAnInvalidDataException()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        Assert.True((await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken)).Succeeded);
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        string confirmationsDirectory = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "confirmations");
        Directory.CreateDirectory(confirmationsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(confirmationsDirectory, "corrupt.json"), "{ not valid json", cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetConfirmationsAsync(environment.ProjectRoot, sprintId, cancellationToken));
    }

    // A Windows CI runner's virus scanner/indexer can transiently hold an incompatible handle on
    // events.jsonl right after it's written, even though every writer here opens with
    // FileShare.Read (observed as a real, reproducible CI failure, not a hypothetical). This
    // proves LoadAsync's retry absorbs a short-lived exclusive hold instead of failing immediately.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncRecoversFromATransientSharingViolationOnTheJournal()
    {
        if (!OperatingSystem.IsWindows())
        {
            // FileShare.None sharing-violation enforcement is reliably a hard failure only on
            // Windows; .NET's FileStream does not emulate it on Unix.
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken)).SprintId!;

        string eventsPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "events.jsonl");
        await using FileStream exclusiveLock = new(eventsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Task<SprintWorkflowState?> readTask =
            store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken);

        // Released well within the retry loop's total backoff window (20+40+60+80 = 200ms across
        // 4 delays before the 5th and final attempt), so the read must recover rather than exhaust
        // its retries.
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        await exclusiveLock.DisposeAsync();

        SprintWorkflowState? state = await readTask;
        Assert.NotNull(state);
    }
}
