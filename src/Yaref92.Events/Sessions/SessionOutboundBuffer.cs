using System.Collections.Concurrent;

namespace Yaref92.Events.Sessions;

public sealed class SessionOutboundBuffer : IDisposable
{
    private readonly ConcurrentQueue<SessionFrame> _frameQueue = new();
    private readonly ConcurrentDictionary<Guid, SessionFrame> _inflightEventFrames = new();
    private readonly SemaphoreSlim _signal = new(0);

    public void EnqueueEvent(Guid eventId, string payload)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty message identifier is required.", nameof(eventId));
        }

        ArgumentNullException.ThrowIfNull(payload);

        var frame = SessionFrame.CreateEventFrame(eventId, payload);
        EnqueueFrame(frame);
    }

    public void EnqueueFrame(SessionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        _frameQueue.Enqueue(frame);
        _signal.Release();
    }

    public bool TryDequeue(out SessionFrame frame)
    {
        if (_frameQueue.TryDequeue(out frame))
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
