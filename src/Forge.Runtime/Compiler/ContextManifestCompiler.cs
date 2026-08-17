using System.Security.Cryptography;
using System.Text;
using Forge.Domain;

namespace Forge.Compiler;

/// <summary>
/// Builds one <see cref="ContextManifest"/> per sprint from a <see cref="ForgeDocumentSet"/> (ADR
/// 0009) and the sprint's own already-frozen identity (ADR 0012). Knows nothing about how a
/// <see cref="ContextResultBundle"/> is produced — a caller that already has one attaches it via
/// <see cref="WithQueryResults"/>.
/// </summary>
public static class ContextManifestCompiler
{
    private const string RuleRationale = "rule";
    private const string KnowledgeRationale = "knowledge:accepted";
    private const string QueryResultRationale = "query_result";
    private const string TruncatedReason = "over_budget";

    /// <summary>Admits <paramref name="documents"/>' rules, then its accepted-or-unstatused
    /// knowledge (ADR 0012's "Accepted ADRs"), each ordered by <see cref="ForgeDocument.RelativePath"/>
    /// (<see cref="StringComparer.Ordinal"/>, matching <c>IntegrationSourceCompiler</c>'s ordering
    /// rule), against <paramref name="tokenBudget"/> in that fixed order. An item that does not fit
    /// the remaining budget is recorded in <see cref="ContextManifest.Truncated"/> and skipped; the
    /// walk continues so a later, smaller item can still be admitted.</summary>
    public static ContextManifest Compile(
        Guid sprintId,
        string sourceCommit,
        string workflow,
        string workflowVersion,
        ForgeDocumentSet documents,
        int tokenBudget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowVersion);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentOutOfRangeException.ThrowIfLessThan(tokenBudget, 1);

        IEnumerable<Candidate> rules = documents.Documents
            .Where(document => document.Kind == ForgeDocumentKind.Rule)
            .OrderBy(document => document.RelativePath, StringComparer.Ordinal)
            .Select(document => new Candidate(document.RelativePath, Digest(document.Body), document.EstimatedTokens));
        IEnumerable<Candidate> knowledge = documents.Documents
            .Where(document => document.Kind == ForgeDocumentKind.Knowledge &&
                document.Status is null or ForgeDocumentStatus.Accepted)
            .OrderBy(document => document.RelativePath, StringComparer.Ordinal)
            .Select(document => new Candidate(document.RelativePath, Digest(document.Body), document.EstimatedTokens));

        List<ContextManifestTruncatedItem> truncated = [];
        int remaining = tokenBudget;
        List<ContextManifestItem> admittedRules = Admit(rules, RuleRationale, truncated, ref remaining);
        List<ContextManifestItem> admittedKnowledge = Admit(knowledge, KnowledgeRationale, truncated, ref remaining);

        ContextManifestLayers layers = new(admittedRules, [], admittedKnowledge, [], []);
        string digest = ComputeManifestDigest(sprintId, sourceCommit, workflow, workflowVersion, tokenBudget, layers);

        return new(
            ContextManifest.ContractVersion, sprintId, sourceCommit, workflow, workflowVersion,
            tokenBudget, SumTokens(layers), layers, truncated, digest);
    }

    /// <summary>Attaches a <see cref="ContextResultBundle"/> (ADR 0012's layer 4) to an already
    /// compiled manifest against whatever budget <paramref name="manifest"/> has left — exactly the
    /// same admit-or-truncate policy <see cref="Compile"/> applies to rules and knowledge, so layer
    /// 4 cannot silently push <see cref="ContextManifest.AllocatedTokens"/> past
    /// <see cref="ContextManifest.TokenBudget"/>. A query result's estimated token cost is its
    /// content's chars/4 estimate — the same heuristic <c>ForgeDocumentCompiler</c> uses on a
    /// document's char length, not its byte count, so every layer counts tokens the same way for
    /// non-ASCII content too.</summary>
    public static ContextManifest WithQueryResults(ContextManifest manifest, ContextResultBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(bundle);

        IEnumerable<Candidate> results = bundle.Results
            .Where(result => result.Diagnostic == ContextQueryOperationDiagnostic.None &&
                result.Content is not null && result.ContentDigest is not null)
            .OrderBy(result => result.OperationId, StringComparer.Ordinal)
            .Select(result => new Candidate(result.OperationId, result.ContentDigest!, EstimateTokens(result.Content!)));

        List<ContextManifestTruncatedItem> truncated = [.. manifest.Truncated];
        int remaining = manifest.TokenBudget - manifest.AllocatedTokens;
        List<ContextManifestItem> queryResults = Admit(results, QueryResultRationale, truncated, ref remaining);

        ContextManifestLayers layers = manifest.Layers with { QueryResults = queryResults };
        string digest = ComputeManifestDigest(
            manifest.SprintId, manifest.SourceCommit, manifest.Workflow, manifest.WorkflowVersion,
            manifest.TokenBudget, layers);

        return manifest with { Layers = layers, AllocatedTokens = SumTokens(layers), Truncated = truncated, ManifestDigest = digest };
    }

    private static List<ContextManifestItem> Admit(
        IEnumerable<Candidate> ordered,
        string rationale,
        List<ContextManifestTruncatedItem> truncated,
        ref int remaining)
    {
        List<ContextManifestItem> admitted = [];
        foreach (Candidate candidate in ordered)
        {
            if (candidate.EstimatedTokens <= remaining)
            {
                admitted.Add(new(candidate.RelativePath, candidate.Digest, candidate.EstimatedTokens, rationale));
                remaining -= candidate.EstimatedTokens;
            }
            else
            {
                truncated.Add(new(candidate.RelativePath, candidate.EstimatedTokens, TruncatedReason));
            }
        }

        return admitted;
    }

    private static int SumTokens(ContextManifestLayers layers) =>
        layers.Rules.Sum(item => item.EstimatedTokens) +
        layers.SprintSpecifications.Sum(item => item.EstimatedTokens) +
        layers.Knowledge.Sum(item => item.EstimatedTokens) +
        layers.Handoffs.Sum(item => item.EstimatedTokens) +
        layers.QueryResults.Sum(item => item.EstimatedTokens);

    private static int EstimateTokens(string content) => (content.Length + 3) / 4;

    private static string ComputeManifestDigest(
        Guid sprintId,
        string sourceCommit,
        string workflow,
        string workflowVersion,
        int tokenBudget,
        ContextManifestLayers layers)
    {
        StringBuilder builder = new();
        builder.Append("schema_version=\"").Append(ContextManifest.ContractVersion).Append('"');
        builder.Append("|sprint_id=\"").Append(sprintId.ToString("D")).Append('"');
        builder.Append("|source_commit=\"").Append(sourceCommit).Append('"');
        builder.Append("|workflow=\"").Append(workflow).Append('"');
        builder.Append("|workflow_version=\"").Append(workflowVersion).Append('"');
        builder.Append("|token_budget=\"").Append(tokenBudget).Append('"');
        AppendLayer(builder, "rules", layers.Rules);
        AppendLayer(builder, "sprint_specifications", layers.SprintSpecifications);
        AppendLayer(builder, "knowledge", layers.Knowledge);
        AppendLayer(builder, "handoffs", layers.Handoffs);
        AppendLayer(builder, "query_results", layers.QueryResults);
        return Digest(builder.ToString());
    }

    private static void AppendLayer(StringBuilder builder, string name, IReadOnlyList<ContextManifestItem> items)
    {
        foreach (ContextManifestItem item in items)
        {
            builder.Append('|').Append(name).Append(".relative_path=\"").Append(item.RelativePath).Append('"');
            builder.Append('|').Append(name).Append(".digest=\"").Append(item.Digest).Append('"');
        }
    }

    private static string Digest(string content) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))}";

    private readonly record struct Candidate(string RelativePath, string Digest, int EstimatedTokens);
}
