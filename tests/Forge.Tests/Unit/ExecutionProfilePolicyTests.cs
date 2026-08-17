using Forge.Application;
using Forge.Domain;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ExecutionProfilePolicyTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(NodeRole.Planning, ExecutionPhase.Planning)]
    [InlineData(NodeRole.Implementation, ExecutionPhase.Implementation)]
    [InlineData(NodeRole.Review, ExecutionPhase.Review)]
    public void PhaseForMapsModelRolesToTheirPhase(NodeRole role, ExecutionPhase expected)
    {
        Assert.Equal(expected, ExecutionProfilePolicy.PhaseFor(role));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(NodeRole.Generic)]
    [InlineData(NodeRole.Intake)]
    [InlineData(NodeRole.Confirmation)]
    [InlineData(NodeRole.TestWork)]
    [InlineData(NodeRole.HumanApproval)]
    [InlineData(NodeRole.Finalization)]
    public void PhaseForReturnsNullForNonModelRoles(NodeRole role)
    {
        Assert.Null(ExecutionProfilePolicy.PhaseFor(role));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectReviewProviderPrefersAnIndependentLineageWhenOneExists()
    {
        (string provider, bool achievedIndependence) =
            ExecutionProfilePolicy.SelectReviewProvider(["claude_code", "codex"], "claude_code");

        Assert.Equal("codex", provider);
        Assert.True(achievedIndependence);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectReviewProviderFallsBackToTheSameProviderWhenNoneIsIndependent()
    {
        (string provider, bool achievedIndependence) =
            ExecutionProfilePolicy.SelectReviewProvider(["claude_code"], "claude_code");

        Assert.Equal("claude_code", provider);
        Assert.False(achievedIndependence);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsCapabilityAllowedRejectsAHumanOnlyIdEvenIfPresentInTheAllowlist()
    {
        ExecutionProfile profile = new(
            ExecutionProfile.ContractVersion, ExecutionPhase.Review, "codex", "gpt-5", "high", "workspace-write",
            "never", [ContextCapabilityIds.GitShow, "workflow.review"], 3600, 300);

        Assert.False(ExecutionProfilePolicy.IsCapabilityAllowed(profile, "workflow.review"));
        Assert.True(ExecutionProfilePolicy.IsCapabilityAllowed(profile, ContextCapabilityIds.GitShow));
        Assert.False(ExecutionProfilePolicy.IsCapabilityAllowed(profile, ContextCapabilityIds.GitGrep));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FreezeProducesExactlyThreePhasesWithReviewLineageEvidence()
    {
        ProviderCatalog catalog = new(
            [
                new FakeLlmProvider(new ProviderId("claude_code"), ProviderState.Ready, "1.0.0"),
                new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0"),
            ]);

        IReadOnlyDictionary<ExecutionPhase, ExecutionProfile> profiles =
            ExecutionProfilePolicy.Freeze(["claude_code", "codex"], catalog);

        Assert.Equal(3, profiles.Count);
        Assert.Equal("claude_code", profiles[ExecutionPhase.Planning].Provider);
        Assert.Equal("claude_code", profiles[ExecutionPhase.Implementation].Provider);
        Assert.Equal("codex", profiles[ExecutionPhase.Review].Provider);
        Assert.Null(profiles[ExecutionPhase.Planning].Lineage);
        Assert.NotNull(profiles[ExecutionPhase.Review].Lineage);
        Assert.True(profiles[ExecutionPhase.Review].Lineage!.AchievedIndependence);
        Assert.Equal("claude_code", profiles[ExecutionPhase.Review].Lineage!.ImplementationProvider);
        Assert.All(
            profiles.Values,
            profile => Assert.Equal(
                [ContextCapabilityIds.GitShow, ContextCapabilityIds.GitGrep], profile.CapabilityAllowlist));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FreezeThrowsForAnEmptyProviderList()
    {
        ProviderCatalog catalog = new([]);

        Assert.Throws<ArgumentException>(() => ExecutionProfilePolicy.Freeze([], catalog));
    }
}
