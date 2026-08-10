using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>Verifies the two things the Stage 6 plan gate demands directly, end to end.</summary>
public sealed class StageSixGateTests
{
    private static readonly Regex MessageKeyPattern = new("^[a-z0-9_.-]+$", RegexOptions.Compiled);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EveryPersistedMessageKeyIsALocalizationKeyNeverRenderedText()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // Drive a realistic mix of transitions: two work nodes (one auto-retried after a
        // failure), a rejected human gate, a finding, and a handoff. The gate depends on "b" so it
        // only opens (and pauses the sprint) after both work nodes have already run their attempts.
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(
                environment.ProjectRoot,
                1,
                Guid.NewGuid(),
                Graph:
                [
                    new("a", NodeKind.Work, []),
                    new("b", NodeKind.Work, ["a"]),
                    new("gate", NodeKind.HumanGate, ["b"]),
                ]),
            cancellationToken)).SprintId!;
        await RunToRunningAsync(orchestrator, environment.ProjectRoot, sprintId, cancellationToken);
        ISprintStore store = environment.Resolve<ISprintStore>();

        StartAttemptResult firstAttempt = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", await NodeVersionAsync(store, environment.ProjectRoot, sprintId, "a", cancellationToken),
            cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", firstAttempt.AttemptId!, false, SampleDigest, [], [],
            cancellationToken);
        StartAttemptResult retryAttempt = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", await NodeVersionAsync(store, environment.ProjectRoot, sprintId, "a", cancellationToken),
            cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "a", retryAttempt.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);
        StartAttemptResult attemptB = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "b", await NodeVersionAsync(store, environment.ProjectRoot, sprintId, "b", cancellationToken),
            cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, sprintId, "b", attemptB.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);

        NodeSnapshot gate = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!.Nodes["gate"];
        await scheduler.ResolveHumanGateAsync(
            environment.ProjectRoot, sprintId, "gate", false, gate.Version,
            SprintScheduler.ResolveHumanGateKey(sprintId, gate), cancellationToken);

        await scheduler.RecordFindingAsync(
            environment.ProjectRoot, sprintId, FindingSeverity.Medium, "finding.example",
            new Dictionary<string, string?>(), ["evidence"], null, cancellationToken);
        await scheduler.RecordHandoffAsync(
            environment.ProjectRoot, sprintId, "b", new string('a', 40), "free text is fine here",
            [], [], null, cancellationToken);

        string sprintDirectory = FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId);
        List<string> messageKeys = [];
        foreach (string line in await File.ReadAllLinesAsync(Path.Combine(sprintDirectory, "events.jsonl"), cancellationToken))
        {
            if (line.Length == 0)
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(line);
            messageKeys.Add(document.RootElement.GetProperty("message_key").GetString()!);
        }

        string findingsDirectory = Path.Combine(sprintDirectory, "findings");
        foreach (string findingPath in Directory.GetFiles(findingsDirectory, "*.json"))
        {
            using JsonDocument finding = JsonDocument.Parse(await File.ReadAllTextAsync(findingPath, cancellationToken));
            messageKeys.Add(finding.RootElement.GetProperty("message_key").GetString()!);
        }

        Assert.NotEmpty(messageKeys);
        Assert.All(messageKeys, key => Assert.Matches(MessageKeyPattern, key));

        // Handoffs are the one durable record whose prose fields are free text by contract
        // (handoff.schema.json) because they are written for a model to read, not localized UI.
        // Handoff files are keyed by handoff id, not node id, so there is exactly one file here.
        string handoffPath = Directory.GetFiles(Path.Combine(sprintDirectory, "handoffs"), "*.json").Single();
        using JsonDocument handoff = JsonDocument.Parse(await File.ReadAllTextAsync(handoffPath, cancellationToken));
        Assert.Equal("free text is fine here", handoff.RootElement.GetProperty("summary").GetString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConcurrentSprintsStayIsolatedAndBothResumeAfterReopeningTheStoreFromScratch()
    {
        using TestEnvironment environment = await InitializedAsync();
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SprintId first = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("solo", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        SprintId second = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("solo", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        Assert.NotEqual(first, second);

        await RunToRunningAsync(orchestrator, environment.ProjectRoot, first, cancellationToken);
        StartAttemptResult attempt =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, first, "solo", 2, cancellationToken);
        await scheduler.CompleteAttemptAsync(
            environment.ProjectRoot, first, "solo", attempt.AttemptId!, true, SampleDigest, [], [],
            cancellationToken);
        // The second sprint is deliberately left untouched at `draft`.

        ISprintStore liveStore = environment.Resolve<ISprintStore>();
        SprintWorkflowState firstLive = (await liveStore.LoadAsync(environment.ProjectRoot, first, cancellationToken))!;
        SprintWorkflowState secondLive = (await liveStore.LoadAsync(environment.ProjectRoot, second, cancellationToken))!;
        Assert.Equal(SprintState.ReadyToFinalize, firstLive.Sprint.State);
        Assert.Equal(SprintState.Draft, secondLive.Sprint.State);
        Assert.Empty(await liveStore.GetNodeResultsAsync(environment.ProjectRoot, second, cancellationToken));

        // A brand new store instance shares nothing but the durable files on disk — the closest a
        // single-process test can get to simulating a full process restart.
        FileSprintEventLog reopened = new(new FakeClock());
        SprintWorkflowState firstReopened = (await reopened.LoadAsync(environment.ProjectRoot, first, cancellationToken))!;
        SprintWorkflowState secondReopened = (await reopened.LoadAsync(environment.ProjectRoot, second, cancellationToken))!;

        Assert.Equal(SprintState.ReadyToFinalize, firstReopened.Sprint.State);
        Assert.Equal(NodeState.Succeeded, firstReopened.Nodes["solo"].State);
        Assert.Equal(SprintState.Draft, secondReopened.Sprint.State);
        Assert.Equal(NodeState.Ready, secondReopened.Nodes["solo"].State);
        Assert.Single(await reopened.GetNodeResultsAsync(environment.ProjectRoot, first, cancellationToken));
        Assert.Empty(await reopened.GetNodeResultsAsync(environment.ProjectRoot, second, cancellationToken));
    }

    private static readonly string SampleDigest = "sha256:" + new string('0', 64);

    private static async Task<long> NodeVersionAsync(
        ISprintStore store,
        string root,
        SprintId sprintId,
        string nodeId,
        CancellationToken cancellationToken) =>
        (await store.LoadAsync(root, sprintId, cancellationToken))!.Nodes[nodeId].Version;

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

    private static async Task<TestEnvironment> InitializedAsync()
    {
        TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        return environment;
    }
}
