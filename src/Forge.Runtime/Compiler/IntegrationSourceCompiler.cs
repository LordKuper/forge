using System.Security.Cryptography;
using System.Text;
using Forge.Localization;

namespace Forge.Compiler;

/// <summary>Why generation was refused. Never thrown — a caller checks
/// <see cref="IntegrationSourceResult.Diagnostic"/> instead (ADR 0010).</summary>
public enum IntegrationSourceDiagnostic
{
    None,

    /// <summary>The resolved `artifacts.language.agent_facing` value is not in
    /// <see cref="ILocalizationCatalog.SupportedCultures"/>. Overview.md: "Missing artifact-language
    /// capability blocks generation; it never silently falls back."</summary>
    LanguageUnsupported,
}

/// <summary>One provider-agnostic, reproducible merged body compiled from a project's canonical
/// `.forge/` content (ADR 0010). <see cref="SourceDigest"/> is a pure function of
/// <see cref="Content"/>'s body (everything after the ownership-marker line, which embeds this
/// same digest) — comparing it to a previously recorded digest *is* drift detection; no separate
/// mechanism exists.</summary>
public sealed record CanonicalIntegrationSource(
    string Content,
    string SourceDigest,
    string PolicySnapshotHash,
    string GeneratorVersion,
    string Language);

public sealed record IntegrationSourceResult(CanonicalIntegrationSource? Source, IntegrationSourceDiagnostic Diagnostic)
{
    public static IntegrationSourceResult Unsupported() => new(null, IntegrationSourceDiagnostic.LanguageUnsupported);
}

/// <summary>
/// Compiles a <see cref="ForgeDocumentSet"/> (ADR 0009) and the project's artifact-language policy
/// into one <see cref="CanonicalIntegrationSource"/> (ADR 0010). Knows nothing about Claude Code,
/// Codex, or any other vendor — <c>Forge.Providers</c>' per-vendor
/// <c>IProviderIntegrationGenerator</c> implementations consume this output.
/// </summary>
public static class IntegrationSourceCompiler
{
    /// <summary>The generated-file marker format's own contract version — distinct from
    /// <see cref="CanonicalIntegrationSource.GeneratorVersion"/>, which is the Forge build that
    /// produced this instance.</summary>
    public const string ContractVersion = "1.0.0";

    public static IntegrationSourceResult Compile(
        ForgeDocumentSet documents,
        string userFacingLanguage,
        string agentFacingLanguage,
        IReadOnlyCollection<string> supportedCultures,
        string generatorVersion,
        ILocalizationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentException.ThrowIfNullOrWhiteSpace(userFacingLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentFacingLanguage);
        ArgumentNullException.ThrowIfNull(supportedCultures);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatorVersion);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!supportedCultures.Contains(agentFacingLanguage, StringComparer.OrdinalIgnoreCase))
        {
            return IntegrationSourceResult.Unsupported();
        }

        SurfaceText text = SurfaceText.For(catalog, agentFacingLanguage);
        string body = BuildBody(text, documents);
        string sourceDigest = Digest(body);
        string content = $"{Marker(sourceDigest, generatorVersion)}\n\n{body}";
        string policySnapshotHash = PolicySnapshotHash(userFacingLanguage, agentFacingLanguage);

        return new(
            new(content, sourceDigest, policySnapshotHash, generatorVersion, agentFacingLanguage),
            IntegrationSourceDiagnostic.None);
    }

    /// <summary>Builds with an explicit <c>'\n'</c> line terminator throughout — never
    /// <see cref="StringBuilder.AppendLine()"/>, which appends <see cref="Environment.NewLine"/>
    /// and would make an identical canonical `.forge/` tree digest differently on Windows
    /// (<c>\r\n</c>) than on Linux/macOS (<c>\n</c>), breaking ADR 0010's reproducibility
    /// guarantee for this cross-platform, no-OS-suffix code (ADR 0007).</summary>
    private static string BuildBody(SurfaceText text, ForgeDocumentSet documents)
    {
        StringBuilder builder = new();
        builder.Append(text.Resolve(MessageKeys.IntegrationHeaderPreamble)).Append('\n');
        builder.Append('\n');
        builder.Append(text.Resolve(MessageKeys.IntegrationTestingInvariant)).Append('\n');

        AppendSection(builder, documents.Documents.Where(document => document.Kind == ForgeDocumentKind.Rule));
        AppendSection(builder, documents.Documents.Where(document => document.Kind == ForgeDocumentKind.Knowledge));

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>Deterministic order (by <see cref="ForgeDocument.RelativePath"/>) so identical
    /// canonical input always compiles to identical bytes, regardless of filesystem enumeration
    /// order (ADR 0010's reproducibility requirement).</summary>
    private static void AppendSection(StringBuilder builder, IEnumerable<ForgeDocument> documents)
    {
        foreach (ForgeDocument document in documents.OrderBy(document => document.RelativePath, StringComparer.Ordinal))
        {
            builder.Append('\n');
            builder.Append("## ").Append(document.Title).Append('\n');
            builder.Append('\n');
            builder.Append(document.Body).Append('\n');
        }
    }

    /// <summary>Both <see cref="Marker"/> and <see cref="TryParseSourceDigest"/> build from this
    /// same constant, so the two can never drift apart the way a hand-rolled marker copy briefly
    /// did during ADR 0010's own review.</summary>
    private const string MarkerPrefix =
        "<!-- Generated by Forge. Do not edit directly — edit .forge/rules/ or .forge/knowledge/ " +
        "instead. source_digest=";

    /// <summary>Public so every <c>IProviderIntegrationGenerator</c> that needs its own
    /// ownership-marker line (e.g. a vendor file that doesn't carry the full canonical body) reuses
    /// this exact format instead of hand-rolling a copy that can drift from it.</summary>
    public static string Marker(string sourceDigest, string generatorVersion) =>
        $"{MarkerPrefix}{sourceDigest} generator_version={generatorVersion} schema_version={ContractVersion} -->";

    /// <summary>Whether <paramref name="content"/> starts with Forge's exact marker prefix and, if
    /// so, the <c>source_digest</c> it embeds (ADR 0011: this is the sole ownership test — a file
    /// without a recognizable marker is foreign, never a heuristic guess). Never throws; a
    /// malformed or absent marker simply returns <see langword="false"/>.</summary>
    public static bool TryParseSourceDigest(string content, out string? sourceDigest)
    {
        sourceDigest = null;
        if (string.IsNullOrEmpty(content) || !content.StartsWith(MarkerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        int start = MarkerPrefix.Length;
        int end = content.IndexOf(' ', start);
        string candidate = end < 0 ? content[start..] : content[start..end];
        if (!candidate.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        sourceDigest = candidate;
        return true;
    }

    private static string Digest(string content) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))}";

    /// <summary>Same digest formula as
    /// <c>Forge.Application.SprintOrchestrator.ArtifactPolicySnapshotHash</c> (JSON-string-quoted
    /// values, pipe-joined, SHA-256) so the same policy state always names the same hash whether a
    /// sprint or this compiler recorded it.</summary>
    private static string PolicySnapshotHash(string userFacingLanguage, string agentFacingLanguage) =>
        Digest($"artifacts.language.user_facing=\"{userFacingLanguage}\"|" +
            $"artifacts.language.agent_facing=\"{agentFacingLanguage}\"");
}
