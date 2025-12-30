using SIPSorcery.Net;

namespace Yaref92.Events.Transport.Grpc;

internal interface IDataChannelSession : IAsyncDisposable
{
    Guid Id { get; }

    Task<bool> ConnectAsync(SignalMessage? offer, TimeSpan? timeout, CancellationToken cancellationToken);

    Task SendAsync(SignalMessage message, CancellationToken cancellationToken = default);

    void HookInboundFrames(RTCDataChannel channel);
}
