
using System.Globalization;
using System.Net;

namespace Yaref92.Events.Sessions;

public class SessionKey(Guid userId, string host, int port)
{
    public Guid UserId { get; private set; } = userId;
    public string Host { get; } = host;
    public int Port { get; } = port;
    public bool IsAnonymousKey { get; init; }

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
        return string.IsNullOrEmpty(sessionKey.Host) || sessionKey.Port == 0;
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

    internal DnsEndPoint AsEndPoint()
    {
        return new DnsEndPoint(Host, Port);
    }

    internal void HydrateAnonymouseId(Guid identifier)
    {
        if (UserId != Guid.Empty)
        {
            throw new InvalidOperationException("Should only hydrate empty Guids");
        }
        UserId = identifier;
    }
}
