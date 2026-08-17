using System.Text.Json;
using Forge.Compiler;
using Forge.Localization;
using Forge.Providers;
using Json.Schema;

namespace Forge.Application;

/// <summary>Why generation produced no artifacts. Mirrors
/// <see cref="IntegrationSourceDiagnostic"/> at the orchestration layer.</summary>
public enum IntegrationGenerationDiagnostic
{
    None,
    LanguageUnsupported,
}

/// <summary><paramref name="DocumentErrors"/> carries every <c>.forge/rules</c>/<c>knowledge</c>
/// parse failure (ADR 0009) alongside a successful generation — one malformed document degrades
/// the compiled content, it never blocks generation from the documents that did parse.</summary>
public sealed record IntegrationGenerationResult(
    IReadOnlyList<GeneratedArtifact> Artifacts,
    IReadOnlyList<ForgeDocumentError> DocumentErrors,
    IntegrationGenerationDiagnostic Diagnostic)
{
    public static IntegrationGenerationResult Unsupported(IReadOnlyList<ForgeDocumentError> documentErrors) =>
        new([], documentErrors, IntegrationGenerationDiagnostic.LanguageUnsupported);
}

/// <summary>
/// Orchestrates ADR 0009's <see cref="ForgeDocumentCompiler"/>, ADR 0010's
/// <see cref="IntegrationSourceCompiler"/>, and every registered
/// <see cref="IProviderIntegrationGenerator"/> into one project-level generation pass. A pure
/// library capability — it never writes outside <c>.forge/</c> (parsing only) and has no CLI
/// command yet; installing a <see cref="GeneratedArtifact"/> into its vendor's real config
/// location is P9.17-P9.24's separate concern.
/// </summary>
public sealed class IntegrationGenerationService(
    IEnumerable<IProviderIntegrationGenerator> generators,
    ILocalizationCatalog catalog)
{
    private const string SchemaLogicalName = "Forge.Providers.Schemas.generated-artifact.schema.json";

    private static readonly JsonSchema Schema = SchemaValidation.LoadEmbedded(SchemaLogicalName);

    public async Task<IntegrationGenerationResult> GenerateAsync(
        string projectRoot,
        IReadOnlyList<ProviderId> enabledProviderIds,
        string userFacingLanguage,
        string agentFacingLanguage,
        string generatorVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enabledProviderIds);

        ForgeDocumentSet documents = await new ForgeDocumentCompiler()
            .ParseAsync(projectRoot, cancellationToken)
            .ConfigureAwait(false);

        IntegrationSourceResult sourceResult = IntegrationSourceCompiler.Compile(
            documents,
            userFacingLanguage,
            agentFacingLanguage,
            catalog.SupportedCultures,
            generatorVersion,
            catalog);
        if (sourceResult.Source is null)
        {
            return IntegrationGenerationResult.Unsupported(documents.Errors);
        }

        HashSet<ProviderId> enabled = [.. enabledProviderIds];
        List<GeneratedArtifact> artifacts = [];
        foreach (IProviderIntegrationGenerator generator in generators)
        {
            if (!enabled.Contains(generator.ProviderId))
            {
                continue;
            }

            GeneratedArtifact artifact = generator.Generate(sourceResult.Source);
            Validate(artifact);
            artifacts.Add(artifact);
        }

        return new(artifacts, documents.Errors, IntegrationGenerationDiagnostic.None);
    }

    /// <summary>Defense-in-depth against a future adapter bug: every artifact a registered
    /// <see cref="IProviderIntegrationGenerator"/> produces must itself conform to
    /// `generated-artifact.schema.json` (ADR 0010). A violation here is Forge's own bug, not
    /// untrusted content, so it throws rather than returning a typed diagnostic.</summary>
    private static void Validate(GeneratedArtifact artifact)
    {
        JsonElement element = JsonSerializer.SerializeToElement(new
        {
            schema_version = IntegrationSourceCompiler.ContractVersion,
            provider_id = artifact.ProviderId.Value,
            relative_path = artifact.RelativePath,
            media_type = artifact.MediaType,
            audience = artifact.Audience,
            language = artifact.Language,
            source_digest = artifact.SourceDigest,
            policy_snapshot_hash = artifact.PolicySnapshotHash,
            generator_version = artifact.GeneratorVersion,
        });
        SchemaValidation.Validate(element, Schema, "generated artifact");
    }
}
