#if ANDROID || NOT_ANDROID
using System.Diagnostics;

using Google.Protobuf;
using Grpc.Core;
using SIPSorcery.Net;

namespace Yaref92.Events.Transport.Grpc;

public sealed partial class GrpcEventTransport
{
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

    private StreamRegistration RegisterDataChannelSession(RTCDataChannel channel)
    {
        var writer = new WebRtcStreamWriter(channel);
        return RegisterStream(writer);
    }

    private void UnregisterDataChannelSession(StreamRegistration? registration, string reason)
    {
        if (registration is null)
        {
            return;
        }

        UnregisterStream(registration);
        Trace.TraceInformation("Data channel session {0} closed: {1}", registration.Id, reason);
    }
}
#endif
