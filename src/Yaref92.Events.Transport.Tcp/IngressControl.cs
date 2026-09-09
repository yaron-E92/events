using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

using Microsoft.Extensions.Logging;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transport.Tcp;

internal sealed class PeerIngressLimiter
{
    private static readonly long WindowTicks = Stopwatch.Frequency;
    private static readonly long StaleTicks = Stopwatch.Frequency * 60L;
    private readonly Dictionary<string, PeerWindow> _windows = [];
    private readonly object _sync = new();
    private readonly int _allowance;
    private readonly int _maximumTrackedPeers;

    public PeerIngressLimiter(ResilientSessionOptions options)
    {
        _allowance = checked(options.MaxFramesPerSecondPerPeer + options.FrameRateBurstAllowance);
        _maximumTrackedPeers = (int)Math.Min((long)Math.Max(options.MaxInboundConnections, 16) * 4, int.MaxValue);
    }

    public bool TryAcquire(EndPoint? remoteEndPoint, out string peer)
    {
        peer = remoteEndPoint is IPEndPoint ipEndPoint
            ? ipEndPoint.Address.ToString()
            : "unknown";
        var now = Stopwatch.GetTimestamp();

        lock (_sync)
        {
            if (!_windows.TryGetValue(peer, out PeerWindow? window))
            {
                if (_windows.Count >= _maximumTrackedPeers)
                {
                    foreach (string stalePeer in _windows.Where(entry => now - entry.Value.LastSeen >= StaleTicks)
                                                               .Select(entry => entry.Key)
                                                               .ToArray())
                    {
                        _windows.Remove(stalePeer);
                    }
                }

                if (_windows.Count >= _maximumTrackedPeers)
                {
                    return false;
                }

                window = new PeerWindow(now);
                _windows.Add(peer, window);
            }

            window.LastSeen = now;
            if (now - window.WindowStarted >= WindowTicks)
            {
                window.WindowStarted = now;
                window.Count = 0;
            }

            if (window.Count >= _allowance)
            {
                return false;
            }

            window.Count++;
            return true;
        }
    }

    private sealed class PeerWindow(long now)
    {
        public long WindowStarted { get; set; } = now;
        public long LastSeen { get; set; } = now;
        public int Count { get; set; }
    }
}

internal sealed class IngressDiagnostics(ILogger? logger)
{
    private readonly ConcurrentDictionary<string, long> _lastLogTicks = new();

    public void Rejected(string category, string peer, string? detail = null, Exception? exception = null)
    {
        if (logger is null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var previous = _lastLogTicks.GetOrAdd(category, 0);
        if ((previous != 0 && now - previous < Stopwatch.Frequency) || !_lastLogTicks.TryUpdate(category, now, previous))
        {
            return;
        }

        logger.LogWarning(exception, "TCP ingress rejected peer {Peer} for {Category}; detail {Detail}", peer, category, detail);
    }
}

public sealed class IngressProtocolException(string category, Exception innerException)
    : Exception("The inbound frame violated the protocol.", innerException)
{
    public string Category { get; } = category;
}
