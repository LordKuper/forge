namespace Forge.Compiler;

/// <summary>A document's kind is derived from which `.forge/` directory contains it (ADR 0009:
/// "removes an entire class of 'declared kind contradicts actual location' validation") — it is
/// never a frontmatter field.</summary>
public enum ForgeDocumentKind
{
    Rule,
    Knowledge,
}

/// <summary>Only <see cref="Project"/> is produced by the MVP directories. ADR 0009 models the
/// field now so Stage 11 can add a `Sprint` value and its document root as an additive minor
/// contract change, without redesigning this parser.</summary>
public enum ForgeDocumentScope
{
    Project,
}

public static class ForgeDocumentDiagnosticCodes
{
    /// <summary>The file is not valid UTF-8 Markdown with a `---`-delimited YAML frontmatter
    /// block, or the frontmatter fails `forge-document.schema.json` (ADR 0009).</summary>
    public const string FrontmatterInvalid = "forge_document_frontmatter_invalid";

    /// <summary>A `references` entry is empty, absolute, contains `..`, contains a backslash or
    /// drive/UNC prefix, resolves outside `.forge/` (directly or through a symlink target), or
    /// does not name a document this parse pass discovered under `rules/` or `knowledge/`.</summary>
    public const string ReferenceUnsafe = "forge_document_reference_unsafe";

    /// <summary>The document's estimated token count exceeds its effective
    /// <c>context_limit_tokens</c> (declared value, or the 4,000-token MVP default).</summary>
    public const string ContextLimitExceeded = "forge_document_context_limit_exceeded";

    /// <summary>Two documents in the same parse pass declared the same frontmatter `id`.</summary>
    public const string DuplicateId = "forge_document_duplicate_id";
}

/// <summary>A validated `references` entry: the frontmatter-declared relative path alongside the
/// full path it safely resolved to (ADR 0009's path-containment and symlink-target checks).</summary>
public sealed record ForgeDocumentReference(string RelativePath, string ResolvedPath);

/// <summary>One successfully parsed and validated `.forge/` canonical document (ADR 0009).
/// <paramref name="RelativePath"/> is forward-slash and relative to `.forge/` (e.g.
/// `rules/testing.md`), matching the shape a `references` entry in another document uses to
/// point back at this one.</summary>
public sealed record ForgeDocument(
    string Id,
    ForgeDocumentKind Kind,
    ForgeDocumentScope Scope,
    string Title,
    string RelativePath,
    string Body,
    int EstimatedTokens,
    int ContextLimitTokens,
    IReadOnlyList<ForgeDocumentReference> References);

/// <summary>One document that failed validation. Parsing collects these instead of throwing, so
/// one malformed file never blocks the rest of the parse pass (ADR 0009).</summary>
public sealed record ForgeDocumentError(string RelativePath, string DiagnosticCode, string Message);

/// <summary>The outcome of parsing every document under `.forge/rules/` and `.forge/knowledge/`.
/// Empty <see cref="Documents"/> and empty <see cref="Errors"/> is the valid, common case of a
/// project with no authored rules or knowledge yet.</summary>
public sealed record ForgeDocumentSet(
    IReadOnlyList<ForgeDocument> Documents,
    IReadOnlyList<ForgeDocumentError> Errors);
