using System.Buffers.Binary;

namespace Forge.Host.Client;

/// <summary>Thrown for a framing-level failure: oversized message, malformed length, or an unexpected close.</summary>
public sealed class ControlProtocolException(ControlDiagnosticCode code, string message) : Exception(message)
{
    public ControlDiagnosticCode Code { get; } = code;
}

/// <summary>
/// The wire format every <see cref="ILocalControlConnection"/> implementation uses: a four-byte little-endian
/// length followed by that many bytes of UTF-8 JSON. One response per request; no partial or multiplexed frames.
/// </summary>
public static class ControlMessageFraming
{
    public const int MaxMessageSize = 1024 * 1024;

    public static async Task WriteMessageAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length > MaxMessageSize)
        {
            throw new ControlProtocolException(
                ControlDiagnosticCode.MessageTooLarge,
                $"Message of {payload.Length} bytes exceeds the {MaxMessageSize}-byte limit.");
        }

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[4];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxMessageSize)
        {
            throw new ControlProtocolException(
                ControlDiagnosticCode.Malformed,
                $"The message length prefix ({length}) is invalid.");
        }

        byte[] payload = new byte[length];
        await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async ValueTask ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new ControlProtocolException(
                    ControlDiagnosticCode.ConnectionClosed,
                    "The connection closed before a complete message was received.");
            }

            offset += read;
        }
    }
}
