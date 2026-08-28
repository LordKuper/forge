namespace Forge.Providers;

/// <summary>
/// Translates a frozen <c>ExecutionProfile.Effort</c> value into the effort vocabulary one vendor
/// actually accepts (ADR 0062). The ladder below is neutral — an ordering of effort words, not a
/// vendor fact — while the accepted set is owned by each adapter, the same split ADR 0008 draws for
/// every other vendor detail.
///
/// `execution-profile.schema.json` types `effort` as any non-empty string, so an adapter can be
/// handed a level its vendor does not offer. Neither vendor protects Forge from that: Codex 0.149.1
/// forwards an unrecognized `model_reasoning_effort` verbatim to the API (verified: `-c
/// model_reasoning_effort=bogus` reaches the run header unchanged), and Claude Code 2.1.233 warns and
/// silently reverts to its own default. Both outcomes are worse than Forge deciding, so Forge decides
/// here and always sends a level the vendor lists.
/// </summary>
public static class ProviderEffortLevels
{
    /// <summary>Every level any registered adapter accepts, ordered lowest to highest. Membership is
    /// what makes a value clampable; a level outside this list is vocabulary Forge does not
    /// understand and is never approximated.</summary>
    private static readonly string[] Ladder =
        ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];

    /// <summary><see cref="Ladder"/> as a read-only view, so a surface that must *reject* unknown
    /// vocabulary rather than silently drop it (ADR 0067's <c>models.effort</c> configuration key
    /// and its schema enum) validates against this list instead of restating it.</summary>
    public static IReadOnlyList<string> KnownLevels => Ladder;

    /// <summary>
    /// The level <paramref name="supportedLevels"/> should actually receive, or <see langword="null"/>
    /// when no flag should be sent at all and the vendor's own default stands: either nothing was
    /// frozen, or the frozen value is not on <see cref="Ladder"/> and approximating it would be a
    /// guess. A supported level passes through unchanged; an unsupported but known level clamps to the
    /// nearest supported neighbour, ties going to the cheaper one — an approximation must never spend
    /// more than the policy asked for.
    /// </summary>
    public static string? Resolve(string? effort, IReadOnlyList<string> supportedLevels)
    {
        ArgumentNullException.ThrowIfNull(supportedLevels);
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        string normalized = effort.Trim().ToLowerInvariant();
        foreach (string level in supportedLevels)
        {
            if (string.Equals(level, normalized, StringComparison.Ordinal))
            {
                return level;
            }
        }

        int requested = Array.IndexOf(Ladder, normalized);
        if (requested < 0)
        {
            return null;
        }

        string? nearest = null;
        int nearestIndex = -1;
        int nearestDistance = int.MaxValue;
        foreach (string level in supportedLevels)
        {
            int index = Array.IndexOf(Ladder, level);
            if (index < 0)
            {
                continue;
            }

            int distance = Math.Abs(index - requested);
            if (distance < nearestDistance || (distance == nearestDistance && index < nearestIndex))
            {
                nearest = level;
                nearestIndex = index;
                nearestDistance = distance;
            }
        }

        return nearest;
    }
}
