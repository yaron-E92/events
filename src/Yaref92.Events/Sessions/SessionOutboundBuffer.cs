using System.Collections.Concurrent;

namespace Yaref92.Events.Sessions;

/// <summary>
/// In charge of the queueing of all outbound session frames.
/// For event frames with non empty ids only, it keeps track of
/// in flight events, and requeues them before dumping to outbox.
/// null frames are not accepted, and therefore no null frame will ever be dequeued
/// </summary>
public sealed class SessionOutboundBuffer : IDisposable
{
    private readonly ConcurrentQueue<SessionFrame> _frameQueue = new();
    private readonly ConcurrentDictionary<Guid, SessionFrame> _inflightEventFrames = new();
    private readonly SemaphoreSlim _signal = new(0);

    /// <summary>
    /// Enqueues non null frames
    /// </summary>
    /// <param name="frame"></param>
    public void EnqueueFrame(SessionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        _frameQueue.Enqueue(frame);
        _signal.Release();
    }

    /// <summary>
    /// Tries to dequeue a frame.
    /// A successfull result guarantess a non null frame
    /// </summary>
    /// <param name="frame"></param>
    /// <returns>true if dequeue successfull and frame is not null, although the second is just
    /// a precaution, since null frames are not accepted
    /// </returns>
    public bool TryDequeue(out SessionFrame? frame)
    {
        if (_frameQueue.TryDequeue(out frame) && frame is not null)
        {
            if (frame.Kind == SessionFrameKind.Event && frame.Id != Guid.Empty)
            {
                _inflightEventFrames[frame.Id] = frame;
            }

            return true;
        }

        return false;
    }

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        return _signal.WaitAsync(cancellationToken);
    }

    public void Return(SessionFrame frame)
    {
        if (frame.Kind == SessionFrameKind.Event && frame.Id != Guid.Empty)
        {
            _inflightEventFrames.TryRemove(frame.Id, out _);
        }

        _frameQueue.Enqueue(frame);
        _signal.Release();
    }

    public bool TryAcknowledge(Guid eventId)
    {
        if (_inflightEventFrames.TryRemove(eventId, out _))
        {
            return true;
        }

        return false;
    }

    public void RequeueInflight()
    {
        if (_inflightEventFrames.IsEmpty)
        {
            return;
        }

        List<SessionFrame> eventFrames = [.. _inflightEventFrames.Values.OrderBy(frame => frame.Id)];
        _inflightEventFrames.Clear();
        if (eventFrames.Count == 0)
        {
            return;
        }

        foreach (var frame in eventFrames)
        {
            _frameQueue.Enqueue(frame);
        }

        _signal.Release(eventFrames.Count);
    }

    public int InflightEventsCount => _inflightEventFrames.Count;

    public void Dispose()
    {
        _signal.Dispose();
    }
}
