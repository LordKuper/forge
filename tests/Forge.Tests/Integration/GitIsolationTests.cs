using Forge.Application;
using Forge.Domain;
using Forge.Tests.Support;

namespace Forge.IntegrationTests;

/// <summary>
/// Exercises <see cref="SprintGitIsolation"/> against a real, disposable Git repository — worktree
/// creation, dirty recovery, the integration barrier's base check, gated rebase (both the clean and
/// the conflicting path), clean replay, and crash-recovery reconciliation are all real `git.exe`
/// behavior, not something a fake can stand in for.
/// </summary>
[Collection("External process tests")]
public sealed class GitIsolationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task EnsureIntegrationWorktreeCreatesAWorktreeCheckedOutAtTheFrozenBaseCommit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        using TestEnvironment environment = new();
        SprintGitIsolation isolation = environment.Resolve<SprintGitIsolation>();
        IWorktreeManager worktrees = environment.Resolve<IWorktreeManager>();
        SprintId sprintId = SprintId.New();
        Guid projectId = Guid.NewGuid();
        string baseCommit = await repository.HeadAsync(cancellationToken);

        GitOperationResult result = await isolation.EnsureIntegrationWorktreeAsync(
            repository.Root, projectId, sprintId, baseCommit, cancellationToken);

        Assert.True(result.Succeeded);
        string path = WorktreeLayout.IntegrationPath(environment, projectId, sprintId);
        Assert.True(await worktrees.ExistsAsync(repository.Root, path, cancellationToken));
        Assert.Equal(baseCommit, await worktrees.GetHeadAsync(repository.Root, path, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EnsureIntegrationWorktreeRecoversFromUncommittedNoiseWithoutMovingHistory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        using TestEnvironment environment = new();
        SprintGitIsolation isolation = environment.Resolve<SprintGitIsolation>();
        IWorktreeManager worktrees = environment.Resolve<IWorktreeManager>();
        SprintId sprintId = SprintId.New();
        Guid projectId = Guid.NewGuid();
        string baseCommit = await repository.HeadAsync(cancellationToken);
        await isolation.EnsureIntegrationWorktreeAsync(
            repository.Root, projectId, sprintId, baseCommit, cancellationToken);
        string path = WorktreeLayout.IntegrationPath(environment, projectId, sprintId);
        await File.WriteAllTextAsync(Path.Combine(path, "crash-leftover.txt"), "garbage", cancellationToken);
        Assert.True(await worktrees.IsDirtyAsync(repository.Root, path, cancellationToken));

        GitOperationResult recovered = await isolation.EnsureIntegrationWorktreeAsync(
            repository.Root, projectId, sprintId, baseCommit, cancellationToken);

        Assert.True(recovered.Succeeded);
        Assert.False(await worktrees.IsDirtyAsync(repository.Root, path, cancellationToken));
        Assert.Equal(baseCommit, await worktrees.GetHeadAsync(repository.Root, path, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EnsureIntegrationWorktreeRecoversFromADeletedDirectoryWithoutLosingAlreadyIntegratedHistory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        using TestEnvironment environment = new();
        SprintGitIsolation isolation = environment.Resolve<SprintGitIsolation>();
        IWorktreeManager worktrees = environment.Resolve<IWorktreeManager>();
        (SprintId sprintId, Guid projectId, string tip) =
            await SetUpIntegrationAsync(isolation, repository, environment, cancellationToken);
        AttemptId attemptId = AttemptId.New();
        await CreateAttemptWorktreeOrFailAsync(isolation, repository.Root, projectId, sprintId, attemptId, cancellationToken);
        string attemptPath = WorktreeLayout.AttemptPath(environment, projectId, sprintId, attemptId);
        string attemptCommit = await repository.CommitFileAsync(
            "feature.txt", "hello", "add feature", cancellationToken, attemptPath);
        GitOperationResult integrated = await isolation.IntegrateAsync(
            repository.Root, projectId, sprintId, attemptId, tip, cancellationToken);
        Assert.True(integrated.Succeeded);
        string integrationPath = WorktreeLayout.IntegrationPath(environment, projectId, sprintId);

        // Simulates external deletion (not a Forge-driven `RemoveAsync`) — e.g. the user emptying a
        // temp/cache directory — leaving `git` still believing the worktree is registered.
        Directory.Delete(integrationPath, true);

        GitOperationResult recovered = await isolation.EnsureIntegrationWorktreeAsync(
            repository.Root, projectId, sprintId, tip, cancellationToken);

        Assert.True(recovered.Succeeded);
        // The commit already integrated before the deletion must survive recovery — recreating the
        // worktree from the sprint's original frozen base (discarding this real history) would be a
        // silent, permanent data-loss regression.
        Assert.Equal(attemptCommit, await worktrees.GetHeadAsync(repository.Root, integrationPath, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateAttemptWorktreeBranchesFromTheIntegrationTipAndIsIdempotentForTheSameAttemptId()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        using TestEnvironment environment = new();
        SprintGitIsolation isolation = environment.Resolve<SprintGitIsolation>();
        (SprintId sprintId, Guid projectId, string tip) =
            await SetUpIntegrationAsync(isolation, repository, environment, cancellationToken);
        AttemptId attemptId = AttemptId.New();

        GitOperationResult first = await isolation.CreateAttemptWorktreeAsync(
            repository.Root, projectId, sprintId, attemptId, cancellationToken);
        GitOperationResult second = await isolation.CreateAttemptWorktreeAsync(
            repository.Root, projectId, sprintId, attemptId, cancellationToken);

        Assert.True(first.Succeeded);
        Assert.Equal(tip, first.Commit);
        Assert.True(second.Succeeded);
        Assert.Equal(first.Commit, second.Commit);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IntegrateFastForwardsTheAttemptsCommitIntoIntegrationAndDiscardsTheAttemptWorktree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        using TestEnvironment environment = new();
        SprintGitIsolation isolation = environment.Resolve<SprintGitIsolation>();
        IWorktreeManager worktrees = environment.Resolve<IWorktreeManager>();
        (SprintId sprintId, Guid projectId, string tip) =
            await SetUpIntegrationAsync(isolation, repository, environment, cancellationToken);
        AttemptId attemptId = AttemptId.New();
        await CreateAttemptWorktreeOrFailAsync(isolation, repository.Root, projectId, sprintId, attemptId, cancellationToken);
        string attemptPath = WorktreeLayout.AttemptPath(environment, projectId, sprintId, attemptId);
        string attemptCommit = await repository.CommitFileAsync(
            "feature.txt", "hello", "add feature", cancellationToken, attemptPath);

        GitOperationResult integrated = await isolation.IntegrateAsync(
            repository.Root, projectId, sprintId, attemptId, tip, cancellationToken);

        Assert.True(integrated.Succeeded);
        Assert.Equal(attemptCommit, integrated.Commit);
        // A clean integration must also report a clean cleanup — `CleanupSucceeded` is a distinct
        // signal from `Succeeded`, and this is its only "both true" coverage.
        Assert.True(integrated.CleanupSucceeded);
        string integrationPath = WorktreeLayout.IntegrationPath(environment, projectId, sprintId);
        Assert.Equal(attemptCommit, await worktrees.GetHeadAsync(repository.Root, integrationPath, cancellationToken));
        Assert.False(await worktrees.ExistsAsync(repository.Root, attemptPath, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IntegrateFailsClosedAndChangesNothingWhenTheIntegrationBaseHasMovedSinceTheAttemptStarted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        using TestEnvironment environment = new();
        SprintGitIsolation isolation = environment.Resolve<SprintGitIsolation>();
        IWorktreeManager worktrees = environment.Resolve<IWorktreeManager>();
        (SprintId sprintId, Guid projectId, string tip) =
            await SetUpIntegrationAsync(isolation, repository, environment, cancellationToken);

        // Both attempts branch from the same original tip *before* either integrates — the
        // concurrent-attempts scenario the base check exists for.
        AttemptId attemptA = AttemptId.New();
        await CreateAttemptWorktreeOrFailAsync(isolation, repository.Root, projectId, sprintId, attemptA, cancellationToken);
        string pathA = WorktreeLayout.AttemptPath(environment, projectId, sprintId, attemptA);
        await repository.CommitFileAsync("a.txt", "a", "add a", cancellationToken, pathA);

        AttemptId attemptB = AttemptId.New();
        await CreateAttemptWorktreeOrFailAsync(isolation, repository.Root, projectId, sprintId, attemptB, cancellationToken);
        string pathB = WorktreeLayout.AttemptPath(environment, projectId, sprintId, attemptB);
        await repository.CommitFileAsync("b.txt", "b", "add b", cancellationToken, pathB);

        GitOperationResult firstIntegration = await isolation.IntegrateAsync(
            repository.Root, projectId, sprintId, attemptA, tip, cancellationToken);
        Assert.True(firstIntegration.Succeeded);

        string integrationPath = WorktreeLayout.IntegrationPath(environment, projectId, sprintId);
        string headBeforeMismatch = await worktrees.GetHeadAsync(repository.Root, integrationPath, cancellationToken);
        string headBeforeMismatchB = await worktrees.GetHeadAsync(repository.Root, pathB, cancellationToken);

        GitOperationResult mismatched = await isolation.IntegrateAsync(
            repository.Root, projectId, sprintId, attemptB, tip, cancellationToken);

        Assert.False(mismatched.Succeeded);
        Assert.Equal(DiagnosticCodes.WorktreeBaseMismatch, mismatched.DiagnosticCode);
        Assert.True(await worktrees.ExistsAsync(repository.Root, pathB, cancellationToken));
        // "Changes nothing" is asserted directly, not just inferred from the diagnostic code: both
        // the integration branch and B's own attempt branch must be exactly where they were before
        // the rejected call.
        Assert.Equal(headBeforeMismatch, await worktrees.GetHeadAsync(repository.Root, integrationPath, cancellationToken));
        Assert.Equal(headBeforeMismatchB, await worktrees.GetHeadAsync(repository.Root, pathB, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RebasingAStaleAttemptOntoTheNewIntegrationTipLetsItThenIntegrateCleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        using TestEnvironment environment = new();
        SprintGitIsolation isolation = environment.Resolve<SprintGitIsolation>();
        IWorktreeManager worktrees = environment.Resolve<IWorktreeManager>();
        (SprintId sprintId, Guid projectId, string tip) =
            await SetUpIntegrationAsync(isolation, repository, environment, cancellationToken);

        // Both attempts branch from the same original tip *before* either integrates, so B's
        // rebase below replays a genuine independent commit rather than a no-op onto its own
        // ancestor.
        AttemptId attemptA = AttemptId.New();
        await CreateAttemptWorktreeOrFailAsync(isolation, repository.Root, projectId, sprintId, attemptA, cancellationToken);
        string pathA = WorktreeLayout.AttemptPath(environment, projectId, sprintId, attemptA);
        await repository.CommitFileAsync("a.txt", "a", "add a", cancellationToken, pathA);

        AttemptId attemptB = AttemptId.New();
        await CreateAttemptWorktreeOrFailAsync(isolation, repository.Root, projectId, sprintId, attemptB, cancellationToken);
        string pathB = WorktreeLayout.AttemptPath(environment, projectId, sprintId, attemptB);
        await repository.CommitFileAsync("b.txt", "b", "add b", cancellationToken, pathB);

        GitOperationResult firstIntegration = await isolation.IntegrateAsync(
            repository.Root, projectId, sprintId, attemptA, tip, cancellationToken);
        Assert.True(firstIntegration.Succeeded);

        GitOperationResult rebased = await isolation.RebaseAttemptAsync(
            repository.Root, projectId, sprintId, attemptB, tip, cancellationToken);
        Assert.True(rebased.Succeeded);

        GitOperationResult secondIntegration = await isolation.IntegrateAsync(
            repository.Root, projectId, sprintId, attemptB, firstIntegration.Commit!, cancellationToken);

        Assert.True(secondIntegration.Succeeded);
        string integrationPath = WorktreeLayout.IntegrationPath(environment, projectId, sprintId);
        string finalHead = await worktrees.GetHeadAsync(repository.Root, integrationPath, cancellationToken);
        Assert.Equal(secondIntegration.Commit, finalHead);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AConflictingRebaseFailsClosedAndLeavesTheAttemptWorktreeCleanAndUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        using TestEnvironment environment = new();
        SprintGitIsolation isolation = environment.Resolve<SprintGitIsolation>();
        IWorktreeManager worktrees = environment.Resolve<IWorktreeManager>();
        (SprintId sprintId, Guid projectId, string tip) =
            await SetUpIntegrationAsync(isolation, repository, environment, cancellationToken);

        // Both attempts branch from the same original tip *before* either integrates, and both
        // create the same new file with different content — a genuine conflict once D is replayed
        // onto the tip after C's own creation of that same path has already landed there.
        AttemptId attemptC = AttemptId.New();
        await CreateAttemptWorktreeOrFailAsync(isolation, repository.Root, projectId, sprintId, attemptC, cancellationToken);
        string pathC = WorktreeLayout.AttemptPath(environment, projectId, sprintId, attemptC);
        await repository.CommitFileAsync("conflict.txt", "from C", "C edits", cancellationToken, pathC);

        AttemptId attemptD = AttemptId.New();
        await CreateAttemptWorktreeOrFailAsync(isolation, repository.Root, projectId, sprintId, attemptD, cancellationToken);
        string pathD = WorktreeLayout.AttemptPath(environment, projectId, sprintId, attemptD);
        string attemptDCommit = await repository.CommitFileAsync(
            "conflict.txt", "from D", "D edits", cancellationToken, pathD);

        GitOperationResult integrated = await isolation.IntegrateAsync(
            repository.Root, projectId, sprintId, attemptC, tip, cancellationToken);
        Assert.True(integrated.Succeeded);

        GitOperationResult rebased = await isolation.RebaseAttemptAsync(
            repository.Root, projectId, sprintId, attemptD, tip, cancellationToken);

        Assert.False(rebased.Succeeded);
        Assert.Equal(DiagnosticCodes.WorktreeRebaseConflict, rebased.DiagnosticCode);
        Assert.Equal(attemptDCommit, await worktrees.GetHeadAsync(repository.Root, pathD, cancellationToken));
        Assert.False(await worktrees.IsDirtyAsync(repository.Root, pathD, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DiscardingAnAttemptRemovesBothItsWorktreeAndItsBranchSoAFreshReplayNeverSeesItsContent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        using TestEnvironment environment = new();
        SprintGitIsolation isolation = environment.Resolve<SprintGitIsolation>();
        IWorktreeManager worktrees = environment.Resolve<IWorktreeManager>();
        (SprintId sprintId, Guid projectId, string _) =
            await SetUpIntegrationAsync(isolation, repository, environment, cancellationToken);
        AttemptId attemptId = AttemptId.New();
        await CreateAttemptWorktreeOrFailAsync(isolation, repository.Root, projectId, sprintId, attemptId, cancellationToken);
        string attemptPath = WorktreeLayout.AttemptPath(environment, projectId, sprintId, attemptId);
        await repository.CommitFileAsync("scratch.txt", "abandoned work", "scratch", cancellationToken, attemptPath);

        bool discarded = await isolation.DiscardAttemptAsync(repository.Root, projectId, sprintId, attemptId, cancellationToken);
        Assert.True(discarded);

        // `ExistsAsync` now means "registered *and* physically present", so it alone cannot tell a
        // fully-removed worktree apart from a leaked directory whose registration alone was pruned —
        // both directory absence and de-registration are asserted directly.
        Assert.False(await worktrees.ExistsAsync(repository.Root, attemptPath, cancellationToken));
        Assert.False(Directory.Exists(attemptPath));
        ProcessResult worktreeList = await repository.RunAsync(
            repository.Root, ["worktree", "list", "--porcelain"], cancellationToken);
        Assert.DoesNotContain(attemptPath, worktreeList.StandardOutput, StringComparison.OrdinalIgnoreCase);
        ProcessResult branches = await repository.RunAsync(
            repository.Root, ["branch", "--list", WorktreeLayout.AttemptBranch(attemptId)], cancellationToken);
        Assert.Equal(string.Empty, branches.StandardOutput.Trim());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReconcileRemovesOrphanedAttemptWorktreesButLeavesANonTerminalAttemptUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        using TestEnvironment environment = new();
        SprintGitIsolation isolation = environment.Resolve<SprintGitIsolation>();
        IWorktreeManager worktrees = environment.Resolve<IWorktreeManager>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        (SprintId sprintId, Guid projectId, string _) =
            await SetUpIntegrationAsync(isolation, repository, environment, cancellationToken);

        AttemptId terminalAttempt = await SeedAttemptAsync(store, repository.Root, sprintId, AttemptState.Failed, cancellationToken);
        AttemptId runningAttempt = await SeedAttemptAsync(store, repository.Root, sprintId, AttemptState.Running, cancellationToken);
        AttemptId unknownAttempt = AttemptId.New();
        foreach (AttemptId attemptId in new[] { terminalAttempt, runningAttempt, unknownAttempt })
        {
            await CreateAttemptWorktreeOrFailAsync(isolation, repository.Root, projectId, sprintId, attemptId, cancellationToken);
        }

        await isolation.ReconcileAsync(repository.Root, projectId, sprintId, cancellationToken);

        string terminalPath = WorktreeLayout.AttemptPath(environment, projectId, sprintId, terminalAttempt);
        string unknownPath = WorktreeLayout.AttemptPath(environment, projectId, sprintId, unknownAttempt);
        string runningPath = WorktreeLayout.AttemptPath(environment, projectId, sprintId, runningAttempt);
        // `ExistsAsync` now means "registered *and* physically present" — asserting the directory
        // itself is gone (not just unregistered) is what actually proves reconciliation cleaned up,
        // rather than merely pruned a registration and left files behind.
        Assert.False(await worktrees.ExistsAsync(repository.Root, terminalPath, cancellationToken));
        Assert.False(Directory.Exists(terminalPath));
        Assert.False(await worktrees.ExistsAsync(repository.Root, unknownPath, cancellationToken));
        Assert.False(Directory.Exists(unknownPath));
        Assert.True(await worktrees.ExistsAsync(repository.Root, runningPath, cancellationToken));
        Assert.True(Directory.Exists(runningPath));
    }

    /// <summary>Creates an attempt worktree and asserts it actually succeeded before the caller
    /// proceeds to use its path — a silent creation failure must surface here, as a clear diagnostic
    /// code, rather than cascade into a confusing crash several calls later against a directory that
    /// was never created.</summary>
    private static async Task<GitOperationResult> CreateAttemptWorktreeOrFailAsync(
        SprintGitIsolation isolation,
        string projectRoot,
        Guid projectId,
        SprintId sprintId,
        AttemptId attemptId,
        CancellationToken cancellationToken)
    {
        GitOperationResult result = await isolation
            .CreateAttemptWorktreeAsync(projectRoot, projectId, sprintId, attemptId, cancellationToken)
            .ConfigureAwait(false);
        Assert.True(result.Succeeded, $"CreateAttemptWorktreeAsync failed: {result.DiagnosticCode}");
        return result;
    }

    private static async Task<(SprintId SprintId, Guid ProjectId, string Tip)> SetUpIntegrationAsync(
        SprintGitIsolation isolation,
        GitTestRepository repository,
        TestEnvironment environment,
        CancellationToken cancellationToken)
    {
        SprintId sprintId = SprintId.New();
        Guid projectId = Guid.NewGuid();
        string baseCommit = await repository.HeadAsync(cancellationToken);
        GitOperationResult result = await isolation.EnsureIntegrationWorktreeAsync(
            repository.Root, projectId, sprintId, baseCommit, cancellationToken);
        Assert.True(result.Succeeded);
        return (sprintId, projectId, baseCommit);
    }

    /// <summary>Appends the minimal sprint + attempt event stream needed to give one attempt id a
    /// chosen state, without going through the full scheduler.</summary>
    private static async Task<AttemptId> SeedAttemptAsync(
        ISprintStore store,
        string projectRoot,
        SprintId sprintId,
        AttemptState targetState,
        CancellationToken cancellationToken)
    {
        string sprintKey = sprintId.Value.ToString("D");
        if (await store.LoadAsync(projectRoot, sprintId, cancellationToken) is null)
        {
            await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Sprint, sprintKey, "SprintChanged", "workflow.sprint_created",
                "draft", 0, Guid.NewGuid(), cancellationToken);
        }

        AttemptId attemptId = AttemptId.New();
        string attemptKey = attemptId.Value.ToString("D");
        await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged", "workflow.attempt_created",
            "created", 0, Guid.NewGuid(), cancellationToken);
        if (targetState == AttemptState.Created)
        {
            return attemptId;
        }

        await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged",
            "workflow.attempt_transitioned", "preparing", 1, Guid.NewGuid(), cancellationToken);
        if (targetState == AttemptState.Preparing)
        {
            return attemptId;
        }

        await store.AppendTransitionAsync(
            projectRoot, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged",
            "workflow.attempt_transitioned", "running", 2, Guid.NewGuid(), cancellationToken);
        if (targetState == AttemptState.Running)
        {
            return attemptId;
        }

        if (targetState == AttemptState.Failed)
        {
            await store.AppendTransitionAsync(
                projectRoot, sprintId, AggregateKind.Attempt, attemptKey, "AttemptChanged",
                "workflow.attempt_transitioned", "failed", 3, Guid.NewGuid(), cancellationToken);
            return attemptId;
        }

        throw new NotSupportedException($"Seeding state '{targetState}' is not implemented by this test helper.");
    }
}
