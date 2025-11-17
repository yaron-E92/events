using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports;
using Yaref92.Events.Transports.ConnectionManagers;
using Yaref92.Events.UnitTests;

using static Yaref92.Events.Abstractions.IInboundResilientConnection;

namespace Yaref92.Events.UnitTests.Transports;

internal sealed class DeterministicEventSerializer : IEventSerializer
{
    internal const string ExpectedPayload = "deterministic-payload";

    public string Serialize<T>(T evt) where T : class, IDomainEvent =>
        throw new NotSupportedException();

    public (Type? type, IDomainEvent? domainEvent) Deserialize(string data) =>
        string.Equals(data, ExpectedPayload, StringComparison.Ordinal)
            ? (typeof(DummyEvent), new DummyEvent(data))
            : (null, null);
}

internal sealed class FakeInboundResilientConnection : IInboundResilientConnection
{
    private event SessionFrameReceivedHandler? FrameReceivedInternal;

    internal FakeInboundResilientConnection(SessionKey sessionKey)
    {
        SessionKey = sessionKey;
    }

    public SessionKey SessionKey { get; }

    public bool IsPastTimeout { get; set; }

    public int RemoteActivityCount { get; private set; }

    public event SessionFrameReceivedHandler? FrameReceived
    {
        add => FrameReceivedInternal += value;
        remove => FrameReceivedInternal -= value;
    }

    public Task<AcknowledgementState> WaitForAck(Guid eventId, CancellationToken cancellationToken) =>
        Task.FromResult(AcknowledgementState.None);

    public Task InitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AttachTransientConnection(TcpClient transientConnection, CancellationTokenSource incomingConnectionCts) =>
        Task.CompletedTask;

    public void RecordRemoteActivity()
    {
        RemoteActivityCount++;
    }

    internal Task RaiseFrameAsync(SessionFrame frame, CancellationToken token)
    {
        SessionFrameReceivedHandler? handler = FrameReceivedInternal;
        return handler is null ? Task.CompletedTask : handler(frame, SessionKey, token);
    }
}

internal sealed class FakeOutboundResilientConnection : IOutboundResilientConnection
{
    public FakeOutboundResilientConnection(SessionKey sessionKey)
    {
        SessionKey = sessionKey;
        OutboundBuffer = new SessionOutboundBuffer();
        AcknowledgedEventIds = new ConcurrentDictionary<Guid, AcknowledgementState>();
        RemoteEndPoint = new DnsEndPoint(sessionKey.Host, sessionKey.Port);
    }

    public SessionKey SessionKey { get; }

    public DnsEndPoint RemoteEndPoint { get; }

    public string OutboxPath => string.Empty;

    public ConcurrentDictionary<Guid, AcknowledgementState> AcknowledgedEventIds { get; }

    public SessionOutboundBuffer OutboundBuffer { get; }

    public List<SessionFrame> EnqueuedFrames { get; } = new();

    public List<Guid> AckedEvents { get; } = new();

    public int RefreshRequests { get; private set; }

    public bool RefreshResult { get; set; } = true;

    public Task DumpBuffer() => Task.CompletedTask;

    public void EnqueueFrame(SessionFrame frame)
    {
        EnqueuedFrames.Add(frame);
    }

    public Task InitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void OnAckReceived(Guid eventId)
    {
        AckedEvents.Add(eventId);
    }

    public Task<bool> RefreshConnectionAsync(CancellationToken token)
    {
        RefreshRequests++;
        return Task.FromResult(RefreshResult);
    }
}

internal sealed class FakeResilientPeerSession : IResilientPeerSession
{
    public FakeResilientPeerSession(SessionKey key, FakeInboundResilientConnection inbound, FakeOutboundResilientConnection outbound, bool isAnonymous = false, bool remoteEndpointHasAuthenticated = true)
    {
        Key = key;
        InboundConnection = inbound;
        OutboundConnection = outbound;
        OutboundBuffer = outbound.OutboundBuffer;
        IsAnonymous = isAnonymous;
        RemoteEndpointHasAuthenticated = remoteEndpointHasAuthenticated;
    }

    public SessionKey Key { get; }

    public string AuthToken => string.Empty;

    public bool IsAnonymous { get; }

    public IOutboundResilientConnection OutboundConnection { get; }

    public IInboundResilientConnection InboundConnection { get; }

    public bool RemoteEndpointHasAuthenticated { get; }

    public SessionOutboundBuffer OutboundBuffer { get; }

    public int TouchCount { get; private set; }

    public int AuthenticationRegistrations { get; private set; }

    public event IInboundResilientConnection.SessionFrameReceivedHandler? FrameReceived
    {
        add => InboundConnection.FrameReceived += value;
        remove => InboundConnection.FrameReceived -= value;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void RegisterAuthentication()
    {
        AuthenticationRegistrations++;
    }

    public void Touch()
    {
        TouchCount++;
    }
}

internal static class SessionManagerTestExtensions
{
    private static readonly FieldInfo SessionsField = typeof(SessionManager).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!;

    internal static void InjectSession(this SessionManager sessionManager, IResilientPeerSession session)
    {
        var sessions = (ConcurrentDictionary<SessionKey, IResilientPeerSession>)SessionsField.GetValue(sessionManager)!;
        sessions[session.Key] = session;
    }
}

internal static class InboundConnectionManagerTestExtensions
{
    private static readonly MethodInfo EnsureFrameHandlerSubscribedMethod = typeof(InboundConnectionManager).GetMethod("EnsureFrameHandlerSubscribed", BindingFlags.NonPublic | BindingFlags.Instance)!;

    internal static void AttachFrameHandler(this InboundConnectionManager manager, IResilientPeerSession session)
    {
        EnsureFrameHandlerSubscribedMethod.Invoke(manager, new object[] { session });
    }
}
