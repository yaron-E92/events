using System.Collections.Concurrent;
using System.Net;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.Abstractions;

internal interface IOutboundResilientConnection : IResilientConnection
{
    DnsEndPoint RemoteEndPoint { get; }
    string OutboxPath { get; }
    ConcurrentQueue<SessionFrame> ControlQueue { get; }
    ConcurrentQueue<SessionFrame> EventQueue { get; }

    Task DumpBuffer(SessionOutboundBuffer outboundBuffer);
    //Task<Guid> EnqueueEventAsync(string payload, CancellationToken cancellationToken);
    void EnqueueFrame(SessionFrame frame);

    Task InitAsync(CancellationToken cancellationToken);
}
