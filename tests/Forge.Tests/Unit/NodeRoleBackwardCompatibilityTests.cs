using System.Text.Json;
using Forge.Application;
using Forge.Domain;

namespace Forge.UnitTests;

/// <summary>
/// A sprint frozen before <see cref="NodeRole"/> existed has no `role` field in its durable
/// `definition.json`. Reading it back must default the node to <see cref="NodeRole.Generic"/>
/// rather than fail — the same tolerant-of-older-data expectation
/// <c>LegacyFindingsMigrationTests</c> already sets for the pre-per-file findings layout.
/// </summary>
public sealed class NodeRoleBackwardCompatibilityTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadingADefinitionWithNoRoleFieldDefaultsEveryNodeToGeneric()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        WriteLegacyDefinitionFile(root.Path, sprintId);

        SprintDefinition? definition = await log.LoadDefinitionAsync(root.Path, sprintId, cancellationToken);

        Assert.NotNull(definition);
        Assert.Equal(NodeRole.Generic, definition.Graph.Single(node => node.Id == "a").Role);
    }

    private static void WriteLegacyDefinitionFile(string root, SprintId sprintId)
    {
        string directory = FileSprintEventLog.SprintDirectory(root, sprintId);
        Directory.CreateDirectory(directory);
        object legacy = new
        {
            base_commit = new string('a', 40),
            workflow = "implementation-critical",
            workflow_version = "1.0.0",
            configuration_snapshot = new Dictionary<string, string>(),
            dependencies = Array.Empty<object>(),
            graph = new[] { new { id = "a", kind = "work", depends_on = Array.Empty<string>() } },
            conversation_language = "en",
            artifact_policy_snapshot_hash = "sha256:" + new string('0', 64),
            frozen_at = DateTimeOffset.UnixEpoch,
            frozen_providers = new[] { "codex" },
        };
        File.WriteAllText(Path.Combine(directory, "definition.json"), JsonSerializer.Serialize(legacy));
    }
}
