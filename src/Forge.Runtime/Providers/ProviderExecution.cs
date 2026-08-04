using System.Text.Json;
using Forge.Application;
using Forge.Infrastructure;

namespace Forge.Providers;

/// <summary>A normalized view over a provider-specific JSON/JSONL event; `Raw` keeps full fidelity.</summary>
public enum ProviderEventKind
{
    Message,
    ToolUse,
    Result,
    Unknown,
}

public sealed record ProviderEvent(ProviderEventKind Kind, string? Text, JsonElement Raw);

/// <summary>
/// Neither vendor publishes an exhaustive error-code table, so this is a classification of the
/// process outcome, not a guarantee of vendor intent. `Unknown` is the safe default.
/// </summary>
public enum ProviderFailureKind
{
    None,
    NotReady,
    Authentication,
    QuotaExceeded,
    RateLimited,
    Policy,
    Transient,
    MalformedOutput,
    Unknown,
}

public sealed record ProviderRunResult(
    bool Succeeded,
    IReadOnlyList<ProviderEvent> Events,
    ProviderFailureKind Failure,
    string? Detail)
{
    public static ProviderRunResult Success(IReadOnlyList<ProviderEvent> events) =>
        new(true, events, ProviderFailureKind.None, null);

    /// <summary>`detail` may echo raw provider output, so it is redacted before it is stored.</summary>
    public static ProviderRunResult Failed(ProviderFailureKind failure, string detail) =>
        new(false, [], failure, SecretRedactor.Redact(detail));
}

/// <summary>Runs an installed, Forge-verified provider CLI non-interactively.</summary>
public interface IProviderAdapter
{
    ProviderKind Kind { get; }

    Task<ProviderRunResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// Shared execution and JSONL parsing. Every argument reaches the resolved, Forge-owned
/// executable directly through `ArgumentList` — never through a shell — so prompt text can never
/// be reinterpreted as a shell operator.
/// </summary>
internal static class ProviderExecution
{
    public static async Task<ProviderRunResult> RunAsync(
        IProviderStrategy strategy,
        IProcessRunner processRunner,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<JsonElement, ProviderEventKind> classify,
        Func<JsonElement, string?> extractText,
        CancellationToken cancellationToken)
    {
        string? executable = await strategy.ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (executable is null)
        {
            return ProviderRunResult.Failed(ProviderFailureKind.NotReady, "The provider is not ready.");
        }

        ProcessResult result = await processRunner
            .RunAsync(new(executable, arguments, workingDirectory), cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return ProviderRunResult.Failed(ClassifyFailure(result.StandardError), result.StandardError);
        }

        List<ProviderEvent> events = [];
        foreach (string line in result.StandardOutput.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            JsonElement root;
            try
            {
                using JsonDocument document = JsonDocument.Parse(trimmed);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return ProviderRunResult.Failed(
                    ProviderFailureKind.MalformedOutput,
                    $"The provider emitted a non-JSON output line: {trimmed}");
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return ProviderRunResult.Failed(
                    ProviderFailureKind.MalformedOutput,
                    "The provider emitted a JSON output line that was not an object.");
            }

            events.Add(new(classify(root), extractText(root), root));
        }

        return ProviderRunResult.Success(events);
    }

    /// <summary>
    /// Best-effort keyword match over the process's own error text; a miss classifies as
    /// `Unknown` rather than guessing, since exact vendor wording is not part of any published
    /// contract (see ADR 0002).
    /// </summary>
    private static ProviderFailureKind ClassifyFailure(string standardError)
    {
        string text = standardError.ToLowerInvariant();
        if (ContainsAny(text, "not logged in", "unauthorized", "authentication", "api key", "401"))
        {
            return ProviderFailureKind.Authentication;
        }

        if (ContainsAny(text, "rate limit", "429", "too many requests"))
        {
            return ProviderFailureKind.RateLimited;
        }

        if (ContainsAny(text, "quota", "usage limit", "billing"))
        {
            return ProviderFailureKind.QuotaExceeded;
        }

        if (ContainsAny(text, "policy", "blocked by", "content filter"))
        {
            return ProviderFailureKind.Policy;
        }

        if (ContainsAny(text, "timed out", "timeout", "econnreset", "network", "temporarily unavailable"))
        {
            return ProviderFailureKind.Transient;
        }

        return ProviderFailureKind.Unknown;
    }

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(needle => haystack.Contains(needle, StringComparison.Ordinal));
}

/// <summary>
/// Event shape per `developers.openai.com/codex` (`type`: `thread.started`, `turn.started`,
/// `turn.completed`, `turn.failed`, `item.*`). Item subtypes are documented only in prose, so
/// text extraction stays conservative rather than guessing field names.
/// </summary>
public sealed class CodexProviderAdapter(CodexProviderStrategy strategy, IProcessRunner processRunner) : IProviderAdapter
{
    public ProviderKind Kind => ProviderKind.Codex;

    public Task<ProviderRunResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        return ProviderExecution.RunAsync(
            strategy,
            processRunner,
            ["exec", "--json", prompt],
            workingDirectory,
            Classify,
            _ => null,
            cancellationToken);
    }

    private static ProviderEventKind Classify(JsonElement root)
    {
        string type = TypeOf(root);
        if (type.StartsWith("turn.", StringComparison.Ordinal))
        {
            return ProviderEventKind.Result;
        }

        return type.StartsWith("item.", StringComparison.Ordinal)
            ? ProviderEventKind.ToolUse
            : ProviderEventKind.Unknown;
    }

    private static string TypeOf(JsonElement root) =>
        root.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>
/// Event shape per Claude Code's `--output-format stream-json`: each line has a `type` of
/// `assistant`, `tool_use`, or `result`. `assistant` events wrap an Anthropic Messages API
/// object, so text is read from `message.content[].text`.
/// </summary>
public sealed class ClaudeCodeProviderAdapter(ClaudeCodeProviderStrategy strategy, IProcessRunner processRunner)
    : IProviderAdapter
{
    public ProviderKind Kind => ProviderKind.ClaudeCode;

    public Task<ProviderRunResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        return ProviderExecution.RunAsync(
            strategy,
            processRunner,
            ["-p", prompt, "--output-format", "stream-json"],
            workingDirectory,
            Classify,
            ExtractText,
            cancellationToken);
    }

    private static ProviderEventKind Classify(JsonElement root) => TypeOf(root) switch
    {
        "assistant" => ProviderEventKind.Message,
        "tool_use" => ProviderEventKind.ToolUse,
        "result" => ProviderEventKind.Result,
        _ => ProviderEventKind.Unknown,
    };

    private static string? ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out JsonElement message) ||
            !message.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        IEnumerable<string> blocks = content
            .EnumerateArray()
            .Where(block => block.TryGetProperty("type", out JsonElement blockType) &&
                blockType.ValueKind == JsonValueKind.String &&
                blockType.GetString() == "text" &&
                block.TryGetProperty("text", out JsonElement _))
            .Select(block => block.GetProperty("text").GetString() ?? string.Empty);
        string joined = string.Join(string.Empty, blocks);
        return joined.Length > 0 ? joined : null;
    }

    private static string TypeOf(JsonElement root) =>
        root.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() ?? string.Empty
            : string.Empty;
}
