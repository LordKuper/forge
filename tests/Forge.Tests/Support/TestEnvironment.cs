using Forge.Application;
using Forge.Bootstrap;
using Forge.Configuration;
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
        IEnumerable<ILlmProvider>? llmProviders = null)
    {
        IPlatformPreflight preflight = platform ?? new SupportedPlatformPreflight();
        Root = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(LocalApplicationData);
        Directory.CreateDirectory(ProjectRoot);
        ServiceCollection services = new();
        services.AddForgeCore();
        services.AddSingleton<IEnvironmentPaths>(this);
        services.AddSingleton(preflight);
        // Registered so ProviderCatalog (resolved by ForgeApplication for write-time validation)
        // reflects a known set even though `providers` below overrides the actual toolchain manager.
        foreach (ILlmProvider llmProvider in llmProviders ?? [])
        {
            services.AddSingleton(llmProvider);
        }

        // Tests never touch the network or the real provider installation; a real toolchain
        // manager stays offline-safe by construction (it only discovers), but callers that need
        // a `ready` or `failed` toolchain override it explicitly.
        services.AddSingleton(providers ?? new FakeProviderToolchainManager());
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
            scheduler);
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

    public Task<ProviderToolchainStatus> EnsureReadyAsync(CancellationToken cancellationToken) =>
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

    public Task<ProviderStatus> DiscoverAsync(bool bypassReleaseCache, CancellationToken cancellationToken)
    {
        DiscoverCalls++;
        return Task.FromResult(new ProviderStatus(id, state, version, ProviderDiagnosticCodes.None));
    }

    public Task<ProviderStatus> InstallOrUpdateAsync(CancellationToken cancellationToken)
    {
        // Mirrors the real adapters: InstallOrUpdateAsync always probes first and only actually
        // mutates anything when that probe found a reason to (ProviderInstallation's own
        // conditional-update/install-repair policy).
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
        CancellationToken cancellationToken) =>
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
}
