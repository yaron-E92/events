
using System.Globalization;
using System.Net;

namespace Yaref92.Events.Sessions;

public class SessionKey(Guid userId, string host, int port)
{
    public Guid UserId { get; private set; } = userId;
    public string Host { get; } = host;
    public int Port { get; } = port;
    public bool IsAnonymousKey { get; init; }

    //public static bool TryParse(string? value, out SessionKey? sessionKey)
    //{
    //    sessionKey = null;
    //    if (string.IsNullOrWhiteSpace(value))
    //    {
    //        return false;
    //    }

    //    var atIndex = value.IndexOf('@');
    //    if (atIndex <= 0)
    //    {
    //        return false;
    //    }

    //    var hostPortSegment = value[(atIndex + 1)..];
    //    if (hostPortSegment.Length == 0)
    //    {
    //        return false;
    //    }

    //    var colonIndex = hostPortSegment.LastIndexOf(':');
    //    if (colonIndex <= 0 || colonIndex == hostPortSegment.Length - 1)
    //    {
    //        return false;
    //    }

    //    var userIdSegment = value[..atIndex];
    //    if (!Guid.TryParse(userIdSegment, out var userId))
    //    {
    //        return false;
    //    }

    //    var hostSegment = hostPortSegment[..colonIndex];
    //    if (string.IsNullOrWhiteSpace(hostSegment))
    //    {
    //        return false;
    //    }

    //    var portSegment = hostPortSegment[(colonIndex + 1)..];
    //    if (!int.TryParse(portSegment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port <= 0)
    //    {
    //        return false;
    //    }

    //    sessionKey = new SessionKey(userId, hostSegment, port);
    //    return true;
    //}

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
