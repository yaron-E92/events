using Yaref92.Events.Abstractions;

using static Yaref92.Events.Transport.Tcp.Abstractions.IInboundResilientConnection;

namespace Yaref92.Events.Transport.Tcp.Abstractions;
internal interface IResilientTcpSession : IResilientPeerSession
{
    IOutboundResilientConnection OutboundConnection { get; }

    IInboundResilientConnection InboundConnection { get; }

    event SessionFrameReceivedHandler? FrameReceived;
}
