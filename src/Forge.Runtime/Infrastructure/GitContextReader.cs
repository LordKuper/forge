using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Forge.Application;
using Forge.Compiler;
using Forge.Domain;

namespace Forge.Infrastructure;

/// <summary>
/// Validates and executes a <see cref="ContextQueryPlan"/> (ADR 0012) through
/// <see cref="IProcessRunner"/> — never a shell string, matching <c>GitWorktreeManager</c>'s own
/// invocation style. Both operation kinds read from `git`'s own content-addressed object store at
/// one pinned commit, from the project root itself: every linked worktree shares the same object
/// database, so no worktree needs to exist for a read pinned to a commit.
/// </summary>
public sealed partial class GitContextReader(IProcessRunner processRunner)
{
    // Matches `GitWorktreeManager.CommitPattern`: a commit-ish argument reaching this class must
    // already be a canonical, full-length hex object id.
    [GeneratedRegex(@"\A[0-9a-f]{40}\z|\A[0-9a-f]{64}\z")]
    private static partial Regex CommitPattern();

    private const int DefaultMaxResultBytes = 4096;
    private const int MinOperations = 1;
    private const int MaxOperations = 20;
    private const int MaxResultBytesCeiling = 65536;

    /// <summary>Validates the whole plan before executing anything — one invalid or unauthorized
    /// operation rejects it entirely, so no partial result can imply a request was partially
    /// honored — then runs each operation and returns the reproducible bundle. Never throws for an
    /// expected content problem.</summary>
    public async Task<ContextQueryPlanResult> ExecuteAsync(
        string projectRoot,
        ContextQueryPlan plan,
        IReadOnlyCollection<string> capabilityAllowlist,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(capabilityAllowlist);

        (ContextQueryPlanDiagnostic diagnostic, string? detail) = Validate(plan, capabilityAllowlist);
        if (diagnostic != ContextQueryPlanDiagnostic.None)
        {
            return ContextQueryPlanResult.Rejected(diagnostic, detail!);
        }

        List<ContextQueryResult> results = [];
        foreach (ContextQueryOperation operation in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ExecuteOperationAsync(projectRoot, plan.SourceCommit, operation, cancellationToken)
                .ConfigureAwait(false));
        }

        ContextResultBundle bundle = new(
            ContextResultBundle.ContractVersion, ComputePlanDigest(plan), plan.SourceCommit, results);
        return new(bundle, ContextQueryPlanDiagnostic.None);
    }

    private static (ContextQueryPlanDiagnostic Diagnostic, string? Detail) Validate(
        ContextQueryPlan plan,
        IReadOnlyCollection<string> capabilityAllowlist)
    {
        if (!CommitPattern().IsMatch(plan.SourceCommit))
        {
            return (ContextQueryPlanDiagnostic.SchemaInvalid, "source_commit is not a canonical full-length hex object id.");
        }

        // A schema-valid plan always has `operations` (`minItems: 1`); a plan built by hand or
        // deserialized without going through schema validation first could still leave it null —
        // treated the same as too few entries rather than left to throw on `.Count` below.
        if (plan.Operations is not { Count: >= MinOperations and <= MaxOperations })
        {
            return (ContextQueryPlanDiagnostic.SchemaInvalid, $"operations must contain {MinOperations}-{MaxOperations} entries.");
        }

        HashSet<string> seenIds = new(StringComparer.Ordinal);
        foreach (ContextQueryOperation operation in plan.Operations)
        {
            if (!seenIds.Add(operation.OperationId))
            {
                return (ContextQueryPlanDiagnostic.DuplicateOperationId,
                    $"operation_id '{operation.OperationId}' is declared more than once.");
            }

            string requiredCapability = operation.Kind == ContextQueryOperationKind.GitShow
                ? ContextCapabilityIds.GitShow
                : ContextCapabilityIds.GitGrep;
            if (!capabilityAllowlist.Contains(requiredCapability, StringComparer.Ordinal))
            {
                return (ContextQueryPlanDiagnostic.CapabilityDenied,
                    $"'{requiredCapability}' is not in the execution profile's capability allowlist.");
            }

            if (operation.MaxResultBytes is { } maxBytes && maxBytes is < 1 or > MaxResultBytesCeiling)
            {
                return (ContextQueryPlanDiagnostic.SchemaInvalid,
                    $"operation '{operation.OperationId}' max_result_bytes is out of range.");
            }

            (ContextQueryPlanDiagnostic Diagnostic, string? Detail)? shapeError = operation.Kind == ContextQueryOperationKind.GitShow
                ? ValidateGitShow(operation)
                : ValidateGitGrep(operation);
            if (shapeError is { } error)
            {
                return error;
            }
        }

        return (ContextQueryPlanDiagnostic.None, null);
    }

    private static (ContextQueryPlanDiagnostic, string?)? ValidateGitShow(ContextQueryOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Path))
        {
            return (ContextQueryPlanDiagnostic.SchemaInvalid, $"operation '{operation.OperationId}' is git_show but declares no path.");
        }

        return IsSafeRelativePath(operation.Path)
            ? null
            : (ContextQueryPlanDiagnostic.PathUnsafe, $"operation '{operation.OperationId}' path is unsafe.");
    }

    private static (ContextQueryPlanDiagnostic, string?)? ValidateGitGrep(ContextQueryOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Pattern))
        {
            return (ContextQueryPlanDiagnostic.SchemaInvalid, $"operation '{operation.OperationId}' is git_grep but declares no pattern.");
        }

        return operation.PathScope is not null && !IsSafeRelativePath(operation.PathScope)
            ? (ContextQueryPlanDiagnostic.PathUnsafe, $"operation '{operation.OperationId}' path_scope is unsafe.")
            : null;
    }

    private async Task<ContextQueryResult> ExecuteOperationAsync(
        string projectRoot,
        string sourceCommit,
        ContextQueryOperation operation,
        CancellationToken cancellationToken)
    {
        int maxBytes = operation.MaxResultBytes ?? DefaultMaxResultBytes;
        ProcessResult result;
        try
        {
            result = operation.Kind == ContextQueryOperationKind.GitShow
                ? await RunAsync(projectRoot, ["show", $"{sourceCommit}:{operation.Path}"], cancellationToken).ConfigureAwait(false)
                : await RunAsync(projectRoot, GrepArguments(sourceCommit, operation), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is Win32Exception or IOException)
        {
            // The process itself never launched (e.g. `git` is not on PATH) — distinct from `git`
            // launching and exiting non-zero, which is `NotFound` below.
            return new(operation.OperationId, ContextQueryOperationDiagnostic.ProcessFailed, null, null, 0, false);
        }

        // `git grep` exits 1 for "no matches" — a valid empty result, not a failure.
        bool noMatches = operation.Kind == ContextQueryOperationKind.GitGrep && result.ExitCode == 1;
        if (!noMatches && result.ExitCode != 0)
        {
            return new(operation.OperationId, ContextQueryOperationDiagnostic.NotFound, null, null, 0, false);
        }

        // Checked directly on the decoded string, before any byte-encoding work: a NUL character
        // is not producible by any valid UTF-8 text `git show`/`git grep -I` would otherwise
        // return, so its presence means the blob is binary content that slipped through — no need
        // to spend an encode pass on content this check is about to discard.
        if (result.StandardOutput.Contains('\0', StringComparison.Ordinal))
        {
            return new(operation.OperationId, ContextQueryOperationDiagnostic.Binary, null, null, 0, false);
        }

        byte[] raw = Encoding.UTF8.GetBytes(result.StandardOutput);
        bool truncated = raw.Length > maxBytes;
        byte[] admitted = truncated ? raw[..maxBytes] : raw;
        // A byte-boundary truncation may split a multi-byte character; `Encoding.UTF8.GetString`
        // replaces the partial trailing sequence with U+FFFD rather than throwing (ADR 0012).
        string content = Encoding.UTF8.GetString(admitted);
        return new(operation.OperationId, ContextQueryOperationDiagnostic.None, content, Digest(admitted), admitted.Length, truncated);
    }

    private static string[] GrepArguments(string sourceCommit, ContextQueryOperation operation)
    {
        List<string> arguments = ["grep", "--no-color", "-n", "-I", "-e", operation.Pattern!, sourceCommit];
        if (operation.PathScope is not null)
        {
            arguments.Add("--");
            arguments.Add(operation.PathScope);
        }

        return [.. arguments];
    }

    private Task<ProcessResult> RunAsync(string projectRoot, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        processRunner.RunAsync(new("git", arguments, projectRoot), cancellationToken);

    private static bool IsSafeRelativePath(string raw) => RelativePathShape.IsSyntacticallySafe(raw);

    /// <summary>Orders operations by <see cref="ContextQueryOperation.OperationId"/> regardless of
    /// the plan's own authored order, so semantically identical plans always digest identically —
    /// the same ordering-independence <c>IntegrationSourceCompiler</c> applies to documents.</summary>
    private static string ComputePlanDigest(ContextQueryPlan plan)
    {
        StringBuilder builder = new();
        builder.Append("schema_version=\"").Append(ContextQueryPlan.ContractVersion).Append('"');
        builder.Append("|source_commit=\"").Append(plan.SourceCommit).Append('"');
        foreach (ContextQueryOperation operation in plan.Operations.OrderBy(o => o.OperationId, StringComparer.Ordinal))
        {
            builder.Append("|operation_id=\"").Append(operation.OperationId).Append('"');
            builder.Append("|kind=\"").Append(operation.Kind).Append('"');
            builder.Append("|path=\"").Append(operation.Path).Append('"');
            builder.Append("|pattern=\"").Append(operation.Pattern).Append('"');
            builder.Append("|path_scope=\"").Append(operation.PathScope).Append('"');
            builder.Append("|max_result_bytes=\"").Append(operation.MaxResultBytes).Append('"');
        }

        return Digest(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string Digest(byte[] content) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(content))}";
}
