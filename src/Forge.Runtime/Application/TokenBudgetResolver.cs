using System.Text.Json;

namespace Forge.Application;

/// <summary>
/// Reads `context.token_budget` (ADR 0029) fresh on every call rather than caching it, matching
/// every node executor's own no-per-sprint-memory discipline — a project's configuration can
/// change between calls, and the added cost is one config read per attempt, not per tick. Falls
/// back to <see cref="DefaultTokenBudget"/> whenever the value cannot be trusted: the project
/// configuration is unreadable, the key is absent (an unconfigured project, the common case), or
/// the resolved value is not a positive integer. Extracted from
/// <c>IntakeExecutionHostedService.ResolveTokenBudgetAsync</c> (ADR 0028/0029) once a second node
/// executor (planning, Stage 11) needed the identical read — behavior-preserving, not a design
/// change.
/// </summary>
public static class TokenBudgetResolver
{
    /// <summary>ADR 0028: eight times <c>ForgeDocumentCompiler</c>'s own 4,000-token per-document
    /// default cap, an unverified MVP guess rather than a measured value. Over-budget items degrade
    /// by truncation, never by failure (ADR 0012), so guessing low costs admitted context rather
    /// than correctness.</summary>
    public const int DefaultTokenBudget = 32_000;

    private const string TokenBudgetKey = "context.token_budget";

    public static async Task<int> ResolveAsync(
        ForgeApplication application, string projectRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        ConfigurationView project = await application
            .GetProjectConfigurationAsync(projectRoot, cancellationToken)
            .ConfigureAwait(false);
        if (project.DiagnosticCode != DiagnosticCodes.None)
        {
            return DefaultTokenBudget;
        }

        JsonElement value = project.Values
            .FirstOrDefault(item => item.Key == TokenBudgetKey)?.Value ?? default;
        return value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int budget) && budget >= 1
                ? budget
                : DefaultTokenBudget;
    }
}
