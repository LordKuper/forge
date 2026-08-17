using System.Text;
using Forge.Compiler;
using Forge.Configuration;
using Forge.Providers;

namespace Forge.Application;

/// <summary>Whether a provider's target file (e.g. `CLAUDE.md`) is missing, matches the current
/// canonical generation, would change if regenerated, or is not Forge-owned at all (ADR 0011).
/// Decided solely by <see cref="IntegrationSourceCompiler.TryParseSourceDigest"/> — never a
/// heuristic guess.</summary>
public enum IntegrationArtifactState
{
    Missing,
    Current,
    Stale,
    Foreign,
}

public sealed record IntegrationArtifactInspection(GeneratedArtifact Artifact, IntegrationArtifactState State);

public sealed record IntegrationInspectionResult(
    IReadOnlyList<IntegrationArtifactInspection> Artifacts,
    IReadOnlyList<ForgeDocumentError> DocumentErrors,
    string DiagnosticCode)
{
    public static IntegrationInspectionResult Empty(string diagnosticCode) => new([], [], diagnosticCode);
}

/// <summary>What actually happened to one artifact during install/remove.</summary>
public enum IntegrationArtifactOutcome
{
    Written,
    Removed,
    Unchanged,

    /// <summary>The target file exists and is not Forge-owned; left untouched (ADR 0005's
    /// "duplicate installations are detected rather than left ambiguous").</summary>
    Refused,
}

public sealed record IntegrationArtifactResult(
    ProviderId ProviderId,
    string RelativePath,
    IntegrationArtifactOutcome Outcome);

public sealed record IntegrationWriteResult(
    IReadOnlyList<IntegrationArtifactResult> Artifacts,
    IReadOnlyList<ForgeDocumentError> DocumentErrors,
    string DiagnosticCode)
{
    public static IntegrationWriteResult Empty(string diagnosticCode) => new([], [], diagnosticCode);
}

/// <summary>
/// Installs and removes the artifacts <see cref="IntegrationGenerationService"/> produces (ADR
/// 0011). `install`/`remove` are idempotent and never touch a target file that is not Forge-owned;
/// every call re-derives state fresh from the current `.forge/` content and the current target
/// files — there is no persisted "last installed" record to go stale.
/// </summary>
public sealed class IntegrationInstallationService(IntegrationGenerationService generation)
{
    public async Task<IntegrationInspectionResult> InspectAsync(
        string projectRoot,
        IReadOnlyList<ProviderId> enabledProviderIds,
        string userFacingLanguage,
        string agentFacingLanguage,
        string generatorVersion,
        CancellationToken cancellationToken)
    {
        IntegrationGenerationResult generated = await generation
            .GenerateAsync(projectRoot, enabledProviderIds, userFacingLanguage, agentFacingLanguage, generatorVersion, cancellationToken)
            .ConfigureAwait(false);
        if (generated.Diagnostic == IntegrationGenerationDiagnostic.LanguageUnsupported)
        {
            return IntegrationInspectionResult.Empty(DiagnosticCodes.IntegrationLanguageUnsupported);
        }

        List<IntegrationArtifactInspection> inspections = [];
        foreach (GeneratedArtifact artifact in generated.Artifacts)
        {
            string targetPath = ResolveTargetPath(projectRoot, artifact);
            IntegrationArtifactState state = await InspectFileAsync(targetPath, artifact.SourceDigest, cancellationToken)
                .ConfigureAwait(false);
            inspections.Add(new(artifact, state));
        }

        return new(inspections, generated.DocumentErrors, DiagnosticCodes.None);
    }

    public async Task<IntegrationWriteResult> InstallAsync(
        string projectRoot,
        IReadOnlyList<ProviderId> enabledProviderIds,
        string userFacingLanguage,
        string agentFacingLanguage,
        string generatorVersion,
        CancellationToken cancellationToken)
    {
        IntegrationInspectionResult inspection = await InspectAsync(
                projectRoot, enabledProviderIds, userFacingLanguage, agentFacingLanguage, generatorVersion, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.DiagnosticCode != DiagnosticCodes.None)
        {
            return IntegrationWriteResult.Empty(inspection.DiagnosticCode);
        }

        List<IntegrationArtifactResult> results = [];
        bool anyRefused = false;
        foreach (IntegrationArtifactInspection item in inspection.Artifacts)
        {
            IntegrationArtifactOutcome outcome;
            switch (item.State)
            {
                case IntegrationArtifactState.Missing or IntegrationArtifactState.Stale:
                    string targetPath = ResolveTargetPath(projectRoot, item.Artifact);
                    await AtomicConfigurationFile
                        .WriteAsync(targetPath, Encoding.UTF8.GetBytes(item.Artifact.Content), cancellationToken, false)
                        .ConfigureAwait(false);
                    outcome = IntegrationArtifactOutcome.Written;
                    break;
                case IntegrationArtifactState.Current:
                    outcome = IntegrationArtifactOutcome.Unchanged;
                    break;
                default:
                    outcome = IntegrationArtifactOutcome.Refused;
                    anyRefused = true;
                    break;
            }

            results.Add(new(item.Artifact.ProviderId, item.Artifact.RelativePath, outcome));
        }

        return new(
            results,
            inspection.DocumentErrors,
            anyRefused ? DiagnosticCodes.IntegrationPartiallyRefused : DiagnosticCodes.None);
    }

    public async Task<IntegrationWriteResult> RemoveAsync(
        string projectRoot,
        IReadOnlyList<ProviderId> enabledProviderIds,
        string userFacingLanguage,
        string agentFacingLanguage,
        string generatorVersion,
        CancellationToken cancellationToken)
    {
        IntegrationInspectionResult inspection = await InspectAsync(
                projectRoot, enabledProviderIds, userFacingLanguage, agentFacingLanguage, generatorVersion, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.DiagnosticCode != DiagnosticCodes.None)
        {
            return IntegrationWriteResult.Empty(inspection.DiagnosticCode);
        }

        List<IntegrationArtifactResult> results = [];
        bool anyRefused = false;
        foreach (IntegrationArtifactInspection item in inspection.Artifacts)
        {
            IntegrationArtifactOutcome outcome;
            switch (item.State)
            {
                case IntegrationArtifactState.Missing:
                    outcome = IntegrationArtifactOutcome.Unchanged;
                    break;
                case IntegrationArtifactState.Current or IntegrationArtifactState.Stale:
                    File.Delete(ResolveTargetPath(projectRoot, item.Artifact));
                    outcome = IntegrationArtifactOutcome.Removed;
                    break;
                default:
                    outcome = IntegrationArtifactOutcome.Refused;
                    anyRefused = true;
                    break;
            }

            results.Add(new(item.Artifact.ProviderId, item.Artifact.RelativePath, outcome));
        }

        return new(
            results,
            inspection.DocumentErrors,
            anyRefused ? DiagnosticCodes.IntegrationPartiallyRefused : DiagnosticCodes.None);
    }

    private static async Task<IntegrationArtifactState> InspectFileAsync(
        string path,
        string expectedDigest,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path).LinkTarget is not null)
        {
            // Checked before File.Exists, not after: File.Exists follows the link and reports
            // false for a symlink whose target is missing/outside the project (POSIX stat()
            // semantics on Linux/macOS) — which would otherwise fall through to Missing and let
            // AtomicConfigurationFile.WriteAsync's File.Move silently replace the dangling symlink
            // with a plain file instead of refusing it. No legitimate installed integration file is
            // ever a symlink, so any reparse point at this path is conservatively foreign — the
            // same as an unrecognized marker — regardless of what it resolves to or whether it
            // resolves at all (ADR 0011).
            return IntegrationArtifactState.Foreign;
        }

        if (!File.Exists(path))
        {
            return IntegrationArtifactState.Missing;
        }

        string existing;
        try
        {
            existing = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Unreadable is treated the same as "no recognizable marker": conservatively foreign,
            // never assumed to be Forge's own file.
            return IntegrationArtifactState.Foreign;
        }

        if (!IntegrationSourceCompiler.TryParseSourceDigest(existing, out string? digest))
        {
            return IntegrationArtifactState.Foreign;
        }

        return string.Equals(digest, expectedDigest, StringComparison.Ordinal)
            ? IntegrationArtifactState.Current
            : IntegrationArtifactState.Stale;
    }

    /// <summary><paramref name="artifact"/>.RelativePath is a fixed string literal from a
    /// first-party <c>IProviderIntegrationGenerator</c>, never untrusted input (ADR 0011) — this is
    /// a correctness assertion on Forge's own code, not a security boundary the way ADR 0009's
    /// `.forge/` reference containment is.</summary>
    private static string ResolveTargetPath(string projectRoot, GeneratedArtifact artifact)
    {
        if (artifact.RelativePath.Contains('/', StringComparison.Ordinal) ||
            artifact.RelativePath.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Provider '{artifact.ProviderId.Value}' returned a non-flat relative path '{artifact.RelativePath}'.");
        }

        return Path.Combine(projectRoot, artifact.RelativePath);
    }
}
