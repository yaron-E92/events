using Yaref92.Events.Sessions;
using Yaref92.Events.Transports;

namespace Yaref92.Events.Abstractions;

public interface IResilientPeerSession : IAsyncDisposable
{
    SessionKey Key { get; }

    string SessionToken { get; }

    IOutboundResilientConnection OutboundConnection { get; }

    IInboundResilientConnection InboundConnection { get; }
    bool HasAuthenticated { get; }
    SessionOutboundBuffer OutboundBuffer { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task PublishAsync(string payload, CancellationToken cancellationToken);
    void AttachResilientConnection(ResilientSessionConnection resilientConnection);
    void EnqueueEvent(Guid eventId, string payload);
}
