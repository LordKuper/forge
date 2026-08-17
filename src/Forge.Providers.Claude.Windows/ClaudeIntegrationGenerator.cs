using Forge.Compiler;

namespace Forge.Providers.Claude;

/// <summary>
/// Generates Claude Code's native integration file (ADR 0010). Claude Code reads `CLAUDE.md` at
/// the project root and supports a `@&lt;path&gt;` import directive — this repository's own
/// `CLAUDE.md` already uses `@AGENTS.md` for exactly this purpose. The generated file stays thin
/// and imports Codex's `AGENTS.md` rather than duplicating the canonical body, so the two vendor
/// files can never diverge in content.
/// </summary>
public sealed class ClaudeIntegrationGenerator : IProviderIntegrationGenerator
{
    /// <summary>This file's entire content after its own marker line — always exactly this fixed
    /// string, independent of the canonical source, so `Marker` embeds a <c>content_digest</c> of
    /// it for the same tamper-detection self-check <c>AGENTS.md</c>'s own body gets (ADR 0011).
    /// Unlike `AGENTS.md`, this file's bytes never need to change when `.forge/` changes — only
    /// its embedded `source_digest` cross-reference does.</summary>
    private const string ImportBody = "@AGENTS.md\n";

    public ProviderId ProviderId { get; } = ClaudeLlmProvider.ClaudeCode;

    public GeneratedArtifact Generate(CanonicalIntegrationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Reuses IntegrationSourceCompiler.Marker verbatim rather than hand-rolling a copy, so this
        // file's marker can never drift from AGENTS.md's (e.g. by silently missing a field like
        // schema_version) — and uses '\n' throughout, matching that same cross-platform-determinism
        // requirement.
        string content =
            $"{IntegrationSourceCompiler.Marker(source.SourceDigest, ImportBody, source.GeneratorVersion)}\n\n" +
            ImportBody;
        return new(
            ProviderId,
            "CLAUDE.md",
            content,
            "text/markdown",
            "agent_facing",
            source.Language,
            source.SourceDigest,
            source.PolicySnapshotHash,
            source.GeneratorVersion);
    }
}
