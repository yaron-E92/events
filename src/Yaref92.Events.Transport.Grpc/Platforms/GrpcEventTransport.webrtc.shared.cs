#if ANDROID || NOT_ANDROID
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

using Google.Protobuf;
using Grpc.Core;
using SIPSorcery.Net;

namespace Yaref92.Events.Transport.Grpc;

public sealed partial class GrpcEventTransport
{
    private const int SignalMessageBufferSize = 4;
    private const int MaxSignalMessageSize = 64 * 1024;
    private static readonly JsonSerializerOptions SignalMessageOptions = new(JsonSerializerDefaults.Web);

    private static async Task<SignalMessage?> ReadSignalMessageAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBuffer = new byte[SignalMessageBufferSize];
        await stream.ReadExactlyAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
        if (length <= 0 || length > MaxSignalMessageSize)
        {
            return null;
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<SignalMessage>(payload, SignalMessageOptions);
    }

    private static async Task WriteSignalMessageAsync(NetworkStream stream, SignalMessage message, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, SignalMessageOptions);
        byte[] lengthBuffer = new byte[SignalMessageBufferSize];
        BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, payload.Length);
        await stream.WriteAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private sealed class WebRtcStreamWriter : IAsyncStreamWriter<TransportFrame>
    {
        private readonly RTCDataChannel _channel;

        public WebRtcStreamWriter(RTCDataChannel channel)
        {
            _channel = channel;
        }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(TransportFrame message)
        {
            DataChannelEnvelope envelope = FrameToEnvelope(message);
            byte[] payload = DataChannelProtocol.Encode(envelope);
            _channel.send(payload);
            return Task.CompletedTask;
        }
    }

    private static DataChannelEnvelope FrameToEnvelope(TransportFrame frame)
    {
        return new DataChannelEnvelope
        {
            Type = "transport_frame",
            CorrelationId = frame.EventId ?? string.Empty,
            Payload = frame.ToByteString(),
        };
    }

    private static TransportFrame EnvelopeToFrame(DataChannelEnvelope envelope)
    {
        try
        {
            return TransportFrame.Parser.ParseFrom(envelope.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return new TransportFrame();
        }
    }

    private sealed class SignalMessage
    {
        public const string OfferType = "offer";
        public const string AnswerType = "answer";
        public const string CandidateType = "candidate";

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("sdp")]
        public string? Sdp { get; set; }

        [JsonPropertyName("candidate")]
        public string? Candidate { get; set; }

        [JsonPropertyName("sdpMid")]
        public string? SdpMid { get; set; }

        [JsonPropertyName("sdpMLineIndex")]
        public int? SdpMLineIndex { get; set; }
    }
}
#endif
