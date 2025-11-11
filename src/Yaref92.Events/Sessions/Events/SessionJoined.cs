namespace Yaref92.Events.Sessions.Events;

public class SessionJoined : DomainEventBase
{
    public SessionKey SessionKey { get; }
    public DateTime JoinedAt { get; }

    public SessionJoined(SessionKey sessionKey)
    {
        SessionKey = sessionKey;
        JoinedAt = DateTime.UtcNow;
    }

    public SessionJoined(Guid userId, string host, int port)
    {
        SessionKey = new(userId, host, port);
        JoinedAt = DateTime.UtcNow;
    }
}
