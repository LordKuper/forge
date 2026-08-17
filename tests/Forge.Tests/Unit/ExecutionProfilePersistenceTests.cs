using Forge.Application;
using Forge.Domain;

namespace Forge.UnitTests;

/// <summary>
/// <see cref="FileSprintEventLog"/>'s `ToPersisted`/`FromPersisted` mapping for
/// <see cref="ExecutionProfile"/> is exercised only through <see cref="ExecutionProfilePolicy.Freeze"/>'s
/// own, deliberately symmetric output elsewhere (planning/implementation share every field but
/// `Phase`) -- never with three profiles whose fields are all distinct, so a transposed field (e.g.
/// swapping `SessionDeadlineSeconds`/`IdleDeadlineSeconds`) would not fail any existing test. This
/// writes and reads back a full <see cref="SprintDefinition"/> built directly (bypassing
/// `ExecutionProfilePolicy` entirely) to catch exactly that class of mapping bug.
/// </summary>
public sealed class ExecutionProfilePersistenceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SavingAndLoadingADefinitionRoundTripsEveryExecutionProfileFieldExactly()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        IReadOnlyDictionary<ExecutionPhase, ExecutionProfile> profiles = new Dictionary<ExecutionPhase, ExecutionProfile>
        {
            [ExecutionPhase.Planning] = new(
                ExecutionProfile.ContractVersion, ExecutionPhase.Planning, "claude_code", "sonnet", "medium",
                "workspace-write", "never", [ContextCapabilityIds.GitShow], 1800, 180),
            [ExecutionPhase.Implementation] = new(
                ExecutionProfile.ContractVersion, ExecutionPhase.Implementation, "codex", "gpt-5", "low",
                "read-only", "always", [ContextCapabilityIds.GitGrep], 2400, 240),
            [ExecutionPhase.Review] = new(
                ExecutionProfile.ContractVersion, ExecutionPhase.Review, "claude_code", "opus", "high",
                "workspace-write", "never", [ContextCapabilityIds.GitShow, ContextCapabilityIds.GitGrep], 3600, 300,
                new ExecutionLineage("codex", "gpt-5", true)),
        };
        SprintDefinition definition = new(
            sprintId,
            new string('a', 40),
            "implementation-critical",
            "1.0.0",
            new Dictionary<string, string>(),
            [],
            [new("a", NodeKind.Work, [])],
            "en",
            "sha256:" + new string('0', 64),
            DateTimeOffset.UnixEpoch,
            ["claude_code", "codex"],
            profiles);

        await log.SaveDefinitionAsync(root.Path, definition, cancellationToken);
        SprintDefinition? reloaded = await log.LoadDefinitionAsync(root.Path, sprintId, cancellationToken);

        Assert.NotNull(reloaded);
        Assert.Equal(profiles.Keys, reloaded.ExecutionProfiles.Keys);
        foreach (ExecutionPhase phase in profiles.Keys)
        {
            // `ExecutionProfile`'s auto-generated record equality does not do structural comparison
            // on `CapabilityAllowlist` (an `IReadOnlyList<string>`, not overridden for equality),
            // so a naive whole-record `Assert.Equal` would fail even for a correct round trip.
            // Field-by-field, with `Assert.Equal`'s own `IEnumerable` sequence comparison for the
            // allowlist, is what actually verifies the mapping without a false negative.
            ExecutionProfile expected = profiles[phase];
            ExecutionProfile actual = reloaded.ExecutionProfiles[phase];
            Assert.Equal(expected.Provider, actual.Provider);
            Assert.Equal(expected.Model, actual.Model);
            Assert.Equal(expected.Effort, actual.Effort);
            Assert.Equal(expected.SandboxPolicy, actual.SandboxPolicy);
            Assert.Equal(expected.PermissionPolicy, actual.PermissionPolicy);
            Assert.Equal(expected.CapabilityAllowlist, actual.CapabilityAllowlist);
            Assert.Equal(expected.SessionDeadlineSeconds, actual.SessionDeadlineSeconds);
            Assert.Equal(expected.IdleDeadlineSeconds, actual.IdleDeadlineSeconds);
            Assert.Equal(expected.Lineage, actual.Lineage);
        }
    }
}
