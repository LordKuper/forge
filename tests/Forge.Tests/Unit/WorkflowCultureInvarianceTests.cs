using System.Globalization;
using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

/// <summary>
/// Durable workflow state must stay culture-invariant (architecture overview, "Localization and
/// artifact audiences"). Turkish is the standard regression case: "I".ToLower() produces 'ı'
/// (dotless i), not 'i', under culture-sensitive case conversion — exactly the kind of bug a
/// stray <c>ToLower()</c> instead of <c>ToLowerInvariant()</c> would introduce into a stored state
/// name, silently breaking every reader that expects the frozen ASCII contract spelling.
/// </summary>
public sealed class WorkflowCultureInvarianceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void StateNamesAreIdenticalUnderTurkishCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            // The concrete regression case: "Info" must fold to "info", never "ınfo".
            Assert.Equal("info", WorkflowStateNames.ToSnakeCase(FindingSeverity.Info));

            AssertRoundTrips<SprintState>();
            AssertRoundTrips<NodeState>();
            AssertRoundTrips<AttemptState>();
            AssertRoundTrips<AggregateKind>();
            AssertRoundTrips<SprintDependencyKind>();
            AssertRoundTrips<NodeKind>();
            AssertRoundTrips<FindingSeverity>();
            AssertRoundTrips<FindingStatus>();
            AssertRoundTrips<ArtifactAudience>();
            AssertRoundTrips<NodeOutcome>();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DurableStateWrittenUnderTurkishCultureReadsBackIdenticallyUnderInvariantCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        using TestEnvironment environment = new();
        SprintId sprintId;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            InitializeProjectResult init = await environment.InitializeAsync(
                environment.ProjectRoot, true, TestContext.Current.CancellationToken);
            Assert.True(init.Succeeded);
            SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
            SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
            CreateSprintResult created = await orchestrator.CreateSprintAsync(
                new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
                TestContext.Current.CancellationToken);
            sprintId = created.SprintId!;
            await scheduler.RecordFindingAsync(
                environment.ProjectRoot, sprintId, FindingSeverity.Info, "finding.example",
                new Dictionary<string, string?>(), ["evidence"], null, TestContext.Current.CancellationToken);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintWorkflowState state = (await store.LoadAsync(
            environment.ProjectRoot, sprintId, TestContext.Current.CancellationToken))!;
        Finding finding = Assert.Single(
            await store.GetFindingsAsync(environment.ProjectRoot, sprintId, TestContext.Current.CancellationToken));

        Assert.Equal(SprintState.Draft, state.Sprint.State);
        Assert.Equal(NodeState.Ready, state.Nodes["a"].State);
        Assert.Equal(FindingSeverity.Info, finding.Severity);
    }

    private static void AssertRoundTrips<TState>() where TState : struct, Enum
    {
        foreach (TState value in Enum.GetValues<TState>())
        {
            string snakeCase = WorkflowStateNames.ToSnakeCase(value);
            Assert.Matches("^[a-z0-9_]+$", snakeCase);
            Assert.Equal(value, WorkflowStateNames.Parse<TState>(snakeCase));
        }
    }
}
