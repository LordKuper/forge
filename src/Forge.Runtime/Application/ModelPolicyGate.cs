using System.Text.Json;
using Forge.Configuration;

namespace Forge.Application;

/// <summary>
/// ADR 0042: the allowlist half of ADR 0006's "project model policy" -- a project may restrict which
/// model id is acceptable per provider via the optional `models.allowed_models` configuration key
/// (a flat array of `"&lt;provider_id&gt;:&lt;model_id&gt;"` entries). A provider with no entries in
/// the list is unrestricted, matching every sprint's behavior before this key existed. Pure and
/// deterministic, callable both from <see cref="SprintOrchestrator.CreateSprintAsync"/> (a real gate,
/// checked before any event is written) and `forge eval`'s <see cref="EvaluationArea.ModelPolicy"/>
/// area (a dry-run report against the same enabled providers, no sprint required).
/// </summary>
public static class ModelPolicyGate
{
    private const char Separator = ':';
    private const string ConfigurationKey = "models.allowed_models";

    /// <summary>Reads `models.allowed_models` from an already-resolved project configuration view
    /// (<see cref="ForgeApplication.GetProjectConfigurationAsync"/> /
    /// <c>SprintOrchestrator</c>'s own project read), tolerating an absent key or a value of the
    /// wrong shape by returning an empty (unrestricted) list rather than throwing -- the same
    /// fallback-to-unrestricted discipline <see cref="TokenBudgetResolver"/> applies for an
    /// untrusted <c>context.token_budget</c>.</summary>
    public static IReadOnlyList<string> ParseAllowedModels(IReadOnlyList<EffectiveConfigurationValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        JsonElement? raw = values.FirstOrDefault(value => value.Key == ConfigurationKey)?.Value;
        if (raw is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        return [.. array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)];
    }

    /// <summary>Round 1 review of PR #87: a configured entry naming a provider id that matches no
    /// enabled provider (a typo, a renamed provider, a stale entry for a now-disabled provider)
    /// silently does nothing -- <see cref="IsAllowed"/> only ever sees the enabled provider's own
    /// id, so a policy meant to restrict a misspelled name enforces no restriction anywhere, with
    /// no signal it was ever ignored. `forge eval`'s <see cref="EvaluationArea.ModelPolicy"/> area
    /// calls this to report each configured entry's provider id that does not match any of
    /// <paramref name="enabledProviderIds"/> as a distinct failed check -- <see cref="IsAllowed"/>
    /// and the <c>SprintOrchestrator.CreateSprintAsync</c> gate stay unchanged (an unmatched entry
    /// still enforces nothing there, by design: a project may list models for a provider it has
    /// not enabled yet), so this is a diagnostic-only addition, not a behavior change to the gate
    /// itself.</summary>
    public static IReadOnlyList<string> UnmatchedProviderIds(
        IReadOnlyList<string> allowedModels, IReadOnlyList<string> enabledProviderIds)
    {
        ArgumentNullException.ThrowIfNull(allowedModels);
        ArgumentNullException.ThrowIfNull(enabledProviderIds);
        HashSet<string> enabled = new(enabledProviderIds, StringComparer.Ordinal);
        HashSet<string> unmatched = new(StringComparer.Ordinal);
        foreach (string entry in allowedModels)
        {
            int separatorIndex = entry.IndexOf(Separator);
            if (separatorIndex <= 0)
            {
                continue;
            }

            string providerId = entry[..separatorIndex];
            if (!enabled.Contains(providerId))
            {
                unmatched.Add(providerId);
            }
        }

        return [.. unmatched];
    }

    public static bool IsAllowed(string providerId, string modelId, IReadOnlyList<string> allowedModels)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(modelId);
        ArgumentNullException.ThrowIfNull(allowedModels);
        string prefix = $"{providerId}{Separator}";
        bool restricted = false;
        foreach (string entry in allowedModels)
        {
            if (!entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            restricted = true;
            if (string.Equals(entry, $"{providerId}{Separator}{modelId}", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return !restricted;
    }
}
