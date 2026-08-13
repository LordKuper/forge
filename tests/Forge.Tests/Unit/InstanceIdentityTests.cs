using Forge.Host.Client;

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
    public void PipeAndLeaseNamesForTheSameIdentityDiffer()
    {
        Guid projectId = Guid.NewGuid();

        Assert.NotEqual(
            InstanceIdentity.ComputePipeName("forge", projectId),
            InstanceIdentity.ComputeLeaseName("forge", projectId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EphemeralIdsAreUnique() =>
        Assert.NotEqual(InstanceIdentity.CreateEphemeral(), InstanceIdentity.CreateEphemeral());
}
