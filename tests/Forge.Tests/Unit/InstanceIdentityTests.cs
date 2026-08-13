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
    public void PipeAndLeaseNamesForTheSameProjectDiffer()
    {
        Guid projectId = Guid.NewGuid();

        Assert.NotEqual(
            InstanceIdentity.ComputePipeName("forge", projectId),
            InstanceIdentity.ComputeLeaseName(projectId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LeaseNamesAreTheSameAcrossEveryInstanceOfOneProject()
    {
        // ADR 0005: "Release, development, test, CLI, and Desktop instances use the same lease
        // namespace, so distinct instance data roots cannot become concurrent writers of one
        // `.forge/` tree." Unlike the pipe name, the lease name takes no instance id at all.
        Guid projectId = Guid.NewGuid();

        string lease = InstanceIdentity.ComputeLeaseName(projectId);

        Assert.Equal(lease, InstanceIdentity.ComputeLeaseName(projectId));
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
    public void EphemeralIdsAreUnique() =>
        Assert.NotEqual(InstanceIdentity.CreateEphemeral(), InstanceIdentity.CreateEphemeral());
}
