namespace Yaref92.Events.Sessions;

public sealed class ResilientSessionOptions
{
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultHeartbeatTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan DefaultSessionBufferWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultBackoffInitialDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultBackoffMaxDelay = TimeSpan.FromSeconds(30);
    public const int DefaultMaximalReconnectAttempts = 5;
    public const int DefaultMaxFrameBytes = 1024 * 1024;
    public const int DefaultMaxInboundConnections = 128;
    public const int DefaultMaxRetainedSessions = 512;
    public const int DefaultMaxFramesPerSecondPerPeer = 100;
    public const int DefaultFrameRateBurstAllowance = 20;

    public bool RequireAuthentication { get; init; }
    public bool DoAnonymousSessionsRequireAuthentication { get; init; }

    public string? AuthenticationToken { get; set; }

    public TimeSpan HeartbeatInterval { get; init; } = DefaultHeartbeatInterval;

    public TimeSpan HeartbeatTimeout { get; init; } = DefaultHeartbeatTimeout;

    public TimeSpan SessionBufferWindow { get; init; } = DefaultSessionBufferWindow;

    public TimeSpan BackoffInitialDelay { get; init; } = DefaultBackoffInitialDelay;

    public TimeSpan BackoffMaxDelay { get; init; } = DefaultBackoffMaxDelay;

    public int MaximalReconnectAttempts { get; init; } = DefaultMaximalReconnectAttempts;

    /// <summary>Maximum accepted length-prefixed session frame payload, in bytes.</summary>
    public int MaxFrameBytes { get; init; } = DefaultMaxFrameBytes;

    /// <summary>Maximum simultaneous inbound handshakes and active transient inbound connections.</summary>
    public int MaxInboundConnections { get; init; } = DefaultMaxInboundConnections;

    /// <summary>Maximum number of peer sessions retained in memory at once.</summary>
    public int MaxRetainedSessions { get; init; } = DefaultMaxRetainedSessions;

    /// <summary>Maximum sustained frame rate accepted from one remote IP address.</summary>
    public int MaxFramesPerSecondPerPeer { get; init; } = DefaultMaxFramesPerSecondPerPeer;

    /// <summary>Additional frames one remote IP address may send during a one-second rate window.</summary>
    public int FrameRateBurstAllowance { get; init; } = DefaultFrameRateBurstAllowance;

    /// <summary>
    /// Host name advertised to peers when establishing a session. Used by remote endpoints to dial back the sender.
    /// </summary>
    public string? CallbackHost { get; set; }

    /// <summary>
    /// Listener port advertised to peers when establishing a session. Used by remote endpoints to dial back the sender.
    /// </summary>
    public int CallbackPort { get; set; }

    /// <summary>
    /// Local platform advertised to peers during authentication.
    /// </summary>
    public Platform? LocalPlatform { get; set; }

    /// <summary>
    /// Target platform selection advertised to peers during authentication.
    /// </summary>
    public Platform? TargetPlatform { get; set; }

    /// <summary>
    /// Checks that all options are valid, returning false if not.
    /// </summary>
    public bool Validate()
    {
        if (HeartbeatInterval <= TimeSpan.Zero)
        {
            return false;
        }
        if (HeartbeatTimeout <= HeartbeatInterval)
        {
            return false;
        }
        if (SessionBufferWindow < TimeSpan.Zero)
        {
            return false;
        }
        if (BackoffInitialDelay <= TimeSpan.Zero)
        {
            return false;
        }
        if (BackoffMaxDelay < BackoffInitialDelay)
        {
            return false;
        }
        if (MaximalReconnectAttempts < 0)
        {
            return false;
        }
        if (MaxFrameBytes <= 0
            || MaxInboundConnections <= 0
            || MaxRetainedSessions <= 0
            || MaxFramesPerSecondPerPeer <= 0
            || FrameRateBurstAllowance < 0)
        {
            return false;
        }
        if ((long)MaxFramesPerSecondPerPeer + FrameRateBurstAllowance > int.MaxValue)
        {
            return false;
        }
        return true;
    }
}
