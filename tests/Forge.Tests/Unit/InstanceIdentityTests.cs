using Forge.Host.Client;
using Forge.Infrastructure;

namespace Forge.UnitTests;

public sealed class InstanceIdentityTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void PipeNamesAreDeterministicAndShort()
    {
        Guid projectId = Guid.NewGuid();

        string first = InstanceIdentity.ComputePipeName("forge", projectId);
        string second = InstanceIdentity.ComputePipeName("forge", projectId);

        Assert.Equal(first, second);
        Assert.True(first.Length <= 32, $"'{first}' should be short enough for a Unix-domain-socket path.");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DifferentInstancesProduceDifferentNames()
    {
        Guid projectId = Guid.NewGuid();

        Assert.NotEqual(
            InstanceIdentity.ComputePipeName("forge", projectId),
            InstanceIdentity.ComputePipeName("forge-dev", projectId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DifferentProjectsProduceDifferentNames()
    {
        Assert.NotEqual(
            InstanceIdentity.ComputePipeName("forge", Guid.NewGuid()),
            InstanceIdentity.ComputePipeName("forge", Guid.NewGuid()));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PipeAndLeaseNamesForTheSameProjectDiffer()
    {
        Guid projectId = Guid.NewGuid();

        Assert.NotEqual(
            InstanceIdentity.ComputePipeName("forge", projectId),
            InstanceIdentity.ComputeLeaseName(projectId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LeaseNamesDifferOnlyByProject()
    {
        Assert.NotEqual(
            InstanceIdentity.ComputeLeaseName(Guid.NewGuid()),
            InstanceIdentity.ComputeLeaseName(Guid.NewGuid()));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SystemEnvironmentPathsDefaultInstanceIdMatchesInstanceIdentityDefault()
    {
        // SystemEnvironmentPaths (Forge.Runtime) duplicates the release/Debug instance-id constants
        // this type owns, since Forge.Runtime deliberately takes no dependency on this leaf project.
        // This is the guard that catches the two ever drifting apart: a composition root that
        // resolves IEnvironmentPaths through AddForgeCore's default (rather than an explicit
        // Forge.Host-style override) must land on exactly the same instance id InstanceIdentity
        // itself would compute pipe/lease names for.
        SystemEnvironmentPaths paths = new();

        Assert.Equal(InstanceIdentity.Default, paths.InstanceId);
    }
}
