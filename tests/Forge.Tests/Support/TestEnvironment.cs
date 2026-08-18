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
        IProviderEnablementSource? providerEnablement = null)
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

/// <summary>Returns a fixed commit without running `git`.</summary>
internal sealed class FakeRepository(string? head = null) : IRepository
{
    private readonly string head = head ?? new string('a', 40);

    public Task<string> GetHeadAsync(string projectRoot, CancellationToken cancellationToken) =>
        Task.FromResult(head);
}

/// <summary>Like <see cref="FakeRepository"/>, but `Head` can change between calls — for tests that
/// need to prove a *later* read is never re-consulted (e.g. a resumed sprint creation must reuse its
/// already-frozen `baseCommit`, not re-read HEAD).</summary>
internal sealed class MutableRepository : IRepository
{
    public string Head { get; set; } = new string('a', 40);

    public Task<string> GetHeadAsync(string projectRoot, CancellationToken cancellationToken) =>
        Task.FromResult(Head);
}

/// <summary>Always fails, matching a project root that is not (or not yet) a Git repository.</summary>
internal sealed class UnavailableRepository : IRepository
{
    public Task<string> GetHeadAsync(string projectRoot, CancellationToken cancellationToken) =>
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

    public Task<ProviderToolchainStatus> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(status);

    public Task<ProviderToolchainStatus> EnsureReadyAsync(bool bypassReleaseCache, CancellationToken cancellationToken) =>
        Task.FromResult(status);
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

    public int SupersedeAttemptCalls { get; private set; }

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
        return Task.FromResult(new CompleteAttemptResult(true, null, DiagnosticCodes.None));
    }

    public int CreateSprintCalls { get; private set; }

    public int RunSprintCalls { get; private set; }

    public int ResumeSprintCalls { get; private set; }

    public int CancelSprintCalls { get; private set; }

    public bool? LastCancelSprintConfirmed { get; private set; }

    public Task<CreateSprintResult> CreateSprintAsync(string? projectRoot, CancellationToken cancellationToken)
    {
        CreateSprintCalls++;
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

    public Task<CreateSprintResult> CreateSprintAsync(string? projectRoot, CancellationToken cancellationToken) =>
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
