using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class FileSprintEventLogTests
{
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
