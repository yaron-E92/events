
namespace Yaref92.Events.Transports;

public class SessionKey(Guid userId, string host, int port)
{
    public Guid UserId { get; } = userId;
    public string Host { get; } = host;
    public int Port { get; } = port;

    internal static bool IsNullOrEmpty(SessionKey sessionKey)
    {
        if (sessionKey is null)
        {
            return true;
        }
        return sessionKey.UserId == Guid.Empty && string.IsNullOrEmpty(sessionKey.Host) && sessionKey.Port == 0;
    }

    internal static bool IsNullOrInvalid(SessionKey sessionKey)
    {
        if (sessionKey is null)
        {
            return true;
        }
        return sessionKey.UserId == Guid.Empty || string.IsNullOrEmpty(sessionKey.Host) || sessionKey.Port == 0;
    }

    public override bool Equals(object? obj)
    {
        if (obj is not SessionKey other)
        {
            return false;
        }
        return UserId == other.UserId && Host == other.Host && Port == other.Port;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(UserId, Host, Port);
    }

    public override string? ToString() => $"{UserId}@{Host}:{Port}";
}
