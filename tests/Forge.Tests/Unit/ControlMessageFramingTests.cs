using System.Buffers.Binary;
using Forge.Host.Client;

namespace Forge.UnitTests;

public sealed class ControlMessageFramingTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RoundTripsAMessage()
    {
        using MemoryStream stream = new();
        byte[] payload = "hello"u8.ToArray();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await ControlMessageFraming.WriteMessageAsync(stream, payload, cancellationToken);
        stream.Position = 0;
        byte[] received = await ControlMessageFraming.ReadMessageAsync(stream, cancellationToken);

        Assert.Equal(payload, received);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RejectsAMessageOverTheSizeLimit()
    {
        using MemoryStream stream = new();
        byte[] oversized = new byte[ControlMessageFraming.MaxMessageSize + 1];

        ControlProtocolException exception = await Assert.ThrowsAsync<ControlProtocolException>(
            () => ControlMessageFraming.WriteMessageAsync(stream, oversized, TestContext.Current.CancellationToken));

        Assert.Equal(ControlDiagnosticCode.MessageTooLarge, exception.Code);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RejectsAMalformedLengthPrefix()
    {
        using MemoryStream stream = new();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, -1);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await stream.WriteAsync(header, cancellationToken);
        stream.Position = 0;

        ControlProtocolException exception = await Assert.ThrowsAsync<ControlProtocolException>(
            () => ControlMessageFraming.ReadMessageAsync(stream, cancellationToken));

        Assert.Equal(ControlDiagnosticCode.Malformed, exception.Code);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RejectsAnUnexpectedClose()
    {
        using MemoryStream stream = new();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 10);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await stream.WriteAsync(header, cancellationToken);
        stream.Position = 0;

        ControlProtocolException exception = await Assert.ThrowsAsync<ControlProtocolException>(
            () => ControlMessageFraming.ReadMessageAsync(stream, cancellationToken));

        Assert.Equal(ControlDiagnosticCode.ConnectionClosed, exception.Code);
    }
}
