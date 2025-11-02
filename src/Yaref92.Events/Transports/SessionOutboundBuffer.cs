using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Yaref92.Events.Transports;

internal sealed class SessionOutboundBuffer : IDisposable
{
    private readonly ConcurrentQueue<SessionFrame> _queue = new();
    private readonly ConcurrentDictionary<Guid, SessionFrame> _inflight = new();
    private readonly SemaphoreSlim _signal = new(0);

    public void EnqueueEvent(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        Guid messageId = Guid.NewGuid();
        var frame = SessionFrame.CreateMessage(messageId, payload);
        EnqueueFrame(frame);
    }

    public void EnqueueFrame(SessionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        _queue.Enqueue(frame);
        _signal.Release();
    }

    public bool TryDequeue(out SessionFrame frame)
    {
        if (_queue.TryDequeue(out frame))
        {
            if (frame.Kind == SessionFrameKind.Event && frame.Id != Guid.Empty)
            {
                _inflight[frame.Id] = frame;
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
            _inflight.TryRemove(frame.Id, out _);
        }

        _queue.Enqueue(frame);
        _signal.Release();
    }

    public bool TryAcknowledge(Guid messageId)
    {
        if (_inflight.TryRemove(messageId, out _))
        {
            return true;
        }

        return false;
    }

    public void RequeueInflight()
    {
        if (_inflight.IsEmpty)
        {
            return;
        }

        List<SessionFrame> frames = _inflight.Values.OrderBy(frame => frame.Id).ToList();
        _inflight.Clear();
        if (frames.Count == 0)
        {
            return;
        }

        foreach (var frame in frames)
        {
            _queue.Enqueue(frame);
        }

        _signal.Release(frames.Count);
    }

    public int InflightCount => _inflight.Count;

    public void Dispose()
    {
        _signal.Dispose();
    }
}
