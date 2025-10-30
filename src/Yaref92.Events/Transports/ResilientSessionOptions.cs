using System;

namespace Yaref92.Events.Transports;

internal sealed class ResilientSessionOptions
{
    public bool RequireAuthentication { get; init; }

    public string? AuthenticationToken { get; init; }

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public TimeSpan SessionBufferWindow { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan BackoffInitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan BackoffMaxDelay { get; init; } = TimeSpan.FromSeconds(30);
}
