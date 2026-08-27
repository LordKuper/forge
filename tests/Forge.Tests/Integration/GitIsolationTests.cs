using System.Runtime.CompilerServices;
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

        AssertSucceeded(result);
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

        AssertSucceeded(recovered);
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
        AssertSucceeded(integrated);
        string integrationPath = WorktreeLayout.IntegrationPath(environment, projectId, sprintId);

        // Simulates external deletion (not a Forge-driven `RemoveAsync`) — e.g. the user emptying a
        // temp/cache directory — leaving `git` still believing the worktree is registered.
        Directory.Delete(integrationPath, true);

        GitOperationResult recovered = await isolation.EnsureIntegrationWorktreeAsync(
            repository.Root, projectId, sprintId, tip, cancellationToken);

        AssertSucceeded(recovered);
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

        AssertSucceeded(first);
        Assert.Equal(tip, first.Commit);
        AssertSucceeded(second);
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

        AssertSucceeded(integrated);
        Assert.Equal(attemptCommit, integrated.Commit);
        // A clean integration must also report a clean cleanup — `CleanupSucceeded` is a distinct
        // signal from `Succeeded`, and this is its only "both true" coverage.
        Assert.True(integrated.CleanupSucceeded);
        string integrationPath = WorktreeLayout.IntegrationPath(environment, projectId, sprintId);
        Assert.Equal(attemptCommit, await worktrees.GetHeadAsync(repository.Root, integrationPath, cancellationToken));
        Assert.False(await worktrees.ExistsAsync(repository.Root, attemptPath, cancellationToken));
    }

    // The commit primitive this stage's node executors need (Stage 11: "invoke a provider, commit
    // its edits" -- ADR 0004's own class doc). Never exercised before this test: every prior test
    // in this file that needed an attempt branch with a real commit past base used
    // GitTestRepository.CommitFileAsync (a test-only fixture helper) precisely because nothing in
    // production could produce one.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CommitAttemptStagesAndCommitsEveryChangeAuthoredAsForgeRegardlessOfTheRepositorysOwnIdentity()
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
        // A tracked file's edit AND an untracked new file -- `git add -A` must stage both, not just
        // changes to files git already knows about.
        await File.WriteAllTextAsync(Path.Combine(attemptPath, "new-feature.txt"), "hello", cancellationToken);

        GitOperationResult committed = await isolation.CommitAttemptAsync(
            repository.Root, projectId, sprintId, attemptId, "Implement the feature", cancellationToken);

        AssertSucceeded(committed);
        Assert.NotEqual(tip, committed.Commit);
        Assert.Equal(committed.Commit, await worktrees.GetHeadAsync(repository.Root, attemptPath, cancellationToken));
        Assert.False(await worktrees.IsDirtyAsync(repository.Root, attemptPath, cancellationToken));
        // Authored as Forge itself, never the repository-level identity GitTestRepository.CreateAsync
        // configured ("Forge Tests <forge-tests@example.invalid>") -- proves CommitAllAsync's own
        // identity override actually takes effect rather than merely falling through to whatever the
        // ambient repository config happens to have.
        ProcessResult authorship = await repository.RunAsync(
            attemptPath, ["log", "-1", "--format=%an <%ae>|%cn <%ce>"], cancellationToken);
        Assert.Equal(0, authorship.ExitCode);
        Assert.Equal("Forge <forge@localhost>|Forge <forge@localhost>", authorship.StandardOutput.Trim());
    }

    // CommitAllAsync must never silently no-op on a clean tree: a caller that means "nothing to
    // integrate" checks IsDirtyAsync itself first (SprintGitIsolation's own doc comment states this
    // is the caller's policy, not this method's) -- so calling this on an unmodified worktree must
    // fail closed and change nothing, the same "never continue over an unknown diff" discipline
    // every other method in this class already follows.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CommitAttemptFailsClosedAndChangesNothingWhenTheWorktreeHasNoUncommittedChanges()
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
        (SprintId sprintId, Guid projectId, _) =
            await SetUpIntegrationAsync(isolation, repository, environment, cancellationToken);
        AttemptId attemptId = AttemptId.New();
        GitOperationResult created = await CreateAttemptWorktreeOrFailAsync(
            isolation, repository.Root, projectId, sprintId, attemptId, cancellationToken);
        string attemptPath = WorktreeLayout.AttemptPath(environment, projectId, sprintId, attemptId);

        GitOperationResult committed = await isolation.CommitAttemptAsync(
            repository.Root, projectId, sprintId, attemptId, "Nothing changed", cancellationToken);

        Assert.False(committed.Succeeded);
        Assert.Equal(DiagnosticCodes.WorktreeCommitFailed, committed.DiagnosticCode);
        Assert.Equal(created.Commit, await worktrees.GetHeadAsync(repository.Root, attemptPath, cancellationToken));
    }

    // End-to-end proof that CommitAllAsync's own output is exactly what IntegrateAsync's existing
    // fast-forward contract expects -- the same scenario
    // IntegrateFastForwardsTheAttemptsCommitIntoIntegrationAndDiscardsTheAttemptWorktree already
    // covers, but with the real production commit primitive in place of that test's own
    // GitTestRepository.CommitFileAsync fixture call.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ACommitMadeByCommitAttemptAsyncIntegratesCleanlyViaFastForward()
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
        await File.WriteAllTextAsync(Path.Combine(attemptPath, "feature.txt"), "hello", cancellationToken);
        GitOperationResult committed = await isolation.CommitAttemptAsync(
            repository.Root, projectId, sprintId, attemptId, "Implement the feature", cancellationToken);
        AssertSucceeded(committed);

        GitOperationResult integrated = await isolation.IntegrateAsync(
            repository.Root, projectId, sprintId, attemptId, tip, cancellationToken);

        AssertSucceeded(integrated);
        Assert.Equal(committed.Commit, integrated.Commit);
        Assert.True(integrated.CleanupSucceeded);
        string integrationPath = WorktreeLayout.IntegrationPath(environment, projectId, sprintId);
        Assert.Equal(committed.Commit, await worktrees.GetHeadAsync(repository.Root, integrationPath, cancellationToken));
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
        AssertSucceeded(firstIntegration);

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
        AssertSucceeded(firstIntegration);

        GitOperationResult rebased = await isolation.RebaseAttemptAsync(
            repository.Root, projectId, sprintId, attemptB, tip, cancellationToken);
        AssertSucceeded(rebased);

        GitOperationResult secondIntegration = await isolation.IntegrateAsync(
            repository.Root, projectId, sprintId, attemptB, firstIntegration.Commit!, cancellationToken);

        AssertSucceeded(secondIntegration);
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
        AssertSucceeded(integrated);

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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadDiffReturnsTheRealGitDiffBetweenTheAttemptsBaseAndItsCurrentTip()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        using TestEnvironment environment = new();
        SprintGitIsolation isolation = environment.Resolve<SprintGitIsolation>();
        (SprintId sprintId, Guid projectId, string baseCommit) =
            await SetUpIntegrationAsync(isolation, repository, environment, cancellationToken);
        AttemptId attemptId = AttemptId.New();
        await CreateAttemptWorktreeOrFailAsync(isolation, repository.Root, projectId, sprintId, attemptId, cancellationToken);
        string attemptPath = WorktreeLayout.AttemptPath(environment, projectId, sprintId, attemptId);
        await File.WriteAllTextAsync(Path.Combine(attemptPath, "new-feature.txt"), "hello", cancellationToken);
        GitOperationResult committed = await isolation.CommitAttemptAsync(
            repository.Root, projectId, sprintId, attemptId, "Implement the feature", cancellationToken);
        AssertSucceeded(committed);

        GitDiffResult diff = await isolation.ReadDiffAsync(
            repository.Root, projectId, sprintId, attemptId, baseCommit, committed.Commit!, cancellationToken);

        Assert.True(diff.Succeeded, $"ReadDiffAsync failed: {diff.DiagnosticCode} ({diff.Detail})");
        Assert.False(diff.Truncated);
        Assert.Contains("new-feature.txt", diff.Diff, StringComparison.Ordinal);
        Assert.Contains("+hello", diff.Diff, StringComparison.Ordinal);
    }

    // Regression test for a real bug an independent PR #74 review found: the first cut of this
    // truncation sliced at a raw UTF-16 offset with no surrogate-pair check, unlike this codebase's
    // own established safe-truncation pattern (ImplementationExecutionHostedService's commit-subject
    // Truncate, ADR 0032's own review finding). A diff whose content is entirely astral characters
    // (surrogate pairs) guarantees the 50,000-character cut lands inside one, regardless of the
    // header's own exact length -- so this reproduces the split deterministically rather than by luck.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadDiffTruncatesWithoutSplittingASurrogatePair()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        using TestEnvironment environment = new();
        SprintGitIsolation isolation = environment.Resolve<SprintGitIsolation>();
        (SprintId sprintId, Guid projectId, string baseCommit) =
            await SetUpIntegrationAsync(isolation, repository, environment, cancellationToken);
        AttemptId attemptId = AttemptId.New();
        await CreateAttemptWorktreeOrFailAsync(isolation, repository.Root, projectId, sprintId, attemptId, cancellationToken);
        string attemptPath = WorktreeLayout.AttemptPath(environment, projectId, sprintId, attemptId);
        // U+1F600 ("😀") is a surrogate pair in UTF-16; 40,000 repeats is 80,000 code units, so the
        // 50,000-character truncation boundary is guaranteed to fall inside this run no matter how
        // long the diff's own header text turns out to be.
        string hugeContent = string.Concat(Enumerable.Repeat("\U0001F600", 40_000));
        await File.WriteAllTextAsync(Path.Combine(attemptPath, "big.txt"), hugeContent, cancellationToken);
        GitOperationResult committed = await isolation.CommitAttemptAsync(
            repository.Root, projectId, sprintId, attemptId, "Add a huge file", cancellationToken);
        AssertSucceeded(committed);

        GitDiffResult diff = await isolation.ReadDiffAsync(
            repository.Root, projectId, sprintId, attemptId, baseCommit, committed.Commit!, cancellationToken);

        Assert.True(diff.Succeeded, $"ReadDiffAsync failed: {diff.DiagnosticCode} ({diff.Detail})");
        Assert.True(diff.Truncated);
        Assert.True(diff.Diff!.Length is 49_999 or 50_000);
        Assert.False(char.IsHighSurrogate(diff.Diff[^1]));
    }

    /// <summary>ADR 0059. Exercises all five <see cref="DiffChangeKinds"/> against real `git.exe` in
    /// one commit, because the classification is the one part of this that cannot be derived from
    /// `--numstat` alone: it is joined from a second `--name-status` invocation, and a binary file
    /// (which `--numstat` reports as `-`/`-`, but `--name-status` reports as an ordinary `M`) must
    /// come back as <see cref="DiffChangeKinds.Binary"/> with zero counts rather than as a modified
    /// file with zero counts. The rename is what proves the `-z` parsing: plain `--numstat` collapses
    /// it into a single ambiguous `old =&gt; new` field.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadDiffStatClassifiesEveryChangeKindAndTotalsRealGitLineCounts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        using TestEnvironment environment = new();
        SprintGitIsolation isolation = environment.Resolve<SprintGitIsolation>();
        (SprintId sprintId, Guid projectId, string baseCommit) =
            await SetUpIntegrationAsync(isolation, repository, environment, cancellationToken);
        AttemptId attemptId = AttemptId.New();
        await CreateAttemptWorktreeOrFailAsync(isolation, repository.Root, projectId, sprintId, attemptId, cancellationToken);
        string attemptPath = WorktreeLayout.AttemptPath(environment, projectId, sprintId, attemptId);

        // A first commit establishes the files the second one then modifies/deletes/renames; the
        // asserted range spans exactly those two commits, so every kind is genuinely reachable
        // (against the sprint's own base, a file created and then renamed inside the attempt is just
        // an addition, and the delete cancels out entirely).
        await File.WriteAllTextAsync(Path.Combine(attemptPath, "kept.txt"), "a\nb\nc\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(attemptPath, "gone.txt"), "x\n", cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(attemptPath, "before.txt"), "r1\nr2\nr3\nr4\nr5\n", cancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(attemptPath, "blob.bin"), [0x00, 0x01, 0x02, 0x03], cancellationToken);
        GitOperationResult seeded = await isolation.CommitAttemptAsync(
            repository.Root, projectId, sprintId, attemptId, "Seed", cancellationToken);
        AssertSucceeded(seeded);

        await File.WriteAllTextAsync(Path.Combine(attemptPath, "kept.txt"), "a\nb\nc\nd\n", cancellationToken);
        File.Delete(Path.Combine(attemptPath, "gone.txt"));
        File.Move(Path.Combine(attemptPath, "before.txt"), Path.Combine(attemptPath, "after.txt"));
        await File.WriteAllTextAsync(Path.Combine(attemptPath, "fresh.txt"), "new\n", cancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(attemptPath, "blob.bin"), [0x00, 0x01, 0x02, 0x03, 0x04], cancellationToken);
        GitOperationResult committed = await isolation.CommitAttemptAsync(
            repository.Root, projectId, sprintId, attemptId, "Change everything", cancellationToken);
        AssertSucceeded(committed);

        GitDiffStatResult result = await isolation.ReadDiffStatAsync(
            repository.Root, projectId, sprintId, attemptId, seeded.Commit!, committed.Commit!, cancellationToken);

        Assert.True(result.Succeeded, $"ReadDiffStatAsync failed: {result.DiagnosticCode} ({result.Detail})");
        DiffPayload stat = result.Stat!;
        Dictionary<string, DiffFileStat> byPath = stat.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        Assert.Equal(DiffChangeKinds.Modified, byPath["kept.txt"].ChangeKind);
        Assert.Equal(1, byPath["kept.txt"].Added);
        Assert.Equal(0, byPath["kept.txt"].Deleted);
        Assert.Equal(DiffChangeKinds.Deleted, byPath["gone.txt"].ChangeKind);
        Assert.Equal(1, byPath["gone.txt"].Deleted);
        Assert.Equal(DiffChangeKinds.Added, byPath["fresh.txt"].ChangeKind);
        Assert.Equal(DiffChangeKinds.Renamed, byPath["after.txt"].ChangeKind);
        Assert.DoesNotContain("before.txt", byPath.Keys, StringComparer.Ordinal);
        Assert.Equal(DiffChangeKinds.Binary, byPath["blob.bin"].ChangeKind);
        Assert.Equal(0, byPath["blob.bin"].Added);
        Assert.Equal(0, byPath["blob.bin"].Deleted);
        Assert.Equal(stat.Files.Count, stat.FilesChanged);
        Assert.Equal(0, stat.ElidedFiles);
        Assert.Equal(stat.Files.Sum(file => file.Added), stat.Insertions);
        Assert.Equal(stat.Files.Sum(file => file.Deleted), stat.Deletions);
    }

    /// <summary>ADR 0059's per-file cap. The totals must still describe the whole change: only the
    /// per-file rows are bounded, and every file beyond the bound is counted -- never silently
    /// dropped -- so a reader can always tell a genuinely small change from a truncated view of a
    /// large one.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadDiffStatCapsThePerFileListAndReportsTheRemainderAsElidedWithoutUnderreportingTotals()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using GitTestRepository repository = await GitTestRepository.CreateAsync(cancellationToken);
        using TestEnvironment environment = new();
        SprintGitIsolation isolation = environment.Resolve<SprintGitIsolation>();
        (SprintId sprintId, Guid projectId, string baseCommit) =
            await SetUpIntegrationAsync(isolation, repository, environment, cancellationToken);
        AttemptId attemptId = AttemptId.New();
        await CreateAttemptWorktreeOrFailAsync(isolation, repository.Root, projectId, sprintId, attemptId, cancellationToken);
        string attemptPath = WorktreeLayout.AttemptPath(environment, projectId, sprintId, attemptId);
        const int fileCount = GitWorktreeManagerDiffStatBudget.MaxFiles + 7;
        for (int index = 0; index < fileCount; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(attemptPath, $"file-{index:D3}.txt"), "one\ntwo\n", cancellationToken);
        }

        GitOperationResult committed = await isolation.CommitAttemptAsync(
            repository.Root, projectId, sprintId, attemptId, "Add many files", cancellationToken);
        AssertSucceeded(committed);

        GitDiffStatResult result = await isolation.ReadDiffStatAsync(
            repository.Root, projectId, sprintId, attemptId, baseCommit, committed.Commit!, cancellationToken);

        Assert.True(result.Succeeded, $"ReadDiffStatAsync failed: {result.DiagnosticCode} ({result.Detail})");
        DiffPayload stat = result.Stat!;
        Assert.Equal(GitWorktreeManagerDiffStatBudget.MaxFiles, stat.Files.Count);
        Assert.Equal(fileCount, stat.FilesChanged);
        Assert.Equal(fileCount - GitWorktreeManagerDiffStatBudget.MaxFiles, stat.ElidedFiles);
        Assert.Equal(fileCount * 2, stat.Insertions);
        Assert.Equal(0, stat.Deletions);
    }

    /// <summary>A `git` failure's own `stderr` (<see cref="GitOperationResult.Detail"/>) is what
    /// actually diagnoses a CI-only failure that cannot be reproduced locally; every success
    /// assertion in this file goes through this helper instead of a bare `Assert.True` so that text
    /// is never silently discarded on failure.</summary>
    private static void AssertSucceeded(
        GitOperationResult result,
        [CallerArgumentExpression(nameof(result))] string? expression = null) =>
        Assert.True(result.Succeeded, $"{expression} failed: {result.DiagnosticCode} ({result.Detail})");

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
        Assert.True(
            result.Succeeded, $"CreateAttemptWorktreeAsync failed: {result.DiagnosticCode} ({result.Detail})");
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
        Assert.True(result.Succeeded, $"EnsureIntegrationWorktreeAsync failed: {result.DiagnosticCode} ({result.Detail})");
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
