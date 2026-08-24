using System.Text.Json;
using Forge.Application;
using Forge.Presentation;

namespace Forge.AcceptanceTests;

/// <summary>
/// ADR 0053: <see cref="RemoteForgeMutations.CapabilityByKind"/> is hand-maintained rather than
/// loaded from `capabilities.json` at runtime (that file is a docs artifact, not wired into any
/// runtime code path; loading it here would be a materially larger, riskier change than the
/// negotiation-enforcement fix needs). This is the drift net that keeps the hand-written table
/// honest against the contract file instead, mirroring how <c>SurfaceParityTests</c> already loads
/// and cross-checks the same file for CLI/Desktop surface parity.
/// </summary>
public sealed class CapabilityNegotiationMappingTests
{
    [Fact]
    [Trait("Category", "Acceptance")]
    public void EveryMappedCapabilityIdExistsInTheCapabilitiesContract()
    {
        using JsonDocument contract = ReadCapabilities();
        HashSet<string> documented = DocumentedIds(contract);

        Assert.All(
            RemoteForgeMutations.CapabilityByKind.Values.Distinct(StringComparer.Ordinal),
            id => Assert.Contains(id, documented));
    }

    /// <summary>The drift catch this table exists for: every capability already in
    /// <see cref="CapabilityIds.Implemented"/> that also has its own wire <c>ControlRequest.Kind</c>
    /// -- i.e. every one <see cref="RemoteForgeMutations"/> could ever be asked to send over the
    /// control-plane connection -- must appear as one of the gate table's values. Someone who adds a
    /// new implemented capability with a real dispatch kind but forgets to gate it here fails this
    /// test, not silently ships unenforced negotiation. <see cref="CapabilityIds.ProjectInitialize"/>
    /// and <see cref="CapabilityIds.ProviderHealth"/> are the only two <c>Implemented</c> capabilities
    /// with no <c>ControlProtocol</c> kind at all -- initialization happens before a Host exists to
    /// dispatch anything (ADR 0005), and provider health is always answered from local state, never
    /// sent over this wire -- so they are named here as the sole, explicit exemption rather than
    /// silently inferred.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void EveryImplementedCapabilityWithADispatchKindIsGated()
    {
        string[] exempt = [CapabilityIds.ProjectInitialize, CapabilityIds.ProviderHealth];
        HashSet<string> gated = RemoteForgeMutations.CapabilityByKind.Values.ToHashSet(StringComparer.Ordinal);

        Assert.All(
            CapabilityIds.Implemented.Except(exempt),
            id => Assert.Contains(id, gated));
    }

    /// <summary>The other direction of the same drift check: a still-reserved capability
    /// (<c>capabilities.json</c>'s own `note` fields document these as implemented on Host/CLI/Desktop
    /// but deliberately withheld from <see cref="CapabilityIds.Implemented"/> as a separable cleanup)
    /// must never be gated -- doing so would reject a request against a Host that actually serves it
    /// today, purely because the capability-advertisement list has not caught up.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public void NoReservedCapabilityIsGated()
    {
        HashSet<string> implemented = CapabilityIds.Implemented.ToHashSet(StringComparer.Ordinal);

        Assert.All(
            RemoteForgeMutations.CapabilityByKind.Values.Distinct(StringComparer.Ordinal),
            id => Assert.Contains(id, implemented));
    }

    private static JsonDocument ReadCapabilities() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(
            Forge.UnitTests.RepositoryRoot.Find(),
            "docs",
            "contracts",
            "v1",
            "capabilities.json")));

    private static HashSet<string> DocumentedIds(JsonDocument contract) =>
        contract.RootElement
            .GetProperty("capabilities")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
}
