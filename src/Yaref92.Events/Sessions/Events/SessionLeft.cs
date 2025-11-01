using Yaref92.Events.Transports;

namespace Yaref92.Events.Sessions.Events;

public class SessionLeft : DomainEventBase
{
    public SessionKey SessionKey { get; }
    public DateTime LeftAt { get; }

    public SessionLeft(SessionKey sessionKey)
    {
        SessionKey = sessionKey;
        LeftAt = DateTime.UtcNow;
    }

    public SessionLeft(Guid userId, string host, int port)
    {
        SessionKey = new(userId, host, port);
        LeftAt = DateTime.UtcNow;
    }
}
