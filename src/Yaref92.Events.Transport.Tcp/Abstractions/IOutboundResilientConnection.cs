using System.Collections.Concurrent;
using System.Net;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transport.Tcp.Abstractions;

public interface IOutboundResilientConnection : IResilientConnection
{
    DnsEndPoint RemoteEndPoint { get; }
    string OutboxPath { get; }
    ConcurrentDictionary<Guid, AcknowledgementState> AcknowledgedEventIds { get; }
    SessionOutboundBuffer OutboundBuffer { get; }

    Task DumpBuffer();
    void EnqueueFrame(SessionFrame frame);

    Task InitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// When an ACK was received for this <paramref name="eventId"/>
    /// remove event from outbox and add acknoledgement
    /// </summary>
    /// <param name="eventId"></param>
    void OnAckReceived(Guid eventId);

    /// <summary>
    /// Whenever the underlying connection is lost or needs to be refreshed,
    /// awaits the re-establishment of the connection.
    /// </summary>
    /// <param name="token"></param>
    /// <returns>Task representing true if reconnected, false otherwise</returns>
    /// <remarks>After the outbound loop starts, we do while checking whether a connection
    /// was successfully re-established.
    /// If not, we await either the reduction of the reconnect semaphor
    /// (triggered by a failed reconnection when subsequent attempts are allowed) or its release
    /// either after a successful reconnect which later disconnects, cancellation or when attempts are exhausted.
    /// </remarks>
    Task<bool> RefreshConnectionAsync(CancellationToken token);
}
