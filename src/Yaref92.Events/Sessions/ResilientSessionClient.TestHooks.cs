#if DEBUG
namespace Yaref92.Events.Transports;

public sealed partial class ResilientSessionConnection
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

    internal void NotifySendFailureForTesting(Exception exception)
        => NotifySendFailure(exception);
}
#endif
