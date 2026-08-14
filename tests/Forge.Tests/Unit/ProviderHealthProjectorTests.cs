using System.Text.Json;
using Forge.Application;
using Forge.Providers;
using Forge.Tests.Support;
using Json.Schema;

namespace Forge.UnitTests;

public sealed class ProviderHealthProjectorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void EveryDiscoveredProviderProjectsAsRegisteredAndEnabledWithNoUpdateOrAuthenticationDetermined()
    {
        ProviderToolchainStatus status = new(
        [
            ProviderStatus.Ready(new ProviderId("codex"), "0.146.0"),
            new(new ProviderId("claude_code"), ProviderState.Missing, null, ProviderDiagnosticCodes.Missing),
        ]);

        IReadOnlyList<ProviderHealthEntry> entries = ProviderHealthProjector.Project(status);

        ProviderHealthEntry codex = Assert.Single(entries, entry => entry.Id == "codex");
        Assert.True(codex.Registered);
        Assert.True(codex.Enabled);
        Assert.Equal(ProviderState.Ready, codex.State);
        Assert.Equal("0.146.0", codex.Version);
        Assert.Null(codex.UpdateAvailable);
        Assert.Null(codex.Authentication);
        Assert.Equal(ProviderDiagnosticCodes.None, codex.DiagnosticCode);

        ProviderHealthEntry claude = Assert.Single(entries, entry => entry.Id == "claude_code");
        Assert.Equal(ProviderState.Missing, claude.State);
        Assert.Equal(ProviderDiagnosticCodes.Missing, claude.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TheProjectedShapeSatisfiesTheVersionedProviderHealthContract()
    {
        ProviderToolchainStatus status = new(
        [
            ProviderStatus.Ready(new ProviderId("codex"), "0.146.0"),
            new(new ProviderId("claude_code"), ProviderState.Missing, null, ProviderDiagnosticCodes.Missing),
        ]);
        IReadOnlyList<ProviderHealthEntry> entries = ProviderHealthProjector.Project(status);
        string json = StatusJson.Serialize(entries);
        using JsonDocument instance = JsonDocument.Parse(
            $$"""{"schema_version":"1.0.0","providers":{{json}}}""");

        EvaluationResults result = ContractSchemas.Load("provider-health").Evaluate(
            instance.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });

        Assert.True(result.IsValid, JsonSerializer.Serialize(result));
    }
}
