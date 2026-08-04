using Forge.Application;
using Forge.Bootstrap;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Tests.Support;

/// <summary>Isolated user and project locations so tests never touch the real profile.</summary>
internal sealed class TestEnvironment : IEnvironmentPaths, IDisposable
{
    private readonly ServiceProvider provider;

    public TestEnvironment(IPlatformPreflight? platform = null)
    {
        IPlatformPreflight preflight = platform ?? new SupportedPlatformPreflight();
        Root = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(LocalApplicationData);
        Directory.CreateDirectory(ProjectRoot);
        ServiceCollection services = new();
        services.AddForgeCore();
        services.AddSingleton<IEnvironmentPaths>(this);
        services.AddSingleton(preflight);
        provider = services.BuildServiceProvider();
    }

    public string Root { get; }

    public string LocalApplicationData => Path.Combine(Root, "local");

    public string ProjectRoot => Path.Combine(Root, "project");

    public string CurrentDirectory => ProjectRoot;

    public ForgeApplication Application => provider.GetRequiredService<ForgeApplication>();

    public T Resolve<T>()
        where T : notnull =>
        provider.GetRequiredService<T>();

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
