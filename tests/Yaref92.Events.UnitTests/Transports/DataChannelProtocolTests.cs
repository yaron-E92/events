using FluentAssertions;
using Google.Protobuf;
using Yaref92.Events.Transport.Grpc;

namespace Yaref92.Events.UnitTests.Transports;

[TestFixture]
public class DataChannelProtocolTests
{
    [Test]
    public void EncodeDecode_RoundTripsEnvelope()
    {
        var envelope = new DataChannelEnvelope
        {
            Type = "event",
            CorrelationId = Guid.NewGuid().ToString("D"),
            Payload = ByteString.CopyFromUtf8("payload"),
        };

        byte[] payload = DataChannelProtocol.Encode(envelope);

        DataChannelProtocol.TryDecode(payload, out var decoded).Should().BeTrue();
        decoded.Should().NotBeNull();
        decoded!.Type.Should().Be(envelope.Type);
        decoded.CorrelationId.Should().Be(envelope.CorrelationId);
        decoded.Payload.Should().BeEquivalentTo(envelope.Payload);
    }

    [Test]
    public void TryDecode_ReturnsFalse_ForInvalidPayload()
    {
        byte[] payload = { 0xFF };

        DataChannelProtocol.TryDecode(payload, out var decoded).Should().BeFalse();
        decoded.Should().BeNull();
    }
}
