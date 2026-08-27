using Forge.Providers;

namespace Forge.UnitTests;

/// <summary>
/// The clamping rule ADR 0062 introduced, exercised on its own rather than through either adapter:
/// `execution-profile.schema.json` types `effort` as any non-empty string, and neither vendor CLI
/// rejects a level it does not offer, so this is the only place that decides what a provider
/// process actually receives.
/// </summary>
public sealed class ProviderEffortLevelsTests
{
    private static readonly IReadOnlyList<string> ClaudeLevels = ["low", "medium", "high", "xhigh", "max"];
    private static readonly IReadOnlyList<string> CodexLevels = ["low", "medium", "high", "xhigh"];

    [Theory]
    [Trait("Category", "Unit")]
    // The two levels ExecutionProfilePolicy actually freezes today reach both vendors unchanged.
    [InlineData("medium", "medium", "medium")]
    [InlineData("high", "high", "high")]
    // Below the lowest level a vendor offers: clamps up to that lowest level rather than being
    // dropped, since the policy did ask for the cheapest run available.
    [InlineData("none", "low", "low")]
    [InlineData("minimal", "low", "low")]
    // Above the highest level a vendor offers: clamps down, never past it.
    [InlineData("ultra", "max", "xhigh")]
    [InlineData("max", "max", "xhigh")]
    // Vocabulary neither vendor's ladder contains is never approximated -- no flag is sent at all,
    // because Codex would forward the garbage verbatim to its API and Claude would silently revert
    // to its own default while Forge believed otherwise.
    [InlineData("aggressive", null, null)]
    [InlineData("", null, null)]
    [InlineData("   ", null, null)]
    // Casing and surrounding whitespace are normalized, not treated as unknown vocabulary.
    [InlineData(" HIGH ", "high", "high")]
    public void ResolveMapsAFrozenLevelOntoWhatEachVendorActuallyAccepts(
        string effort, string? expectedForClaude, string? expectedForCodex)
    {
        Assert.Equal(expectedForClaude, ProviderEffortLevels.Resolve(effort, ClaudeLevels));
        Assert.Equal(expectedForCodex, ProviderEffortLevels.Resolve(effort, CodexLevels));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveTreatsAnAbsentLevelAsLeaveTheVendorDefaultAlone()
    {
        Assert.Null(ProviderEffortLevels.Resolve(null, ClaudeLevels));
        Assert.Null(ProviderEffortLevels.Resolve(null, CodexLevels));
    }
}
