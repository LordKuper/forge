using System.Text.Json;
using Forge.Configuration;
using Forge.Host.Client;

namespace Forge.Application;

/// <summary>
/// The client-side half of ADR 0005's "one Host owns mutations": every method here round-trips
/// through <see cref="ForgeHostClient"/> instead of ever executing the mutation locally. Only
/// project-scope <see cref="SetConfigurationAsync"/> calls are ever routed here — user-scope
/// configuration is not project state and stays local (see <see cref="IForgeMutations"/>'s own
/// remarks). Connecting is the caller's responsibility (<see cref="ForgeHostClient.EnsureConnectedAsync"/>
/// with a launcher, so a missing Host is started); a request sent before that, or a connection or
/// protocol failure, reports <see cref="DiagnosticCodes.HostUnavailable"/> — never a thrown
/// exception a caller must specifically catch, and never a silent local fallback. Disposing this
/// disposes the underlying <see cref="ForgeHostClient"/>.
/// </summary>
public sealed class RemoteForgeMutations(ForgeHostClient client, StartHostAsync? startHost = null)
    : IForgeMutations, IAsyncDisposable
{
    private readonly ForgeHostClient client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<RecoverStartupResult> RecoverStartupAsync(
        string? projectRoot,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(
            new RecoverStartupRequest(confirmed),
            ControlProtocol.JsonOptions);
        ControlResponse response =
            await SendAsync(ControlProtocol.RecoverStartupKind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<RecoverStartupResult>(ControlProtocol.JsonOptions) ??
                new(false, null, DiagnosticCodes.HostUnavailable)
            : new(false, null, DiagnosticCodes.HostUnavailable);
    }

    public async Task<ConfigurationWriteResult> SetConfigurationAsync(
        ConfigurationScope scope,
        string? projectRoot,
        string key,
        string? rawValue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (scope != ConfigurationScope.Project)
        {
            // A programming error in the caller, not a runtime condition — see IForgeMutations'
            // remarks on why user scope is never routed here.
            throw new ArgumentOutOfRangeException(
                nameof(scope),
                scope,
                "Only project-scope configuration routes through the Host.");
        }

        JsonElement payload = JsonSerializer.SerializeToElement(
            new SetConfigurationRequest("project", key, rawValue),
            ControlProtocol.JsonOptions);
        ControlResponse response =
            await SendAsync(ControlProtocol.SetConfigurationKind, payload, cancellationToken).ConfigureAwait(false);
        return response.Diagnostic.Code == ControlDiagnosticCode.None && response.Payload is { } responsePayload
            ? responsePayload.Deserialize<ConfigurationWriteResult>(ControlProtocol.JsonOptions) ??
                new(false, DiagnosticCodes.HostUnavailable)
            : new(false, DiagnosticCodes.HostUnavailable);
    }

    private async Task<ControlResponse> SendAsync(
        string kind,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        try
        {
            ControlDiagnostic connected = await client.EnsureConnectedAsync(startHost, cancellationToken)
                .ConfigureAwait(false);
            return connected.Code != ControlDiagnosticCode.None
                ? new(Guid.Empty, connected)
                : await client.SendAsync(kind, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (ControlProtocolException exception)
        {
            return new(Guid.Empty, new(exception.Code, exception.Message));
        }
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
