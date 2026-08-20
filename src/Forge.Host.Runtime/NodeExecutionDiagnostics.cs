using System.Security.Cryptography;
using System.Text;
using Forge.Domain;
using Forge.Providers;

namespace Forge.Host;

/// <summary>
/// Shared helpers every model-bearing node executor needs identically — extracted once a second
/// executor (implementation) needed the exact same three pieces <see cref="PlanningExecutionHostedService"/>
/// already had (behavior-preserving, not a design change): building a <see cref="NodeDiagnostic"/>
/// with the established `diagnostic.&lt;code&gt;` message-key convention, computing a
/// content-addressed `sha256:` digest for a <see cref="NodeResult.Outputs"/> entry, and mapping a
/// <see cref="ProviderFailureKind"/> to its durable diagnostic code.
/// </summary>
internal static class NodeExecutionDiagnostics
{
    public static NodeDiagnostic Diagnostic(string category, string code, string? detail = null)
    {
        Dictionary<string, string?> arguments = new(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            arguments["detail"] = detail;
        }

        return new(code, category, $"diagnostic.{code}", arguments);
    }

    public static string Digest(string content) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))}";

    public static string MapProviderFailure(ProviderFailureKind failure) => failure switch
    {
        ProviderFailureKind.NotReady => ProviderDiagnosticCodes.RunNotReady,
        ProviderFailureKind.Authentication => ProviderDiagnosticCodes.AuthenticationRequired,
        ProviderFailureKind.QuotaExceeded => ProviderDiagnosticCodes.QuotaExceeded,
        ProviderFailureKind.RateLimited => ProviderDiagnosticCodes.RateLimited,
        ProviderFailureKind.Policy => ProviderDiagnosticCodes.RunPolicyViolation,
        ProviderFailureKind.Transient => ProviderDiagnosticCodes.RunTransientFailure,
        ProviderFailureKind.MalformedOutput => ProviderDiagnosticCodes.RunMalformedOutput,
        ProviderFailureKind.MissingTerminalResult => ProviderDiagnosticCodes.MissingTerminalResult,
        ProviderFailureKind.DuplicateTerminalResult => ProviderDiagnosticCodes.DuplicateTerminalResult,
        _ => ProviderDiagnosticCodes.RunUnknownFailure,
    };
}
