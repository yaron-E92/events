#if DEBUG
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transport.Tcp.Connections;

internal sealed partial class ResilientCompositSessionConnection
{
    internal void SetOutboxPathForTesting(string outboxPath)
        => OutboundConnection.SetOutboxPathForTesting(outboxPath);

    internal Task PersistOutboxForTestingAsync(CancellationToken cancellationToken)
        => OutboundConnection.PersistOutboxForTestingAsync(cancellationToken);

    internal Task LoadOutboxForTestingAsync(CancellationToken cancellationToken)
        => OutboundConnection.LoadOutboxForTestingAsync(cancellationToken);

    internal IReadOnlyDictionary<Guid, string> GetOutboxSnapshotForTesting()
        => OutboundConnection.GetOutboxSnapshotForTesting();

    internal void SetLastRemoteActivityForTesting(DateTime timestamp)
        => OutboundConnection.SetLastRemoteActivityForTesting(timestamp);

    internal Task RunHeartbeatLoopForTestingAsync(CancellationToken cancellationToken)
        => OutboundConnection.RunHeartbeatLoopForTestingAsync(cancellationToken);

    internal TimeSpan GetBackoffDelayForTesting(int attempt)
        => OutboundConnection.GetBackoffDelayForTesting(attempt);

    internal void NotifySendFailureForTesting(Exception exception)
        => OutboundConnection.NotifySendFailureForTesting(exception);

    internal Guid StageOutboxEventForTesting(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var eventId = Guid.NewGuid();
        var frame = SessionFrame.CreateEventFrame(eventId, payload);
        OutboundConnection.EnqueueFrame(frame);
        return eventId;
    }
}
#endif
