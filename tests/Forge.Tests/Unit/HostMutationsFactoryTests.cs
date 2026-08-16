using Forge.Application;
using Forge.Configuration;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class HostMutationsFactoryTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateResolverFallsBackToTheLocalApplicationForAnUninitializedProject()
    {
        using TestEnvironment environment = new();
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations = HostMutationsFactory.CreateResolver(
            environment.Resolve<ProjectRootResolver>(),
            environment.Resolve<IConfigurationRegistry>(),
            environment,
            environment.Application,
            "1.0.0-test");

        IForgeMutations mutations = await resolveMutations(
            environment.ProjectRoot,
            TestContext.Current.CancellationToken);

        Assert.Same(environment.Application, mutations);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateResolverReturnsARemoteInstanceForAnInitializedProject()
    {
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations = HostMutationsFactory.CreateResolver(
            environment.Resolve<ProjectRootResolver>(),
            environment.Resolve<IConfigurationRegistry>(),
            environment,
            environment.Application,
            "1.0.0-test");

        IForgeMutations mutations = await resolveMutations(
            environment.ProjectRoot,
            TestContext.Current.CancellationToken);
        try
        {
            // Not the local application — a project with a real id gets a Host-routed instance,
            // never falling back silently.
            Assert.NotSame(environment.Application, mutations);
            Assert.IsType<RemoteForgeMutations>(mutations);
        }
        finally
        {
            if (mutations is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }
    }
}
