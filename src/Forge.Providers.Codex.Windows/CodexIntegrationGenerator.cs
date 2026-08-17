using Forge.Compiler;

namespace Forge.Providers.Codex;

/// <summary>
/// Generates Codex's native integration file (ADR 0010). Codex reads `AGENTS.md` at the project
/// root — the same convention this repository's own `AGENTS.md` uses — so the full canonical body
/// is written there verbatim; Claude Code's generated `CLAUDE.md` imports this file rather than
/// duplicating it.
/// </summary>
public sealed class CodexIntegrationGenerator : IProviderIntegrationGenerator
{
    public ProviderId ProviderId { get; } = CodexLlmProvider.Codex;

    public GeneratedArtifact Generate(CanonicalIntegrationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(
            ProviderId,
            "AGENTS.md",
            source.Content,
            "text/markdown",
            "agent_facing",
            source.Language,
            source.SourceDigest,
            source.PolicySnapshotHash,
            source.GeneratorVersion);
    }
}
