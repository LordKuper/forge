namespace Forge.Application;

/// <summary>
/// The `diagnostic-bundle.schema.json` contract `forge doctor --bundle` (ADR 0005) will produce:
/// allowlisted, redacted operational evidence only. This stage (P8.48-P8.54) only versions the
/// contract shape; P12.1-P12.8 implements collection, redaction proof, and the CLI command that
/// produces one. <see cref="Omissions"/> names any section a caller could not safely collect or
/// redact — ADR 0005: "the bundle records that omission" rather than guessing at redaction.
/// </summary>
public sealed record DiagnosticBundle(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    string ForgeVersion,
    string ProtocolVersion,
    IReadOnlyList<DiagnosticProviderVersion> Providers,
    IReadOnlyList<StartupCheck> StartupChecks,
    DiagnosticProjectSummary Project,
    DiagnosticEventLogIntegrity EventLogIntegrity,
    DiagnosticWorktreeRegistrations WorktreeRegistrations,
    IReadOnlyList<DiagnosticCircuitBreaker> CircuitBreakers,
    DiagnosticRetryBudget RetryBudget,
    IReadOnlyList<DiagnosticWritableProbe> WritableProbes,
    IReadOnlyList<string> Omissions)
{
    public const string ContractVersion = "1.0.0";
}

public sealed record DiagnosticProviderVersion(string Id, string? Version);

public sealed record DiagnosticProjectSummary(bool Initialized, int SprintCount);

public sealed record DiagnosticEventLogIntegrity(bool Valid, string DiagnosticCode);

public sealed record DiagnosticWorktreeRegistrations(int Count, int OrphanedCount);

public enum DiagnosticCircuitState
{
    Closed,
    Open,
    HalfOpen,
}

public sealed record DiagnosticCircuitBreaker(string Key, DiagnosticCircuitState State);

public sealed record DiagnosticRetryBudget(int Total, int Remaining);

public sealed record DiagnosticWritableProbe(string Label, bool Writable);
