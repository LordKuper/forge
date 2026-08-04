using Forge.Application;
using Forge.Bootstrap;
using Forge.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Tests.Support;

/// <summary>Isolated user and project locations so tests never touch the real profile.</summary>
internal sealed class TestEnvironment : IEnvironmentPaths, IDisposable
{
    private readonly ServiceProvider provider;

    public TestEnvironment(IPlatformPreflight? platform = null, IProviderToolchainManager? providers = null)
    {
        IPlatformPreflight preflight = platform ?? new SupportedPlatformPreflight();
        Root = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(LocalApplicationData);
        Directory.CreateDirectory(ProjectRoot);
        ServiceCollection services = new();
        services.AddForgeCore();
        services.AddSingleton<IEnvironmentPaths>(this);
        services.AddSingleton(preflight);
        // Tests never touch the network or the real provider installation; a real toolchain
        // manager stays offline-safe by construction (it only discovers), but callers that need
        // a `ready` or `failed` toolchain override it explicitly.
        services.AddSingleton(providers ?? new FakeProviderToolchainManager());
        provider = services.BuildServiceProvider();
    }

    public string Root { get; }

    public string LocalApplicationData => Path.Combine(Root, "local");

    public string UserProfile => Path.Combine(Root, "userprofile");

    public string ProjectRoot => Path.Combine(Root, "project");

    public string CurrentDirectory => ProjectRoot;

    public ForgeApplication Application => provider.GetRequiredService<ForgeApplication>();

    public T Resolve<T>()
        where T : notnull =>
        provider.GetRequiredService<T>();

    /// <summary>Dispatches initialization exactly like a surface: snapshot first, then command.</summary>
    public async Task<InitializeProjectResult> InitializeAsync(
        string? root,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        ProjectStatusSnapshot snapshot = await Application
            .GetProjectStatusAsync(root, cancellationToken)
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

/// <summary>Returns a fixed toolchain status without any network or process call.</summary>
internal sealed class FakeProviderToolchainManager(ProviderToolchainStatus? status = null)
    : IProviderToolchainManager
{
    public static ProviderToolchainStatus NotReady { get; } = new([
        new(ProviderKind.Codex, ProviderState.Missing, null, ProviderDiagnosticCodes.Missing),
        new(ProviderKind.ClaudeCode, ProviderState.Missing, null, ProviderDiagnosticCodes.Missing),
    ]);

    public static ProviderToolchainStatus Ready { get; } = new([
        ProviderStatus.Ready(ProviderKind.Codex, "0.146.0"),
        ProviderStatus.Ready(ProviderKind.ClaudeCode, "2.1.221"),
    ]);

    private readonly ProviderToolchainStatus status = status ?? NotReady;

    public Task<ProviderToolchainStatus> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(status);

    public Task<ProviderToolchainStatus> EnsureReadyAsync(CancellationToken cancellationToken) =>
        Task.FromResult(status);
}
