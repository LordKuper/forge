using System.Globalization;
using Forge.Application;
using Forge.Bootstrap;
using Forge.Configuration;
using Forge.Domain;
using Forge.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Tests.Support;

/// <summary>Isolated user and project locations so tests never touch the real profile.</summary>
internal sealed class TestEnvironment : IEnvironmentPaths, IDisposable
{
    private readonly ServiceProvider provider;

    public TestEnvironment(
        IPlatformPreflight? platform = null,
        IProviderToolchainManager? providers = null,
        IRepository? repository = null,
        IEnumerable<ILlmProvider>? llmProviders = null,
        IProviderEnablementSource? providerEnablement = null,
        IEnumerable<IProviderIntegrationGenerator>? generators = null,
        IWorktreeManager? worktrees = null)
    {
        IPlatformPreflight preflight = platform ?? new SupportedPlatformPreflight();
        Root = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(LocalApplicationData);
        Directory.CreateDirectory(ProjectRoot);
        ServiceCollection services = new();
        services.AddForgeCore();
        services.AddSingleton<IEnvironmentPaths>(this);
        services.AddSingleton(preflight);
        // Registered so ProviderCatalog (resolved by ForgeApplication for write-time validation,
        // and by SprintOrchestrator to freeze a sprint's routing candidates) reflects a known set
        // even though `providers` below overrides the actual toolchain manager. Defaults to one
        // ready fake provider so sprint creation keeps working without every caller wiring one up;
        // pass an empty list explicitly to exercise the no-candidates-available path.
        foreach (ILlmProvider llmProvider in
            llmProviders ?? [new FakeLlmProvider(new ProviderId("fake"), ProviderState.Ready, "1.0.0")])
        {
            services.AddSingleton(llmProvider);
        }

        if (providers is not null)
        {
            services.AddSingleton(providers);
        }
        else if (llmProviders is null)
        {
            // No explicit fake providers were registered above (only the one default fake), and no
            // toolchain override was given either: default to a network/process-free toolchain
            // fake so a test that doesn't care about provider startup behavior at all keeps
            // working without wiring anything. A test that DOES register its own llmProviders (to
            // exercise real install/repair orchestration, e.g. through StartupPipeline) gets
            // AddForgeCore's real ProviderToolchainManager instead, backed by those providers.
            services.AddSingleton<IProviderToolchainManager>(new FakeProviderToolchainManager());
        }
        if (providerEnablement is not null)
        {
            // Overrides AddForgeCore's TryAddSingleton registration (last registration wins) so a
            // test can control which of the registered llmProviders are "enabled" without writing
            // through real user-scope configuration.
            services.AddSingleton(providerEnablement);
        }
        // Test project roots are plain temp directories, not Git repositories; a real
        // `git rev-parse HEAD` would fail there, so sprint creation gets a fixed fake commit
        // unless a test explicitly needs to exercise repository-unavailable behavior.
        services.AddSingleton(repository ?? new FakeRepository());
        // No default: the real generators (Claude/Codex) live in Windows-only OS-adapter projects
        // this neutral test composition never references (ADR 0007), so `IEnumerable<IProviderIntegrationGenerator>`
        // resolves empty unless a test opts in -- matching `forge integration skill generate`'s own
        // "no enabled provider has an integration generator" path by default.
        foreach (IProviderIntegrationGenerator generator in generators ?? [])
        {
            services.AddSingleton(generator);
        }

        // AddForgeCore's default is the real GitWorktreeManager (real `git.exe`, exercised against
        // an actual repository by GitIsolationTests); overridden only when a caller explicitly
        // wants SprintGitIsolation's own orchestration decoupled from a real subprocess -- the same
        // `repository` override pattern above, for the same reason.
        if (worktrees is not null)
        {
            services.AddSingleton(worktrees);
        }

        provider = services.BuildServiceProvider();
    }

    public string Root { get; }

    public string LocalApplicationData => Path.Combine(Root, "local");

    public string UserProfile => Path.Combine(Root, "userprofile");

    public string ProjectRoot => Path.Combine(Root, "project");

    public string CurrentDirectory => ProjectRoot;

    public string InstanceId => "forge-test";

    public ForgeApplication Application => provider.GetRequiredService<ForgeApplication>();

    public T Resolve<T>()
        where T : notnull =>
        provider.GetRequiredService<T>();

    /// <summary>Builds a fresh orchestrator/scheduler pair wired to a <see cref="FlakySprintStore"/>
    /// wrapping the real store, sharing every other real dependency from this environment's
    /// container — for tests that simulate a crash or a conflicting append mid compound operation.</summary>
    public (SprintOrchestrator Orchestrator, SprintScheduler Scheduler, FlakySprintStore Store) ResolveWithFlakyStore()
    {
        FlakySprintStore store = new(Resolve<ISprintStore>());
        SprintScheduler scheduler = new(store, Resolve<IClock>());
        SprintOrchestrator orchestrator = new(
            Resolve<ProjectRootResolver>(),
            store,
            Resolve<IConfigurationRegistry>(),
            Resolve<IRepository>(),
            Resolve<ScopedConfigurationService>(),
            Resolve<IClock>(),
            scheduler,
            Resolve<ProviderCatalog>(),
            Resolve<IProviderEnablementSource>());
        return (orchestrator, scheduler, store);
    }

    /// <summary>Builds a fresh <see cref="ForgeApplication"/> sharing every real dependency from this
    /// environment's container except <see cref="ISprintStore"/>, which is wrapped by
    /// <paramref name="decorate"/> — the same "swap one dependency, resolve the rest" technique as
    /// <see cref="ResolveWithFlakyStore"/>, but for the whole application entry point rather than just
    /// the orchestrator/scheduler pair. Exists so a test can drive an assertion through the actual
    /// method every surface calls (e.g. <see cref="ForgeApplication.GetSprintTimelineAsync"/>) instead
    /// of only through an internal collaborator it happens to call, which would not notice that
    /// collaborator being bypassed or its result being left unprocessed.</summary>
    public ForgeApplication ResolveApplicationWithSprintStore(Func<ISprintStore, ISprintStore> decorate)
    {
        ISprintStore decorated = decorate(Resolve<ISprintStore>());
        return new(
            Resolve<StartupPipeline>(),
            Resolve<ProjectRootResolver>(),
            Resolve<ProjectInitializer>(),
            Resolve<StartupRecovery>(),
            Resolve<StatusAdvisor>(),
            Resolve<IConfigurationRegistry>(),
            Resolve<ScopedConfigurationService>(),
            Resolve<IProviderToolchainManager>(),
            Resolve<ProviderCatalog>(),
            Resolve<ControlEventsReader>(),
            Resolve<IProviderEnablementSource>(),
            Resolve<IntegrationInstallationService>(),
            decorated,
            Resolve<SprintScheduler>(),
            Resolve<SprintOrchestrator>(),
            Resolve<IRepository>(),
            Resolve<RoutingLedger>(),
            Resolve<IWorktreeManager>(),
            Resolve<IEnvironmentPaths>(),
            Resolve<IFileSystem>(),
            Resolve<IClock>(),
            Resolve<StopOperationCoordinator>(),
            Resolve<ActiveOperationRegistry>(),
            Resolve<StageTransitionAssessor>(),
            Resolve<StageTransitionCoordinator>(),
            Resolve<WorkspaceSummaryProjector>(),
            new SprintTimelineProjector(decorated),
            Resolve<AvailableActionProjector>());
    }

    /// <summary>Dispatches initialization exactly like a surface: snapshot first, then command.</summary>
    public async Task<InitializeProjectResult> InitializeAsync(
        string? root,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ProjectSnapshot snapshot = await Application
            .GetProjectSnapshotAsync(root, cancellationToken)
            .ConfigureAwait(false);
        return await Application
            .InitializeProjectAsync(
                new(
                    root,
                    confirmed,
                    snapshot.StateVersion,
                    ForgeApplication.InitializationKey(snapshot)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        provider.Dispose();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
        catch (IOException)
        {
            // Temporary directories are reclaimed by the operating system.
        }
    }
}

internal sealed class SupportedPlatformPreflight : IPlatformPreflight
{
    public PlatformPreflightResult Check() =>
        new("windows", "x64", true, DiagnosticCodes.None);
}

/// <summary>Returns a fixed commit and branch without running `git`. <see cref="MergeResult"/>
/// controls what <see cref="MergeSprintIntoDefaultBranchAsync"/> reports (defaults to success), and
/// every call is recorded in <see cref="MergeCalls"/> so a test can assert what branch names a
/// finalize call actually presented.</summary>
internal sealed class FakeRepository(string? head = null, string? defaultBranch = "main") : IRepository
{
    private readonly string head = head ?? new string('a', 40);

    // Not `defaultBranch ?? "main"`: a caller passing `defaultBranch: null` explicitly (simulating a
    // detached HEAD) must actually get null, not have it silently coalesced back to "main" -- only
    // an *omitted* argument should default, which the parameter's own default value already handles.
    public string? DefaultBranch { get; set; } = defaultBranch;

    public GitOperationResult MergeResult { get; set; } = GitOperationResult.Ok(new string('b', 40));

    public List<(string DefaultBranch, string SourceBranch)> MergeCalls { get; } = [];

    public Task<string> GetHeadAsync(string projectRoot, CancellationToken cancellationToken) =>
        Task.FromResult(head);

    public Task<string?> GetCurrentBranchAsync(string projectRoot, CancellationToken cancellationToken) =>
        Task.FromResult(DefaultBranch);

    public Task<GitOperationResult> MergeSprintIntoDefaultBranchAsync(
        string projectRoot, string defaultBranch, string sourceBranch, CancellationToken cancellationToken)
    {
        MergeCalls.Add((defaultBranch, sourceBranch));
        return Task.FromResult(MergeResult);
    }
}

/// <summary>Like <see cref="FakeRepository"/>, but `Head` can change between calls — for tests that
/// need to prove a *later* read is never re-consulted (e.g. a resumed sprint creation must reuse its
/// already-frozen `baseCommit`, not re-read HEAD).</summary>
internal sealed class MutableRepository : IRepository
{
    public string Head { get; set; } = new string('a', 40);

    public string? CurrentBranch { get; set; } = "main";

    public Task<string> GetHeadAsync(string projectRoot, CancellationToken cancellationToken) =>
        Task.FromResult(Head);

    public Task<string?> GetCurrentBranchAsync(string projectRoot, CancellationToken cancellationToken) =>
        Task.FromResult(CurrentBranch);

    public Task<GitOperationResult> MergeSprintIntoDefaultBranchAsync(
        string projectRoot, string defaultBranch, string sourceBranch, CancellationToken cancellationToken) =>
        Task.FromResult(GitOperationResult.Ok(new string('b', 40)));
}

/// <summary>Always fails, matching a project root that is not (or not yet) a Git repository.</summary>
internal sealed class UnavailableRepository : IRepository
{
    public Task<string> GetHeadAsync(string projectRoot, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No repository is available in this test.");

    public Task<string?> GetCurrentBranchAsync(string projectRoot, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No repository is available in this test.");

    public Task<GitOperationResult> MergeSprintIntoDefaultBranchAsync(
        string projectRoot, string defaultBranch, string sourceBranch, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No repository is available in this test.");
}

/// <summary>Returns a fixed toolchain status without any network or process call.</summary>
internal sealed class FakeProviderToolchainManager(ProviderToolchainStatus? status = null)
    : IProviderToolchainManager
{
    private static readonly ProviderId Codex = new("codex");
    private static readonly ProviderId ClaudeCode = new("claude_code");

    public static ProviderToolchainStatus NotReady { get; } = new([
        new(Codex, ProviderState.Missing, null, ProviderDiagnosticCodes.Missing),
        new(ClaudeCode, ProviderState.Missing, null, ProviderDiagnosticCodes.Missing),
    ]);

    public static ProviderToolchainStatus Ready { get; } = new([
        ProviderStatus.Ready(Codex, "0.146.0") with { Authentication = ProviderAuthenticationStatus.Ready },
        ProviderStatus.Ready(ClaudeCode, "2.1.221") with { Authentication = ProviderAuthenticationStatus.Ready },
    ]);

    private readonly ProviderToolchainStatus status = status ?? NotReady;

    /// <summary>Counts every <see cref="CheckAsync"/> call -- distinct from
    /// <see cref="EnsureReadyCalls"/> -- so a test can prove a caller never issues the uncached
    /// discovery-plus-authentication probe <see cref="CheckAsync"/> represents (PR #100 review
    /// finding 1: <c>SidebarViewModel.LoadAsync</c> previously called it a second time per render on
    /// top of <see cref="EnsureReadyAsync"/>, which every startup/workspace-summary check already
    /// pays for).</summary>
    public int CheckCalls { get; private set; }

    public int EnsureReadyCalls { get; private set; }

    public Task<ProviderToolchainStatus> CheckAsync(CancellationToken cancellationToken)
    {
        CheckCalls++;
        return Task.FromResult(status);
    }

    public Task<ProviderToolchainStatus> EnsureReadyAsync(bool bypassReleaseCache, CancellationToken cancellationToken)
    {
        EnsureReadyCalls++;
        return Task.FromResult(status);
    }
}

/// <summary>A minimal, in-memory provider: reports a fixed state and counts install calls,
/// without ever touching the network or spawning a process. Reports authentication as ready by
/// default, since most callers only care about install/discovery orchestration; pass
/// <paramref name="authentication"/> explicitly to exercise an authentication-gated scenario.</summary>
internal sealed class FakeLlmProvider(
    ProviderId id,
    ProviderState state,
    string? version,
    ProviderAuthenticationStatus? authentication = null) : ILlmProvider
{
    public int InstallCalls { get; private set; }

    public int DiscoverCalls { get; private set; }

    public ProviderId Id => id;

    public string DefaultModel => $"{id.Value}-fake-model";

    public Task<ProviderStatus> DiscoverAsync(bool bypassReleaseCache, CancellationToken cancellationToken)
    {
        DiscoverCalls++;
        return Task.FromResult(new ProviderStatus(id, state, version, ProviderDiagnosticCodes.None));
    }

    public bool? LastInstallOrUpdateBypassedReleaseCache { get; private set; }

    public Task<ProviderStatus> InstallOrUpdateAsync(bool bypassReleaseCache, CancellationToken cancellationToken)
    {
        // Mirrors the real adapters: InstallOrUpdateAsync always probes first and only actually
        // mutates anything when that probe found a reason to (ProviderInstallation's own
        // conditional-update/install-repair policy).
        LastInstallOrUpdateBypassedReleaseCache = bypassReleaseCache;
        DiscoverCalls++;
        if (state == ProviderState.Ready)
        {
            return Task.FromResult(new ProviderStatus(id, state, version, ProviderDiagnosticCodes.None));
        }

        InstallCalls++;
        return Task.FromResult(ProviderStatus.Ready(id, "1.0.0"));
    }

    public Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<ProviderAuthenticationStatus> CheckAuthenticationAsync(CancellationToken cancellationToken) =>
        Task.FromResult(authentication ?? ProviderAuthenticationStatus.Ready);

    public Task<ProviderRunResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken,
        Func<AttemptActivityKind, CancellationToken, Task>? onActivity = null) =>
        throw new NotSupportedException("This fake only exercises discovery/install orchestration.");
}

/// <summary>Unlike <see cref="FakeLlmProvider"/> (which deliberately throws from
/// <see cref="RunAsync"/> — it exists only to exercise discovery/install orchestration), this fake
/// actually runs a caller-supplied delegate, for tests of a node executor that calls
/// <see cref="ILlmProvider.RunAsync"/> for real (Stage 11's planning executor). Records every
/// invocation's prompt and working directory so a test can assert on them without the delegate
/// itself needing to.</summary>
internal sealed class FakeRunnableLlmProvider(
    ProviderId id,
    Func<string, string, CancellationToken, Func<AttemptActivityKind, CancellationToken, Task>?, Task<ProviderRunResult>> run)
    : ILlmProvider
{
    public List<(string Prompt, string WorkingDirectory)> Calls { get; } = [];

    public ProviderId Id => id;

    public string DefaultModel => $"{id.Value}-fake-model";

    public Task<ProviderStatus> DiscoverAsync(bool bypassReleaseCache, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderStatus.Ready(id, "1.0.0"));

    public Task<ProviderStatus> InstallOrUpdateAsync(bool bypassReleaseCache, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderStatus.Ready(id, "1.0.0"));

    public Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<ProviderAuthenticationStatus> CheckAuthenticationAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ProviderAuthenticationStatus.Ready);

    public Task<ProviderRunResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken,
        Func<AttemptActivityKind, CancellationToken, Task>? onActivity = null)
    {
        Calls.Add((prompt, workingDirectory));
        return run(prompt, workingDirectory, cancellationToken, onActivity);
    }
}

/// <summary>An in-memory stand-in for real `git.exe` worktree operations (`GitIsolationTests`
/// already exercises the real thing) — for a test that needs to prove a caller's own orchestration
/// (which methods it calls, in what order, how it reacts to a failure) rather than git's actual
/// behavior. A worktree "exists" once <see cref="CreateAsync"/> succeeds for its path and stops
/// existing once <see cref="RemoveAsync"/> is called, matching the real manager's own contract
/// closely enough for that purpose.</summary>
internal sealed class FakeWorktreeManager : IWorktreeManager
{
    private readonly HashSet<string> paths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> heads = new(StringComparer.Ordinal);

    public List<string> CreatedPaths { get; } = [];

    public List<string> RemovedPaths { get; } = [];

    /// <summary>Persistent, not a one-shot latch: a caller that retries after a failure (e.g. an
    /// executor's bounded automatic retry) must keep observing the same failure until a test
    /// explicitly clears this, matching a genuinely broken environment (e.g. disk full) rather
    /// than a transient one.</summary>
    public bool FailNextCreate { get; set; }

    public string CreateFailureCode { get; set; } = DiagnosticCodes.WorktreeUnavailable;

    public Task<bool> ExistsAsync(string projectRoot, string path, CancellationToken cancellationToken) =>
        Task.FromResult(paths.Contains(path));

    public Task<GitOperationResult> CreateAsync(
        string projectRoot, string path, string branch, string commit, CancellationToken cancellationToken)
    {
        if (FailNextCreate)
        {
            return Task.FromResult(GitOperationResult.Fail(CreateFailureCode));
        }

        paths.Add(path);
        heads[path] = commit;
        CreatedPaths.Add(path);
        return Task.FromResult(GitOperationResult.Ok(commit));
    }

    public Task<bool> IsDirtyAsync(string projectRoot, string path, CancellationToken cancellationToken) =>
        Task.FromResult(Dirty.Contains(path));

    /// <summary>Paths <see cref="IsDirtyAsync"/> reports as dirty; empty by default, matching every
    /// fake worktree starting clean.</summary>
    public HashSet<string> Dirty { get; } = new(StringComparer.Ordinal);

    public List<(string Path, string Message)> Commits { get; } = [];

    public bool FailNextCommit { get; set; }

    public string CommitFailureCode { get; set; } = DiagnosticCodes.WorktreeCommitFailed;

    /// <summary>Optional side effect run immediately after a successful commit lands in
    /// <see cref="Commits"/> but before <see cref="CommitAllAsync"/> returns -- lets a test inject a
    /// concurrent mutation (PR #101 review finding 2: a stop that converges between
    /// <c>SprintGitIsolation.CommitAttemptAsync</c> succeeding and the caller's own next re-check)
    /// into that exact window deterministically, without a real race.</summary>
    public Func<CancellationToken, Task>? AfterCommitAll { get; set; }

    public async Task<GitOperationResult> CommitAllAsync(
        string projectRoot, string path, string message, CancellationToken cancellationToken)
    {
        if (FailNextCommit)
        {
            return GitOperationResult.Fail(CommitFailureCode);
        }

        Commits.Add((path, message));
        Dirty.Remove(path);
        // Fixed 40-hex-char length regardless of how large Commits.Count grows (a bare decimal
        // count concatenated onto a zero-padded suffix would overflow that length once the count
        // itself grows past a single digit) -- "c" (always a valid hex nibble) plus the count's own
        // hex digits, left-padded to fill the remaining 39 characters.
        string commit = "c" + Commits.Count.ToString("x", CultureInfo.InvariantCulture).PadLeft(39, '0');
        heads[path] = commit;
        if (AfterCommitAll is { } hook)
        {
            await hook(cancellationToken).ConfigureAwait(false);
        }

        return GitOperationResult.Ok(commit);
    }

    public Task<GitOperationResult> ResetHardAsync(
        string projectRoot, string path, string commit, CancellationToken cancellationToken) =>
        Task.FromResult(GitOperationResult.Ok(commit));

    /// <summary>Returned by every <see cref="DiffAsync"/> call; a test overrides it to exercise a
    /// specific diff a review executor's own prompt-building/parsing should see.</summary>
    public string Diff { get; set; } = "diff --git a/file.txt b/file.txt\n+changed";

    public bool FailNextDiff { get; set; }

    public string DiffFailureCode { get; set; } = DiagnosticCodes.WorktreeDiffFailed;

    /// <summary>A test overrides this to exercise the review executor's own handling of a
    /// budget-truncated diff (appending the "(truncated)" marker to its prompt) without needing a
    /// real diff 50,000 characters long -- the real truncation arithmetic itself belongs to
    /// <c>GitIsolationTests</c>, against real `git.exe`.</summary>
    public bool DiffTruncated { get; set; }

    public Task<GitDiffResult> DiffAsync(
        string projectRoot, string path, string fromCommit, string toCommit, CancellationToken cancellationToken) =>
        Task.FromResult(
            FailNextDiff ? GitDiffResult.Fail(DiffFailureCode) : GitDiffResult.Ok(Diff, DiffTruncated));

    public Task<string> GetHeadAsync(string projectRoot, string path, CancellationToken cancellationToken) =>
        heads.TryGetValue(path, out string? head)
            ? Task.FromResult(head)
            : throw new InvalidOperationException($"No fake worktree is registered at '{path}'.");

    public Task<GitOperationResult> IntegrateFastForwardAsync(
        string projectRoot, string path, string sourceBranch, CancellationToken cancellationToken) =>
        Task.FromResult(GitOperationResult.Ok(heads.GetValueOrDefault(path)));

    public Task<GitOperationResult> RebaseOntoAsync(
        string projectRoot, string path, string upstream, string ontoCommit, CancellationToken cancellationToken) =>
        Task.FromResult(GitOperationResult.Ok(ontoCommit));

    public Task<bool> RemoveAsync(string projectRoot, string path, CancellationToken cancellationToken)
    {
        bool removed = paths.Remove(path);
        heads.Remove(path);
        if (removed)
        {
            RemovedPaths.Add(path);
        }

        return Task.FromResult(true);
    }

    public Task<bool> DeleteBranchAsync(string projectRoot, string branch, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    // No separate "orphaned" concept: RemoveAsync above already unregisters a path the same moment
    // it removes it, so this fake has nothing corresponding to git's own registered-but-deleted
    // window. That distinction is only meaningfully testable against real git.exe (see
    // GitIsolationTests.cs/GitRepositoryMergeTests.cs's own precedent for this codebase).
    public Task<IReadOnlyList<WorktreeRegistration>> ListAsync(
        string projectRoot, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorktreeRegistration>>([.. paths.Select(path => new WorktreeRegistration(path, true))]);
}

/// <summary>A fixed, ordered `providers.enabled` selection — bypasses the real configuration
/// store so <see cref="ProviderToolchainManager"/> tests can control enablement directly.</summary>
internal sealed class FakeProviderEnablementSource(IReadOnlyList<string>? enabledIds) : IProviderEnablementSource
{
    public Task<IReadOnlyList<string>?> GetEnabledIdsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(enabledIds);
}

/// <summary>Stands in for a real Host connection (ADR 0005): counts calls and their arguments
/// without ever touching durable state, so a test can prove a mutation routed here — and not to
/// the local <see cref="ForgeApplication"/> — without a real Host process.</summary>
internal sealed class FakeForgeMutations : IForgeMutations
{
    public int RecoverStartupCalls { get; private set; }

    public int SetConfigurationCalls { get; private set; }

    public ConfigurationScope? LastScope { get; private set; }

    public int InstallIntegrationCalls { get; private set; }

    public int RemoveIntegrationCalls { get; private set; }

    public bool? LastIntegrationConfirmed { get; private set; }

    public Task<RecoverStartupResult> RecoverStartupAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        RecoverStartupCalls++;
        return Task.FromResult(new RecoverStartupResult(true, null, DiagnosticCodes.None));
    }

    public Task<ConfigurationWriteResult> SetConfigurationAsync(
        ConfigurationScope scope,
        string? projectRoot,
        string key,
        string? rawValue,
        CancellationToken cancellationToken)
    {
        SetConfigurationCalls++;
        LastScope = scope;
        return Task.FromResult(ConfigurationWriteResult.Success);
    }

    public Task<IntegrationWriteResult> InstallIntegrationAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        InstallIntegrationCalls++;
        LastIntegrationConfirmed = confirmed;
        return Task.FromResult(IntegrationWriteResult.Empty(DiagnosticCodes.None));
    }

    public Task<IntegrationWriteResult> RemoveIntegrationAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        RemoveIntegrationCalls++;
        LastIntegrationConfirmed = confirmed;
        return Task.FromResult(IntegrationWriteResult.Empty(DiagnosticCodes.None));
    }

    public int ResolveGateCalls { get; private set; }

    public bool? LastGateApproved { get; private set; }

    public bool? LastGateConfirmed { get; private set; }

    public string? LastGateNodeId { get; private set; }

    public Guid? LastGateSprintId { get; private set; }

    public int SupersedeAttemptCalls { get; private set; }

    public Guid? LastSupersedeSprintId { get; private set; }

    public Guid? LastSupersedeAttemptId { get; private set; }

    public string? LastSupersedeInstruction { get; private set; }

    public bool? LastSupersedeConfirmed { get; private set; }

    public Task<NodeActionResult> ResolveGateAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        bool approved,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ResolveGateCalls++;
        LastGateApproved = approved;
        LastGateConfirmed = confirmed;
        LastGateNodeId = nodeId;
        LastGateSprintId = sprintId;
        return Task.FromResult(new NodeActionResult(true, null, DiagnosticCodes.None));
    }

    public Task<CompleteAttemptResult> SupersedeAttemptAsync(
        string? projectRoot,
        Guid sprintId,
        Guid attemptId,
        string instruction,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        SupersedeAttemptCalls++;
        LastSupersedeSprintId = sprintId;
        LastSupersedeAttemptId = attemptId;
        LastSupersedeInstruction = instruction;
        LastSupersedeConfirmed = confirmed;
        return Task.FromResult(new CompleteAttemptResult(true, null, DiagnosticCodes.None));
    }

    public int PostSprintMessageCalls { get; private set; }

    public Guid? LastMessageSprintId { get; private set; }

    public string? LastMessageText { get; private set; }

    public Task<PostSprintMessageResult> PostSprintMessageAsync(
        string? projectRoot, Guid sprintId, string text, CancellationToken cancellationToken)
    {
        PostSprintMessageCalls++;
        LastMessageSprintId = sprintId;
        LastMessageText = text;
        return Task.FromResult(new PostSprintMessageResult(true, null, DiagnosticCodes.None));
    }

    public int StopCurrentOperationCalls { get; private set; }

    public Guid? LastStopSprintId { get; private set; }

    public Guid? LastStopAttemptId { get; private set; }

    public bool? LastStopConfirmed { get; private set; }

    public Task<StopOperationResult> StopCurrentOperationAsync(
        string? projectRoot,
        Guid sprintId,
        Guid attemptId,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        StopCurrentOperationCalls++;
        LastStopSprintId = sprintId;
        LastStopAttemptId = attemptId;
        LastStopConfirmed = confirmed;
        return Task.FromResult(new StopOperationResult(true, DiagnosticCodes.None));
    }

    public int MoveSprintToStageCalls { get; private set; }

    public Guid? LastMoveStageSprintId { get; private set; }

    public string? LastMoveStageTargetStageId { get; private set; }

    public long? LastMoveStageExpectedStateVersion { get; private set; }

    public string? LastMoveStageAssessmentToken { get; private set; }

    public string? LastMoveStageReason { get; private set; }

    public bool? LastMoveStageConfirmed { get; private set; }

    public Guid? LastMoveStageIdempotencyKey { get; private set; }

    public Task<MoveStageResult> MoveSprintToStageAsync(
        string? projectRoot,
        Guid sprintId,
        string targetStageId,
        long expectedStateVersion,
        string? assessmentToken,
        string? reason,
        bool confirmed,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        MoveSprintToStageCalls++;
        LastMoveStageSprintId = sprintId;
        LastMoveStageTargetStageId = targetStageId;
        LastMoveStageExpectedStateVersion = expectedStateVersion;
        LastMoveStageAssessmentToken = assessmentToken;
        LastMoveStageReason = reason;
        LastMoveStageConfirmed = confirmed;
        LastMoveStageIdempotencyKey = idempotencyKey;
        return Task.FromResult(new MoveStageResult(true, null, null, DiagnosticCodes.None));
    }

    public int ConfirmNodeCalls { get; private set; }

    public ConfirmationOutcome? LastConfirmOutcome { get; private set; }

    public bool? LastConfirmConfirmed { get; private set; }

    public string? LastConfirmNodeId { get; private set; }

    public Guid? LastConfirmSprintId { get; private set; }

    public IReadOnlyList<ConfirmationEvidence>? LastConfirmEvidence { get; private set; }

    public Task<RecordConfirmationResult> ConfirmNodeAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        ConfirmationOutcome outcome,
        string definitionOfDone,
        IReadOnlyList<ConfirmationEvidence> evidence,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ConfirmNodeCalls++;
        LastConfirmOutcome = outcome;
        LastConfirmConfirmed = confirmed;
        LastConfirmNodeId = nodeId;
        LastConfirmSprintId = sprintId;
        LastConfirmEvidence = evidence;
        return Task.FromResult(new RecordConfirmationResult(true, null, DiagnosticCodes.None));
    }

    public int RecordTestWorkCalls { get; private set; }

    public TestWorkOutcome? LastTestWorkOutcome { get; private set; }

    public bool? LastTestWorkConfirmed { get; private set; }

    public string? LastTestWorkNodeId { get; private set; }

    public Guid? LastTestWorkSprintId { get; private set; }

    public Task<RecordTestWorkResult> RecordTestWorkAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        TestWorkOutcome outcome,
        string justification,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        RecordTestWorkCalls++;
        LastTestWorkOutcome = outcome;
        LastTestWorkConfirmed = confirmed;
        LastTestWorkNodeId = nodeId;
        LastTestWorkSprintId = sprintId;
        return Task.FromResult(new RecordTestWorkResult(true, null, DiagnosticCodes.None));
    }

    public int FinalizeSprintCalls { get; private set; }

    public bool? LastFinalizeConfirmed { get; private set; }

    public string? LastFinalizeNodeId { get; private set; }

    public Guid? LastFinalizeSprintId { get; private set; }

    public Task<FinalizeSprintResult> FinalizeSprintAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        FinalizeSprintCalls++;
        LastFinalizeConfirmed = confirmed;
        LastFinalizeNodeId = nodeId;
        LastFinalizeSprintId = sprintId;
        return Task.FromResult(new FinalizeSprintResult(true, null, null, DiagnosticCodes.None));
    }

    public int CreateSprintCalls { get; private set; }

    public int RunSprintCalls { get; private set; }

    public int ResumeSprintCalls { get; private set; }

    public int CancelSprintCalls { get; private set; }

    public bool? LastCancelSprintConfirmed { get; private set; }

    public string? LastCreateSprintTitle { get; private set; }

    public Task<CreateSprintResult> CreateSprintAsync(
        string? projectRoot, string? title, CancellationToken cancellationToken)
    {
        CreateSprintCalls++;
        LastCreateSprintTitle = title;
        return Task.FromResult(new CreateSprintResult(true, new(Guid.NewGuid()), DiagnosticCodes.None));
    }

    public Task<SprintTransitionResult> RunSprintAsync(
        string? projectRoot, Guid sprintId, CancellationToken cancellationToken)
    {
        RunSprintCalls++;
        return Task.FromResult(new SprintTransitionResult(true, null, DiagnosticCodes.None));
    }

    public Task<SprintTransitionResult> ResumeSprintAsync(
        string? projectRoot, Guid sprintId, CancellationToken cancellationToken)
    {
        ResumeSprintCalls++;
        return Task.FromResult(new SprintTransitionResult(true, null, DiagnosticCodes.None));
    }

    public Task<SprintTransitionResult> CancelSprintAsync(
        string? projectRoot, Guid sprintId, bool confirmed, CancellationToken cancellationToken)
    {
        CancelSprintCalls++;
        LastCancelSprintConfirmed = confirmed;
        return Task.FromResult(new SprintTransitionResult(true, null, DiagnosticCodes.None));
    }
}

/// <summary>Like <see cref="FakeForgeMutations"/>, but disposable — proves a caller that resolves a
/// disposable <see cref="IForgeMutations"/> (standing in for a real <c>RemoteForgeMutations</c>) actually
/// disposes it.</summary>
internal sealed class DisposableFakeForgeMutations : IForgeMutations, IAsyncDisposable
{
    public int DisposeCalls { get; private set; }

    public Task<RecoverStartupResult> RecoverStartupAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken) =>
        Task.FromResult(new RecoverStartupResult(true, null, DiagnosticCodes.None));

    public Task<ConfigurationWriteResult> SetConfigurationAsync(
        ConfigurationScope scope,
        string? projectRoot,
        string key,
        string? rawValue,
        CancellationToken cancellationToken) =>
        Task.FromResult(ConfigurationWriteResult.Success);

    public Task<IntegrationWriteResult> InstallIntegrationAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken) =>
        Task.FromResult(IntegrationWriteResult.Empty(DiagnosticCodes.None));

    public Task<IntegrationWriteResult> RemoveIntegrationAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken) =>
        Task.FromResult(IntegrationWriteResult.Empty(DiagnosticCodes.None));

    public Task<NodeActionResult> ResolveGateAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        bool approved,
        bool confirmed,
        CancellationToken cancellationToken) =>
        Task.FromResult(new NodeActionResult(true, null, DiagnosticCodes.None));

    public Task<CompleteAttemptResult> SupersedeAttemptAsync(
        string? projectRoot,
        Guid sprintId,
        Guid attemptId,
        string instruction,
        bool confirmed,
        CancellationToken cancellationToken) =>
        Task.FromResult(new CompleteAttemptResult(true, null, DiagnosticCodes.None));

    public Task<PostSprintMessageResult> PostSprintMessageAsync(
        string? projectRoot, Guid sprintId, string text, CancellationToken cancellationToken) =>
        Task.FromResult(new PostSprintMessageResult(true, null, DiagnosticCodes.None));

    public Task<StopOperationResult> StopCurrentOperationAsync(
        string? projectRoot,
        Guid sprintId,
        Guid attemptId,
        bool confirmed,
        CancellationToken cancellationToken) =>
        Task.FromResult(new StopOperationResult(true, DiagnosticCodes.None));

    public Task<MoveStageResult> MoveSprintToStageAsync(
        string? projectRoot,
        Guid sprintId,
        string targetStageId,
        long expectedStateVersion,
        string? assessmentToken,
        string? reason,
        bool confirmed,
        Guid idempotencyKey,
        CancellationToken cancellationToken) =>
        Task.FromResult(new MoveStageResult(true, null, null, DiagnosticCodes.None));

    public Task<RecordConfirmationResult> ConfirmNodeAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        ConfirmationOutcome outcome,
        string definitionOfDone,
        IReadOnlyList<ConfirmationEvidence> evidence,
        bool confirmed,
        CancellationToken cancellationToken) =>
        Task.FromResult(new RecordConfirmationResult(true, null, DiagnosticCodes.None));

    public Task<RecordTestWorkResult> RecordTestWorkAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        TestWorkOutcome outcome,
        string justification,
        bool confirmed,
        CancellationToken cancellationToken) =>
        Task.FromResult(new RecordTestWorkResult(true, null, DiagnosticCodes.None));

    public Task<FinalizeSprintResult> FinalizeSprintAsync(
        string? projectRoot,
        Guid sprintId,
        string nodeId,
        bool confirmed,
        CancellationToken cancellationToken) =>
        Task.FromResult(new FinalizeSprintResult(true, null, null, DiagnosticCodes.None));

    public Task<CreateSprintResult> CreateSprintAsync(
        string? projectRoot, string? title, CancellationToken cancellationToken) =>
        Task.FromResult(new CreateSprintResult(true, new(Guid.NewGuid()), DiagnosticCodes.None));

    public Task<SprintTransitionResult> RunSprintAsync(
        string? projectRoot, Guid sprintId, CancellationToken cancellationToken) =>
        Task.FromResult(new SprintTransitionResult(true, null, DiagnosticCodes.None));

    public Task<SprintTransitionResult> ResumeSprintAsync(
        string? projectRoot, Guid sprintId, CancellationToken cancellationToken) =>
        Task.FromResult(new SprintTransitionResult(true, null, DiagnosticCodes.None));

    public Task<SprintTransitionResult> CancelSprintAsync(
        string? projectRoot, Guid sprintId, bool confirmed, CancellationToken cancellationToken) =>
        Task.FromResult(new SprintTransitionResult(true, null, DiagnosticCodes.None));

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        return ValueTask.CompletedTask;
    }
}
