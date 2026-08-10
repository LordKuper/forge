using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Configuration;
using Forge.Domain;

namespace Forge.Application;

/// <summary>
/// File-based <see cref="IRoutingStore"/>: one small JSON file per circuit breaker key, one small
/// JSON file for the sprint's shared retry budget, and an append-only <c>decisions.jsonl</c> for
/// route-decision history — the same atomic-write and per-path-lock conventions
/// <see cref="FileSprintEventLog"/> already uses for findings and node results.
/// </summary>
public sealed class FileRoutingStore : IRoutingStore
{
    private static readonly JsonSerializerOptions JsonOptions = ConfigurationSchemaCodec.SerializerOptions;

    // `JsonOptions` writes indented, multi-line JSON — fine for the one-value-per-file breaker and
    // budget records below, but fatal for `decisions.jsonl`, where each record must be exactly one
    // line for line-based reads to find record boundaries.
    private static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public async Task<CircuitBreakerRecord?> GetCircuitBreakerAsync(
        string projectRoot,
        SprintId sprintId,
        HealthKey key,
        CancellationToken cancellationToken)
    {
        string path = BreakerPath(projectRoot, sprintId, key);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        PersistedBreaker persisted = JsonSerializer.Deserialize<PersistedBreaker>(bytes, JsonOptions) ??
            throw new InvalidDataException($"The circuit breaker at '{path}' is empty.");
        return new(
            key,
            WorkflowStateNames.Parse<CircuitState>(persisted.State),
            persisted.ConsecutiveFailures,
            persisted.OpenedAt,
            persisted.CooldownUntil,
            persisted.UpdatedAt);
    }

    public async Task SaveCircuitBreakerAsync(
        string projectRoot,
        SprintId sprintId,
        CircuitBreakerRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        string path = BreakerPath(projectRoot, sprintId, record.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PersistedBreaker persisted = new()
        {
            Provider = record.Key.Provider,
            Model = record.Key.Model,
            Surface = record.Key.Surface,
            State = WorkflowStateNames.ToSnakeCase(record.State),
            ConsecutiveFailures = record.ConsecutiveFailures,
            OpenedAt = record.OpenedAt,
            CooldownUntil = record.CooldownUntil,
            UpdatedAt = record.UpdatedAt,
        };
        await WriteLockedAsync(
            path, JsonSerializer.SerializeToUtf8Bytes(persisted, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RetryBudgetRecord?> GetRetryBudgetAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        string path = BudgetPath(projectRoot, sprintId);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        PersistedBudget persisted = JsonSerializer.Deserialize<PersistedBudget>(bytes, JsonOptions) ??
            throw new InvalidDataException($"The retry budget at '{path}' is empty.");
        return new(sprintId, persisted.Total, persisted.Consumed);
    }

    public async Task SaveRetryBudgetAsync(
        string projectRoot,
        SprintId sprintId,
        RetryBudgetRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        string path = BudgetPath(projectRoot, sprintId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PersistedBudget persisted = new() { Total = record.Total, Consumed = record.Consumed };
        await WriteLockedAsync(
            path, JsonSerializer.SerializeToUtf8Bytes(persisted, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AppendRouteDecisionAsync(
        string projectRoot,
        RouteDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        string path = DecisionsPath(projectRoot, decision.SprintId);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        PersistedDecision persisted = new()
        {
            DecisionId = decision.DecisionId,
            NodeId = decision.NodeId,
            AttemptId = decision.AttemptId.Value.ToString("D"),
            Provider = decision.Key.Provider,
            Model = decision.Key.Model,
            Surface = decision.Key.Surface,
            Outcome = WorkflowStateNames.ToSnakeCase(decision.Outcome),
            FailureClass = decision.FailureClass is { } failureClass
                ? WorkflowStateNames.ToSnakeCase(failureClass)
                : null,
            DecidedAt = decision.DecidedAt,
        };
        JsonElement element = JsonSerializer.SerializeToElement(persisted, JsonOptions);
        string line = JsonSerializer.Serialize(element, CompactOptions);
        SemaphoreSlim gate = Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<RouteDecision>> GetRouteDecisionsAsync(
        string projectRoot,
        SprintId sprintId,
        CancellationToken cancellationToken)
    {
        string path = DecisionsPath(projectRoot, sprintId);
        if (!File.Exists(path))
        {
            return [];
        }

        List<RouteDecision> decisions = [];
        foreach (string line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (line.Length == 0)
            {
                continue;
            }

            PersistedDecision persisted = JsonSerializer.Deserialize<PersistedDecision>(line, JsonOptions) ??
                throw new InvalidDataException($"A route decision line at '{path}' is empty.");
            decisions.Add(new(
                persisted.DecisionId,
                sprintId,
                persisted.NodeId,
                new(Guid.Parse(persisted.AttemptId)),
                new(persisted.Provider, persisted.Model, persisted.Surface),
                WorkflowStateNames.Parse<RouteOutcome>(persisted.Outcome),
                persisted.FailureClass is { } failureClass
                    ? WorkflowStateNames.Parse<FailureClass>(failureClass)
                    : null,
                persisted.DecidedAt));
        }

        return decisions;
    }

    private static async Task WriteLockedAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicConfigurationFile.WriteAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A breaker key is provider/model/surface text, not a safe filename component — its
    /// digest is the file's stable, collision-resistant identity.</summary>
    private static string BreakerPath(string projectRoot, SprintId sprintId, HealthKey key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.Canonical));
        return Path.Combine(
            RoutingDirectory(projectRoot, sprintId), "breakers", $"{Convert.ToHexStringLower(hash)}.json");
    }

    private static string BudgetPath(string projectRoot, SprintId sprintId) =>
        Path.Combine(RoutingDirectory(projectRoot, sprintId), "retry-budget.json");

    private static string DecisionsPath(string projectRoot, SprintId sprintId) =>
        Path.Combine(RoutingDirectory(projectRoot, sprintId), "decisions.jsonl");

    private static string RoutingDirectory(string projectRoot, SprintId sprintId) =>
        Path.Combine(FileSprintEventLog.SprintDirectory(projectRoot, sprintId), "routing");

    private sealed class PersistedBreaker
    {
        public string Provider { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public string Surface { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? OpenedAt { get; set; }

        public DateTimeOffset? CooldownUntil { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class PersistedBudget
    {
        public int Total { get; set; }

        public int Consumed { get; set; }
    }

    private sealed class PersistedDecision
    {
        public Guid DecisionId { get; set; }

        public string NodeId { get; set; } = string.Empty;

        public string AttemptId { get; set; } = string.Empty;

        public string Provider { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public string Surface { get; set; } = string.Empty;

        public string Outcome { get; set; } = string.Empty;

        public string? FailureClass { get; set; }

        public DateTimeOffset DecidedAt { get; set; }
    }
}
