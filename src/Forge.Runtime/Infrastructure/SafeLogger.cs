using System.Text.Json;
using Forge.Application;

namespace Forge.Infrastructure;

/// <summary>
/// Stage 12's P12.1-P12.8 structured-logging slice: a redacted, append-only operational log,
/// persisted so it survives a headless Host process nobody watches on the console — the Host
/// already writes structured `ILogger` events (<c>ControlPlaneHostedService</c>'s
/// `LogListening`/etc.), but those go to JSON console output only and are gone once the terminal
/// that started the Host closes. This is deliberately a SEPARATE channel from the general
/// `ILogger` pipeline, not a blanket file provider capturing every category: only call sites that
/// explicitly build a property bag for <see cref="ISafeLogger"/> get persisted, so persistence
/// never silently extends to a log statement elsewhere that was never audited for
/// redaction-safety (AGENTS.md: "Never expose secrets or sensitive data in logs or errors").
/// Destination follows ADR 0005's own instance-namespaced convention (the same
/// `LocalApplicationData/Forge/&lt;InstanceId&gt;` root <see cref="ForgeApplication"/>'s writable-probe
/// check already treats as reserved for "user configuration, worktrees, and — once they exist —
/// logs/caches"), one compact JSON object per line, matching every other append-only log this
/// codebase already writes (e.g. <c>FileSprintEventLog</c>).
/// </summary>
public sealed class SafeLogger(IEnvironmentPaths paths) : ISafeLogger, IDisposable
{
    private const string FileName = "forge.jsonl";
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask InformationAsync(
        string eventName,
        IReadOnlyDictionary<string, object?> properties,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(properties);
        string line = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            event_name = eventName,
            properties = SecretRedactor.RedactProperties(properties),
        });
        string directory = LogDirectory(paths);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(
                Path.Combine(directory, FileName), line + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static string LogDirectory(IEnvironmentPaths paths) =>
        Path.Combine(paths.LocalApplicationData, "Forge", paths.InstanceId, "logs");

    public void Dispose() => gate.Dispose();
}
