using System.Net.Sockets;

namespace Yaref92.Events.Sessions;

public sealed class SessionState
{
    private readonly object _lock = new();

    private long _lastHeartbeatTicks = DateTime.UtcNow.Ticks;

    public SessionState(SessionKey key)
    {
        Key = key;
    }

    public SessionKey Key { get; }

    public bool HasAuthenticated { get; private set; }

    public void RegisterAuthentication()
    {
        HasAuthenticated = true;
        Touch();
    }

    public void Touch()
    {
        Volatile.Write(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
    }

    public bool IsExpired(DateTime utcNow, TimeSpan timeout)
    {
        var ticks = Volatile.Read(ref _lastHeartbeatTicks);
        var last = new DateTime(ticks, DateTimeKind.Utc);
        return utcNow - last > timeout;
    }
}
