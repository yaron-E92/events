using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transport.Tcp;

internal partial class ResilientTcpPeerSession
{

    internal partial class SessionState(SessionKey key)
    {
        private long _lastHeartbeatTicks = DateTime.UtcNow.Ticks;

        public SessionKey Key { get; } = key;

        public bool RemoteEndpointHasAuthenticated { get; private set; }

        public void RegisterAuthentication()
        {
            RemoteEndpointHasAuthenticated = true;
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
}
