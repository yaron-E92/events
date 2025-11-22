using System;
using System.Threading;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Connections;

using static Yaref92.Events.Abstractions.IInboundResilientConnection;

namespace Yaref92.Events.Sessions;

internal sealed partial class ResilientPeerSession : IResilientPeerSession
{
    private readonly SessionState _state;
    private readonly ResilientCompositSessionConnection _resilientConnection;
    private Action<ResilientPeerSession>? _disposed;

    public SessionOutboundBuffer OutboundBuffer { get; }

    public string AuthToken => _resilientConnection.InboundConnection.SessionToken;

    public ResilientCompositSessionConnection PersistentClient => _resilientConnection;

    public SessionKey Key { get; }
    public ResilientSessionOptions Options { get; }

    public bool RemoteEndpointHasAuthenticated => _state.RemoteEndpointHasAuthenticated;

    public IOutboundResilientConnection OutboundConnection => _resilientConnection.OutboundConnection;

    public IInboundResilientConnection InboundConnection => _resilientConnection.InboundConnection;

    public bool IsAnonymous { get; init; }

    event SessionFrameReceivedHandler? IResilientPeerSession.FrameReceived
    {
        add => InboundConnection.FrameReceived += value;
        remove => InboundConnection.FrameReceived -= value;
    }

    internal event Action<ResilientPeerSession>? Disposed
    {
        add => _disposed += value;
        remove => _disposed -= value;
    }

    public ResilientPeerSession(SessionKey sessionKey,
        ResilientSessionOptions options)
        : this(sessionKey,
            new ResilientCompositSessionConnection(sessionKey, options),
            options,
            null)
    {
    }

    internal ResilientPeerSession(SessionKey sessionKey,
        ResilientCompositSessionConnection connection,
        ResilientSessionOptions options,
        SessionState? state)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connection);

        Key = sessionKey;
        Options = options;
        _resilientConnection = connection;
        _state = state ?? new SessionState(Key);
        OutboundBuffer = OutboundConnection.OutboundBuffer;
    }

    public void Touch()
    {
        _state.Touch();
        InboundConnection.RecordRemoteActivity();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await OutboundConnection.DumpBuffer().ConfigureAwait(false);
            OutboundBuffer.Dispose();
            await _resilientConnection.DisposeAsync().ConfigureAwait(false);
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
