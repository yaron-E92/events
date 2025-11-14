using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaref92.Events.Sessions;

internal static class SessionFrameContract
{
    private static readonly JsonSerializerOptions AuthPayloadSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string TokenSecretDelimiter = "||";

    public static string CreateSessionToken(SessionKey sessionKey, ResilientSessionOptions options, string? authenticationSecret)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentNullException.ThrowIfNull(options);

        var prefix = sessionKey.ToString() ?? throw new InvalidOperationException("Session key produced an invalid sessionToken prefix.");
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
            throw new ArgumentException("Session sessionToken must be provided.", nameof(sessionToken));
        }

        var payload = new SessionAuthenticationPayload(
            options.RequireAuthentication ? ResolveSecret(authenticationSecret, options) : null,
            options.CallbackHost,
            options.CallbackPort > 0 ? options.CallbackPort : null);

        var payloadJson = JsonSerializer.Serialize(payload, AuthPayloadSerializerOptions);
        return SessionFrame.CreateAuth(sessionToken, payloadJson);
    }

    /// <summary>
    /// If the frame is an authentication frame, attempts to validate it according to the provided options.<br/>
    /// The session key is output if validation is successful.
    /// </summary>
    /// <param name="frame"></param>
    /// <param name="options"></param>
    /// <param name="sessionKey">An anonymous session will have an empty user id at the end of this method</param>
    /// <returns>true if the authentication is valid or not required, false otherwise</returns>
    /// <remarks>
    /// In the case that authentication is not required, or if it is generally but not for an anonymouse session and this is one,
    /// then the sessionToken does not need to contain a secret.
    /// </remarks>
    public static bool TryValidateAuthentication(
        SessionFrame frame,
        ResilientSessionOptions options,
        out SessionKey sessionKey,
        out SessionAuthenticationPayload? authenticationPayload)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        sessionKey = null!;
        authenticationPayload = null;
        if (frame.Kind != SessionFrameKind.Auth)
        {
            // TODO: Log invalid frame kind for authentication
            return false;
        }

        if (!TryParseSessionToken(frame.Token, out sessionKey, out string? embeddedSecret))
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

        if (sessionKey.IsAnonymousKey && !options.DoAnonymousSessionsRequireAuthentication)
        {
            return true;
        }

        authenticationPayload = ParseAuthenticationPayload(frame.Payload);
        var providedSecret = authenticationPayload?.Secret;
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

    /// <summary>
    /// Using the format "userId@host:port||secret" for the session token, attempts to parse the sessionToken into its components.
    /// It then constructs a <see cref="SessionKey"/> from the parsed components.
    /// </summary>
    /// <param name="sessionToken"></param>
    /// <param name="sessionKey"></param>
    /// <param name="embeddedAuthSecret"></param>
    /// <returns></returns>
    /// <remarks>A <see cref="Guid.Empty"/> means it is an anonymous session</remarks>
    public static bool TryParseSessionToken(string? sessionToken, out SessionKey sessionKey, out string? embeddedAuthSecret)
    {
        sessionKey = null!;
        embeddedAuthSecret = null;

        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return false;
        }

        var baseToken = sessionToken;
        var delimiterIndex = sessionToken.IndexOf(TokenSecretDelimiter, StringComparison.Ordinal);
        if (delimiterIndex >= 0)
        {
            var secretIndex = delimiterIndex + TokenSecretDelimiter.Length;
            if (secretIndex < sessionToken.Length)
            {
                embeddedAuthSecret = sessionToken[secretIndex..];
            }

            baseToken = sessionToken[..delimiterIndex];
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

        if (!TryParseUserId(userIdPart, out Guid userId))
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

        sessionKey = new SessionKey(userId, hostPart, port)
        {
            IsAnonymousKey = userId == Guid.Empty,
        };
        return true;
    }

    private static bool TryParseUserId(string userIdPart, out Guid userId)
    {
        if (string.IsNullOrWhiteSpace(userIdPart) || userIdPart.Equals("anonymous", StringComparison.OrdinalIgnoreCase))
        {
            userId = Guid.Empty;
            return true;
        }
        return Guid.TryParse(userIdPart, out userId);
    }

    private static SessionAuthenticationPayload? ParseAuthenticationPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SessionAuthenticationPayload>(payload, AuthPayloadSerializerOptions);
        }
        catch (JsonException)
        {
            return new SessionAuthenticationPayload(payload, null, null);
        }
    }
}

internal sealed record SessionAuthenticationPayload(string? Secret, string? CallbackHost, int? CallbackPort);
