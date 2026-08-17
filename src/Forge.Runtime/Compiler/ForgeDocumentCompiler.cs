using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Json.Schema;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Forge.Compiler;

/// <summary>
/// Parses `.forge/rules/*.md` and `.forge/knowledge/*.md` into validated
/// <see cref="ForgeDocument"/>s (ADR 0009). Never throws for an expected content problem —
/// missing directories, malformed frontmatter, unsafe references, oversized documents, and
/// duplicate ids are all collected as <see cref="ForgeDocumentError"/> entries instead, so one
/// malformed document never blocks the rest of the parse pass.
/// </summary>
public sealed class ForgeDocumentCompiler
{
    private const string RulesDirectoryName = "rules";
    private const string KnowledgeDirectoryName = "knowledge";
    private const string SchemaLogicalName = "Forge.Compiler.Schemas.forge-document.schema.json";
    private const int DefaultContextLimitTokens = 4000;
    private const int TokenEstimateCharsPerToken = 4;

    private static readonly JsonSchema Schema = SchemaValidation.LoadEmbedded(SchemaLogicalName);
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Deserializes directly into <see cref="Frontmatter"/> rather than a schema-less
    /// <c>object</c> graph: YamlDotNet's dynamic/untyped deserialization stringifies every scalar
    /// (a real, verified behavior — not just a risk), which would silently turn
    /// <c>context_limit_tokens: 5</c> into the JSON string <c>"5"</c> and fail
    /// `forge-document.schema.json`'s <c>"type": "integer"</c>. Typed deserialization resolves
    /// ints/strings correctly and, by YamlDotNet's own default, throws on an unmapped frontmatter
    /// key — the same strictness `additionalProperties: false` gives the rest of the schema.</summary>
    private readonly IDeserializer typedDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public async Task<ForgeDocumentSet> ParseAsync(string projectRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string forgeRoot = ProjectRootResolver.ForgeDirectory(Path.GetFullPath(projectRoot));

        List<Candidate> candidates = [
            .. Discover(forgeRoot, RulesDirectoryName, ForgeDocumentKind.Rule),
            .. Discover(forgeRoot, KnowledgeDirectoryName, ForgeDocumentKind.Knowledge),
        ];
        HashSet<string> knownFullPaths = new(candidates.Select(c => c.FullPath), Comparer());

        List<ForgeDocument> documents = [];
        List<ForgeDocumentError> errors = [];
        foreach (Candidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (ForgeDocument? document, ForgeDocumentError? error) = await ParseFileAsync(
                    forgeRoot,
                    candidate,
                    knownFullPaths,
                    cancellationToken)
                .ConfigureAwait(false);
            if (document is not null)
            {
                documents.Add(document);
            }
            else if (error is not null)
            {
                errors.Add(error);
            }
        }

        return SplitDuplicateIds(documents, errors);
    }

    private static ForgeDocumentSet SplitDuplicateIds(
        List<ForgeDocument> documents,
        List<ForgeDocumentError> errors)
    {
        List<IGrouping<string, ForgeDocument>> byId = [.. documents.GroupBy(d => d.Id, StringComparer.Ordinal)];
        List<ForgeDocument> unique = [];
        foreach (IGrouping<string, ForgeDocument> group in byId)
        {
            if (group.Count() == 1)
            {
                unique.Add(group.Single());
                continue;
            }

            errors.AddRange(group.Select(document => new ForgeDocumentError(
                document.RelativePath,
                ForgeDocumentDiagnosticCodes.DuplicateId,
                $"Document id '{document.Id}' is declared by more than one document.")));
        }

        return new(unique, errors);
    }

    private static IEnumerable<Candidate> Discover(string forgeRoot, string directoryName, ForgeDocumentKind kind)
    {
        string directory = Path.Combine(forgeRoot, directoryName);
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (string fullPath in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(fullPath);
            yield return new(kind, $"{directoryName}/{fileName}", Path.GetFullPath(fullPath));
        }
    }

    private async Task<(ForgeDocument? Document, ForgeDocumentError? Error)> ParseFileAsync(
        string forgeRoot,
        Candidate candidate,
        HashSet<string> knownFullPaths,
        CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(candidate.FullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, Invalid(candidate.RelativePath, "The document could not be read."));
        }

        if (!TrySplitFrontmatter(text, out string frontmatterYaml, out string body))
        {
            return (null, Invalid(
                candidate.RelativePath,
                "The document is missing a '---'-delimited YAML frontmatter block."));
        }

        Frontmatter frontmatter;
        try
        {
            frontmatter = typedDeserializer.Deserialize<Frontmatter>(frontmatterYaml) ??
                throw new InvalidDataException("The document frontmatter is empty.");
            JsonElement element = JsonSerializer.SerializeToElement(frontmatter, ConfigurationSchemaCodec.SerializerOptions);
            SchemaValidation.Validate(element, Schema, "forge document frontmatter");
        }
        catch (Exception error) when (
            error is YamlException or InvalidDataException or FormatException or JsonException)
        {
            return (null, Invalid(candidate.RelativePath, error.Message));
        }

        (List<ForgeDocumentReference> references, ForgeDocumentError? referenceError) = ResolveReferences(
            candidate.RelativePath,
            frontmatter.References,
            forgeRoot,
            knownFullPaths);
        if (referenceError is not null)
        {
            return (null, referenceError);
        }

        string trimmedBody = body.Trim('\n');
        int estimatedTokens = (trimmedBody.Length + TokenEstimateCharsPerToken - 1) / TokenEstimateCharsPerToken;
        int effectiveLimit = frontmatter.ContextLimitTokens ?? DefaultContextLimitTokens;
        if (estimatedTokens > effectiveLimit)
        {
            return (null, new(
                candidate.RelativePath,
                ForgeDocumentDiagnosticCodes.ContextLimitExceeded,
                $"The document's estimated {estimatedTokens} tokens exceed its {effectiveLimit}-token context limit."));
        }

        return (new ForgeDocument(
            frontmatter.Id!,
            candidate.Kind,
            ForgeDocumentScope.Project,
            frontmatter.Title!,
            candidate.RelativePath,
            trimmedBody,
            estimatedTokens,
            effectiveLimit,
            references), null);
    }

    private static (List<ForgeDocumentReference>, ForgeDocumentError?) ResolveReferences(
        string relativePath,
        IReadOnlyList<string>? rawReferences,
        string forgeRoot,
        HashSet<string> knownFullPaths)
    {
        List<ForgeDocumentReference> resolved = [];
        if (rawReferences is null)
        {
            return (resolved, null);
        }

        foreach (string raw in rawReferences)
        {
            if (!TryResolveSafeReference(raw, forgeRoot, knownFullPaths, out string resolvedPath, out string reason))
            {
                return ([], new(
                    relativePath,
                    ForgeDocumentDiagnosticCodes.ReferenceUnsafe,
                    $"Reference '{raw}' is unsafe: {reason}"));
            }

            resolved.Add(new(raw, resolvedPath));
        }

        return (resolved, null);
    }

    private static bool TryResolveSafeReference(
        string raw,
        string forgeRoot,
        HashSet<string> knownFullPaths,
        out string resolvedPath,
        out string reason)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            reason = "it is empty";
            return false;
        }

        if (raw.Contains('\\', StringComparison.Ordinal))
        {
            reason = "it must use forward slashes";
            return false;
        }

        if (raw.StartsWith('/') || raw.Contains(':', StringComparison.Ordinal))
        {
            reason = "it must be a relative path with no drive or scheme prefix";
            return false;
        }

        string[] segments = raw.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            reason = "it must not contain '.', '..', or empty segments";
            return false;
        }

        string candidateFull = Path.GetFullPath(Path.Combine(forgeRoot, raw));
        if (!IsWithin(forgeRoot, candidateFull))
        {
            reason = "it resolves outside .forge/";
            return false;
        }

        if (!File.Exists(candidateFull))
        {
            reason = "it does not exist";
            return false;
        }

        string? finalTarget = new FileInfo(candidateFull).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        if (finalTarget is not null && !IsWithin(forgeRoot, finalTarget))
        {
            reason = "it resolves outside .forge/ through a symlink";
            return false;
        }

        if (!knownFullPaths.Contains(candidateFull))
        {
            reason = "it does not name a document this parse pass discovered";
            return false;
        }

        resolvedPath = candidateFull;
        reason = string.Empty;
        return true;
    }

    private static bool IsWithin(string root, string candidate)
    {
        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, PathComparison);
    }

    private static bool TrySplitFrontmatter(string text, out string frontmatter, out string body)
    {
        frontmatter = string.Empty;
        body = string.Empty;
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return false;
        }

        int end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        int delimiterLength = 5;
        if (end < 0)
        {
            if (!normalized.EndsWith("\n---", StringComparison.Ordinal))
            {
                return false;
            }

            end = normalized.Length - 4;
            delimiterLength = 4;
        }

        frontmatter = normalized[4..end];
        body = normalized[(end + delimiterLength)..];
        return true;
    }

    private static ForgeDocumentError Invalid(string relativePath, string message) =>
        new(relativePath, ForgeDocumentDiagnosticCodes.FrontmatterInvalid, message);

    private static StringComparer Comparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record Candidate(ForgeDocumentKind Kind, string RelativePath, string FullPath);

    /// <summary>All required fields are nullable and default to <see langword="null"/> rather
    /// than an empty string, so an absent YAML key stays absent through
    /// <see cref="ConfigurationSchemaCodec.SerializerOptions"/>'s
    /// <c>JsonIgnoreCondition.WhenWritingNull</c> and schema `required` validation still catches
    /// it — a default of <see cref="string.Empty"/> would silently satisfy `required` instead.</summary>
    private sealed class Frontmatter
    {
        public string? SchemaVersion { get; set; }

        public string? Id { get; set; }

        public string? Title { get; set; }

        public string? Scope { get; set; }

        public List<string>? References { get; set; }

        public int? ContextLimitTokens { get; set; }
    }
}
