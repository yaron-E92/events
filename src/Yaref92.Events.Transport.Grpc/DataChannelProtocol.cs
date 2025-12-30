using Google.Protobuf;

namespace Yaref92.Events.Transport.Grpc;

internal static class DataChannelProtocol
{
    public static byte[] Encode(DataChannelEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.ToByteArray();
    }

    public static bool TryDecode(ReadOnlyMemory<byte> payload, out DataChannelEnvelope? envelope)
    {
        envelope = null;
        if (payload.IsEmpty)
        {
            return false;
        }

        try
        {
            envelope = DataChannelEnvelope.Parser.ParseFrom(payload.Span);
            return true;
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
    }
}
