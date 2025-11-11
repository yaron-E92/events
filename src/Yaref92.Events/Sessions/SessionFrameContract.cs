namespace Yaref92.Events.Sessions;

internal static class SessionFrameContract
{
    public const string TokenSecretDelimiter = "||";

    public static string CreateSessionToken(SessionKey sessionKey, ResilientSessionOptions options, string? authenticationSecret)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentNullException.ThrowIfNull(options);

        var prefix = sessionKey.ToString() ?? throw new InvalidOperationException("Session key produced an invalid token prefix.");
        if (!options.RequireAuthentication)
        {
            return $"{prefix}{TokenSecretDelimiter}{Guid.NewGuid():N}";
        }

        var secret = ResolveSecret(authenticationSecret, options);
        return $"{prefix}{TokenSecretDelimiter}{secret}";
    }

    public static SessionFrame CreateAuthFrame(string sessionToken, ResilientSessionOptions options, string? authenticationSecret)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            throw new ArgumentException("Session token must be provided.", nameof(sessionToken));
        }

        var payload = options.RequireAuthentication
            ? ResolveSecret(authenticationSecret, options)
            : null;

        return SessionFrame.CreateAuth(sessionToken, payload);
    }

    public static bool TryValidateAuthentication(SessionFrame frame, ResilientSessionOptions options, out SessionKey sessionKey)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        sessionKey = null!;
        if (frame.Kind != SessionFrameKind.Auth)
        {
            return false;
        }

        if (!TryParseSessionToken(frame.Token, out sessionKey, out var embeddedSecret))
        {
            return false;
        }

        if (!options.RequireAuthentication)
        {
            return true;
        }

        var expectedSecret = options.AuthenticationToken;
        if (string.IsNullOrEmpty(expectedSecret))
        {
            return true;
        }

        var providedSecret = frame.Payload;
        if (string.IsNullOrEmpty(providedSecret))
        {
            providedSecret = embeddedSecret;
        }

        if (string.IsNullOrEmpty(providedSecret))
        {
            return false;
        }

        return string.Equals(expectedSecret, providedSecret, StringComparison.Ordinal);
    }

    public static TimeSpan GetHeartbeatInterval(ResilientSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.HeartbeatInterval;
    }

    public static TimeSpan GetHeartbeatTimeout(ResilientSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var timeout = options.HeartbeatTimeout;
        return timeout <= TimeSpan.Zero
            ? GetHeartbeatInterval(options) * 2
            : timeout;
    }

    private static string ResolveSecret(string? providedSecret, ResilientSessionOptions options)
    {
        var secret = providedSecret ?? options.AuthenticationToken;
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("Authentication is required but no secret was supplied.");
        }

        return secret;
    }

    private static bool TryParseSessionToken(string? token, out SessionKey sessionKey, out string? embeddedSecret)
    {
        sessionKey = null!;
        embeddedSecret = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var baseToken = token;
        var delimiterIndex = token.IndexOf(TokenSecretDelimiter, StringComparison.Ordinal);
        if (delimiterIndex >= 0)
        {
            var secretIndex = delimiterIndex + TokenSecretDelimiter.Length;
            if (secretIndex < token.Length)
            {
                embeddedSecret = token[secretIndex..];
            }

            baseToken = token[..delimiterIndex];
        }

        var atIndex = baseToken.IndexOf('@');
        var colonIndex = baseToken.LastIndexOf(':');
        if (atIndex <= 0 || colonIndex <= atIndex + 1 || colonIndex >= baseToken.Length - 1)
        {
            return false;
        }

        var userIdPart = baseToken[..atIndex];
        var hostPart = baseToken[(atIndex + 1)..colonIndex];
        var portPart = baseToken[(colonIndex + 1)..];

        if (!Guid.TryParse(userIdPart, out var userId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(hostPart))
        {
            return false;
        }

        if (!int.TryParse(portPart, out var port))
        {
            return false;
        }

        if (port <= 0)
        {
            return false;
        }

        sessionKey = new SessionKey(userId, hostPart, port);
        return true;
    }
}
