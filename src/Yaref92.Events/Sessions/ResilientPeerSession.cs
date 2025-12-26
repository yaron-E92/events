using Yaref92.Events.Abstractions;

namespace Yaref92.Events.Sessions;

internal sealed partial class ResilientPeerSession : IResilientPeerSession
{
    private readonly SessionState _state;
    private Action<ResilientPeerSession>? _disposed;

    public SessionOutboundBuffer OutboundBuffer { get; }

    public string AuthToken => throw new NotImplementedException();

    public SessionKey Key { get; }
    public ResilientSessionOptions Options { get; }

    public bool RemoteEndpointHasAuthenticated => _state.RemoteEndpointHasAuthenticated;

    public bool IsAnonymous { get; init; }

    internal event Action<ResilientPeerSession>? Disposed
    {
        add => _disposed += value;
        remove => _disposed -= value;
    }

    internal ResilientPeerSession(SessionKey sessionKey,
        ResilientSessionOptions options,
        SessionState? state)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentNullException.ThrowIfNull(options);

        Key = sessionKey;
        Options = options;
        _state = state ?? new SessionState(Key);
        OutboundBuffer = new SessionOutboundBuffer();
    }

    public void Touch()
    {
        _state.Touch();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            //await OutboundConnection.DumpBuffer().ConfigureAwait(false);
            OutboundBuffer.Dispose();
        }
        finally
        {
            Action<ResilientPeerSession>? handlers = Interlocked.Exchange(ref _disposed, null);
            handlers?.Invoke(this);
        }
    }

    public void RegisterAuthentication()
    {
        _state.RegisterAuthentication();
    }

    internal partial class SessionState { }
}
