#if DEBUG
namespace Yaref92.Events.Sessions;

public sealed partial class ResilientOutboundConnection
{
    internal void SetOutboxPathForTesting(string outboxPath)
    {
        OutboxPath = outboxPath;
    }

    internal Task PersistOutboxForTestingAsync(CancellationToken cancellationToken)
        => PersistOutboxAsync(cancellationToken);

    internal Task LoadOutboxForTestingAsync(CancellationToken cancellationToken)
        => LoadOutboxAsync(cancellationToken);

    internal IReadOnlyDictionary<Guid, string> GetOutboxSnapshotForTesting()
    {
        var snapshot = new Dictionary<Guid, string>(_outboxEntries.Count);

        foreach (var (messageId, entry) in _outboxEntries)
        {
            snapshot[messageId] = entry.Payload;
        }

        return snapshot;
    }

    internal void SetLastRemoteActivityForTesting(DateTime timestamp)
    {
        _lastRemoteActivityTicks = timestamp.Ticks;
    }

    internal Task RunHeartbeatLoopForTestingAsync(CancellationToken cancellationToken)
        => RunHeartbeatLoopAsync(cancellationToken);

    internal TimeSpan GetBackoffDelayForTesting(int attempt)
        => GetBackoffDelay(attempt);

    internal Task WaitReconnectGateForTestingAsync(CancellationToken cancellationToken)
        => _reconnectGate.WaitAsync(cancellationToken);

    internal int GetReconnectGateCurrentCountForTesting()
        => _reconnectGate.CurrentCount;

    internal void FullyReleaseReconnectGateForTesting()
        => FullyReleaseReconnectGate();

    internal Task WaitForReconnectGateSignalForTestingAsync(CancellationToken cancellationToken)
        => WaitForReconnectGateCountChangeOrFullRelease(cancellationToken);

    internal void NotifySendFailureForTesting(Exception exception)
        => NotifySendFailure(exception);
}
#endif
