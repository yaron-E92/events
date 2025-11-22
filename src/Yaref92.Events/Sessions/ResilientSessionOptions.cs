namespace Yaref92.Events.Sessions;

public sealed class ResilientSessionOptions
{
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultHeartbeatTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan DefaultSessionBufferWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultBackoffInitialDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultBackoffMaxDelay = TimeSpan.FromSeconds(30);
    public const int DefaultMaximalReconnectAttempts = 5;

    public bool RequireAuthentication { get; init; }
    public bool DoAnonymousSessionsRequireAuthentication { get; init; }

    public string? AuthenticationToken { get; init; }

    public TimeSpan HeartbeatInterval { get; init; } = DefaultHeartbeatInterval;

    public TimeSpan HeartbeatTimeout { get; init; } = DefaultHeartbeatTimeout;

    public TimeSpan SessionBufferWindow { get; init; } = DefaultSessionBufferWindow;

    public TimeSpan BackoffInitialDelay { get; init; } = DefaultBackoffInitialDelay;

    public TimeSpan BackoffMaxDelay { get; init; } = DefaultBackoffMaxDelay;

    public int MaximalReconnectAttempts { get; init; } = DefaultMaximalReconnectAttempts;

    /// <summary>
    /// Host name advertised to peers when establishing a session. Used by remote endpoints to dial back the sender.
    /// </summary>
    public string? CallbackHost { get; set; }

    /// <summary>
    /// Listener port advertised to peers when establishing a session. Used by remote endpoints to dial back the sender.
    /// </summary>
    public int CallbackPort { get; set; }

    /// <summary>
    /// Checks that all options are valid, returning false if not.
    /// </summary>
    internal bool Validate()
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
        return true;
    }
}
