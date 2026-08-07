using System.Text.Json;
using Forge.Application;
using Forge.Domain;

namespace Forge.UnitTests;

/// <summary>
/// v0.9.0 stored every finding in one shared <c>findings.json</c>; the per-finding-file layout that
/// replaced it must migrate that legacy data instead of silently dropping it on first read.
/// </summary>
public sealed class LegacyFindingsMigrationTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReadingFindingsMigratesOpenAndResolvedEntriesFromTheLegacyFile()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        (Guid openId, Guid resolvedId) = WriteLegacyFindingsFile(
            root.Path, sprintId, ("finding.open", "open"), ("finding.resolved", "resolved"));

        IReadOnlyList<Finding> findings =
            await log.GetFindingsAsync(root.Path, sprintId, cancellationToken);

        Assert.Equal(2, findings.Count);
        Assert.Equal(FindingStatus.Open, findings.Single(item => item.FindingId == openId).Status);
        Assert.Equal(FindingStatus.Resolved, findings.Single(item => item.FindingId == resolvedId).Status);
        Assert.False(File.Exists(LegacyFindingsPath(root.Path, sprintId)));
        Assert.True(Directory.Exists(Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "findings")));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MigrationIsSafeToRunASecondTime()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        WriteLegacyFindingsFile(root.Path, sprintId, ("finding.a", "open"));

        await log.GetFindingsAsync(root.Path, sprintId, cancellationToken);
        // Nothing left to migrate (the legacy file is already gone); a second call must still be a
        // safe no-op, not an error, and must not duplicate the migrated finding.
        IReadOnlyList<Finding> second = await log.GetFindingsAsync(root.Path, sprintId, cancellationToken);

        Assert.Single(second);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnInterruptedMigrationCompletesCorrectlyOnRetry()
    {
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        (Guid firstId, Guid secondId) = WriteLegacyFindingsFile(
            root.Path, sprintId, ("finding.first", "open"), ("finding.second", "open"));

        // Simulate a crash mid-migration: one finding already landed in its own file, the legacy
        // file was never deleted (the crash happened before that last step).
        string findingsDirectory = Path.Combine(FileSprintEventLog.SprintDirectory(root.Path, sprintId), "findings");
        Directory.CreateDirectory(findingsDirectory);
        string legacyContent = await File.ReadAllTextAsync(LegacyFindingsPath(root.Path, sprintId), cancellationToken);
        using JsonDocument legacyDocument = JsonDocument.Parse(legacyContent);
        string firstEntryJson = legacyDocument.RootElement.GetProperty(firstId.ToString("D")).GetRawText();
        await File.WriteAllTextAsync(
            Path.Combine(findingsDirectory, $"{firstId:N}.json"), firstEntryJson, cancellationToken);

        IReadOnlyList<Finding> findings = await log.GetFindingsAsync(root.Path, sprintId, cancellationToken);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, item => item.FindingId == firstId);
        Assert.Contains(findings, item => item.FindingId == secondId);
        Assert.False(File.Exists(LegacyFindingsPath(root.Path, sprintId)));
    }

    private static string LegacyFindingsPath(string root, SprintId sprintId) =>
        Path.Combine(FileSprintEventLog.SprintDirectory(root, sprintId), "findings.json");

    /// <summary>Writes a v0.9.0-shaped shared <c>findings.json</c> directly, matching the wire shape
    /// that release's <c>FileSprintEventLog</c> produced, bypassing every current API on purpose.</summary>
    private static (Guid First, Guid Second) WriteLegacyFindingsFile(
        string root, SprintId sprintId, (string MessageKey, string Status) first, (string MessageKey, string Status)? second = null)
    {
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        Dictionary<string, object> entries = new(StringComparer.Ordinal)
        {
            [firstId.ToString("D")] = LegacyEntry(firstId, first.MessageKey, first.Status),
        };
        if (second is { } value)
        {
            entries[secondId.ToString("D")] = LegacyEntry(secondId, value.MessageKey, value.Status);
        }

        string directory = FileSprintEventLog.SprintDirectory(root, sprintId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "findings.json"), JsonSerializer.Serialize(entries));
        return (firstId, secondId);
    }

    private static object LegacyEntry(Guid findingId, string messageKey, string status) =>
        new
        {
            finding_id = findingId,
            fingerprint = "sha256:" + new string('a', 64),
            severity = "medium",
            status,
            message_key = messageKey,
            arguments = new Dictionary<string, string?>(),
            evidence = new[] { "src/Foo.cs:1" },
        };
}
