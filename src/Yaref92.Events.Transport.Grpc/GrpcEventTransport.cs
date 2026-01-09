using System.Collections.Concurrent;
using System.Net;

using Grpc.Core;
using Grpc.Net.Client;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Serialization;
using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transport.Grpc;

public sealed partial class GrpcEventTransport : IEventTransport, IAsyncDisposable
{
    internal enum TransportMode
    {
        Grpc,
        WebRtcDataChannel,
    }

    private readonly int _listenPort;
    private readonly IEventSerializer _serializer;
    private readonly ConcurrentDictionary<Guid, StreamRegistration> _activeStreams = new();
    private readonly ConcurrentBag<GrpcChannel> _channels = new();
    private readonly string? _authenticationSecret;
    private Task? _disposeTask;
    private int _disposeState;
    private Platform? _localPlatform;
    private Platform? _targetPlatform;

    internal ISessionManager SessionManager { get; }

    private event Func<IDomainEvent, Task<bool>>? EventReceived;

    event Func<IDomainEvent, Task<bool>> IEventTransport.EventReceived
    {
        add => EventReceived += value;
        remove => EventReceived -= value;
    }

    public event IEventTransport.SessionInboundConnectionDroppedHandler? SessionInboundConnectionDropped;

    public Platform? LocalPlatform
    {
        get => _localPlatform ?? SessionManager.Options.LocalPlatform;
        set
        {
            _localPlatform = value;
            SessionManager.Options.LocalPlatform = value;
        }
    }

    public Platform? TargetPlatform
    {
        get => _targetPlatform ?? SessionManager.Options.TargetPlatform;
        set
        {
            _targetPlatform = value;
            SessionManager.Options.TargetPlatform = value;
        }
    }

    public GrpcEventTransport(
        int listenPort,
        ISessionManager sessionManager,
        IEventSerializer? serializer = null,
        string? authenticationSecret = null,
        Platform? localPlatform = null,
        Platform? targetPlatform = null)
    {
        _listenPort = listenPort;
        SessionManager = sessionManager;
        _serializer = serializer ?? new JsonEventSerializer();
        _authenticationSecret = authenticationSecret;
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        if (!string.IsNullOrWhiteSpace(authenticationSecret)
            && string.IsNullOrWhiteSpace(SessionManager.Options.AuthenticationToken))
        {
            SessionManager.Options.AuthenticationToken = authenticationSecret;
        }
        if (localPlatform.HasValue)
        {
            LocalPlatform = localPlatform;
        }

        if (targetPlatform.HasValue)
        {
            TargetPlatform = targetPlatform;
        }
    }
#if !ANDROID && !NOT_ANDROID
    public Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("StartListeningAsync is only supported on Android and non-Android platforms. If somehow this is accessed something went wrong in configuration");
    }
#endif

    public Task ConnectToPeerAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        return ConnectToPeerAsync(Guid.Empty, host, port, cancellationToken);
    }

    public async Task ConnectToPeerAsync(Guid userId, string host, int port, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host cannot be null or whitespace.", nameof(host));
        }

        SessionKey sessionKey = new(userId, host, port)
        {
            IsAnonymousKey = userId == Guid.Empty,
        };
        if (sessionKey.IsAnonymousKey)
        {
            SessionManager.HydrateAnonymousSessionId(sessionKey, new DnsEndPoint(host, port));
        }

        var transportMode = ResolveTransportMode(sessionKey);

        if (transportMode == TransportMode.WebRtcDataChannel
            && await TryConnectToPeerViaWebRtcAsync(sessionKey, host, port, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await ConnectToPeerViaGrpcAsync(sessionKey, host, port, cancellationToken).ConfigureAwait(false);
    }

    private TransportMode ResolveTransportMode(SessionKey sessionKey)
    {
        if (RequiresServerBasedConnection(sessionKey))
        {
            return TransportMode.Grpc;
        }

        return ShouldUseWebRtcForTarget() ? TransportMode.WebRtcDataChannel : TransportMode.Grpc;
    }

    private bool RequiresServerBasedConnection(SessionKey sessionKey)
    {
        var options = SessionManager.Options;
        if (!options.RequireAuthentication)
        {
            return false;
        }

        if (!sessionKey.IsAnonymousKey)
        {
            return true;
        }

        return options.DoAnonymousSessionsRequireAuthentication;
    }

    private bool ShouldUseWebRtcForTarget(Platform? targetPlatform = null)
    {
        var resolvedTarget = targetPlatform ?? _targetPlatform ?? SessionManager.Options.TargetPlatform;
        return resolvedTarget == Platform.Android;
    }

    private TransportMode ResolveTransportMode(SessionKey sessionKey, Platform? targetPlatform)
    {
        if (RequiresServerBasedConnection(sessionKey))
        {
            return TransportMode.Grpc;
        }

        return ShouldUseWebRtcForTarget(targetPlatform) ? TransportMode.WebRtcDataChannel : TransportMode.Grpc;
    }

    private void SyncPlatformOptions()
    {
        if (_localPlatform.HasValue)
        {
            SessionManager.Options.LocalPlatform = _localPlatform;
        }

        if (_targetPlatform.HasValue)
        {
            SessionManager.Options.TargetPlatform = _targetPlatform;
        }
    }

    private TransportFrame CreateAuthFrame(SessionKey sessionKey)
    {
        SyncPlatformOptions();
        var sessionToken = SessionFrameContract.CreateSessionToken(sessionKey, SessionManager.Options, _authenticationSecret);
        var authFrame = SessionFrameContract.CreateAuthFrame(sessionToken, SessionManager.Options, _authenticationSecret);
        return new TransportFrame
        {
            EventId = authFrame.Token ?? string.Empty,
            EventJson = authFrame.Payload,
            Kind = FrameKind.Auth,
        };
    }

    private async Task ConnectToPeerViaGrpcAsync(SessionKey sessionKey, string host, int port, CancellationToken cancellationToken)
    {
        var channel = GrpcChannel.ForAddress($"http://{host}:{port}");
        _channels.Add(channel);

        var client = new EventStream.EventStreamClient(channel);
        var call = client.Connect(cancellationToken: cancellationToken);
        var registration = RegisterStream(call.RequestStream, null);
        await SendAuthFrameAsync(registration, sessionKey).ConfigureAwait(false);
        _ = ProcessIncomingStreamAsync(call.ResponseStream, registration, cancellationToken)
            .ContinueWith(_ => UnregisterStream(registration), TaskScheduler.Default);
    }

    public async Task PublishEventAsync<T>(T domainEvent, CancellationToken cancellationToken = default) where T : class, IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var eventEnvelopeJson = _serializer.Serialize(domainEvent);
        var publishTasks = new List<Task>();

        foreach (var registration in _activeStreams.Values)
        {
            publishTasks.Add(WriteFrameAsync(registration, CreateEventFrame(domainEvent, eventEnvelopeJson)));
        }

        await Task.WhenAll(publishTasks).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) == 0)
        {
            _disposeTask = DisposeAsyncCore();
        }

        return _disposeTask is null ? ValueTask.CompletedTask : new ValueTask(_disposeTask);
    }
#if !ANDROID && !NOT_ANDROID
    private Task DisposeAsyncCore()
    {
        return Task.CompletedTask;
    }
#endif
    private StreamRegistration RegisterStream(IAsyncStreamWriter<TransportFrame> writer, EndPoint? remoteEndPoint)
    {
        var registration = new StreamRegistration(Guid.NewGuid(), writer, remoteEndPoint);
        _activeStreams.TryAdd(registration.Id, registration);
        return registration;
    }

    private void UnregisterStream(StreamRegistration registration)
    {
        if (_activeStreams.TryRemove(registration.Id, out _))
        {
            registration.Dispose();
        }
    }

    private async Task ProcessIncomingStreamAsync(
        IAsyncStreamReader<TransportFrame> reader,
        StreamRegistration registration,
        CancellationToken cancellationToken)
    {
        var authFailed = false;
        try
        {
            while (await reader.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                if (!await HandleIncomingFrameAsync(reader.Current, registration).ConfigureAwait(false))
                {
                    authFailed = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (authFailed)
            {
                UnregisterStream(registration);
            }
        }
    }

    private async Task<bool> HandleIncomingFrameAsync(TransportFrame frame, StreamRegistration registration)
    {
        if (frame.Kind == FrameKind.Auth)
        {
            return await HandleAuthFrameAsync(frame, registration).ConfigureAwait(false);
        }

        if (frame.Kind != FrameKind.Event)
        {
            return true;
        }

        if (!registration.IsAuthenticated && SessionManager.Options.RequireAuthentication)
        {
            return true;
        }

        var (_, domainEvent) = _serializer.Deserialize(frame.EventJson);
        if (domainEvent is null)
        {
            return true;
        }

        var handler = EventReceived;
        if (handler is null)
        {
            return true;
        }

        bool eventReceivedSuccessfully = await handler(domainEvent).ConfigureAwait(false);
        if (eventReceivedSuccessfully)
        {
            await WriteFrameAsync(registration, new TransportFrame
            {
                EventId = frame.EventId,
                Kind = FrameKind.Ack,
            }).ConfigureAwait(false);
        }

        return true;
    }

    private static async Task WriteFrameAsync(StreamRegistration registration, TransportFrame frame)
    {
        await registration.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await registration.Writer.WriteAsync(frame).ConfigureAwait(false);
        }
        finally
        {
            registration.WriteLock.Release();
        }
    }

    private Task SendAuthFrameAsync(StreamRegistration registration, SessionKey sessionKey)
    {
        return WriteFrameAsync(registration, CreateAuthFrame(sessionKey));
    }

    private async Task<bool> HandleAuthFrameAsync(TransportFrame frame, StreamRegistration registration)
    {
        var authFrame = SessionFrame.CreateAuth(frame.EventId ?? string.Empty, frame.EventJson);
        try
        {
            var session = SessionManager.ResolveSession(registration.RemoteEndPoint, authFrame);
            registration.IsAuthenticated = true;
            try
            {
                await InitiateReverseConnectionAsync(session, authFrame).ConfigureAwait(false);
            }
            catch
            {
                // Reverse dial is best-effort; keep inbound stream alive even if it fails.
            }
            return true;
        }
        catch (System.Security.Authentication.AuthenticationException)
        {
            registration.IsAuthenticated = false;
            return false;
        }
    }

    private async Task InitiateReverseConnectionAsync(IResilientPeerSession session, SessionFrame authFrame)
    {
        if (!SessionFrameContract.TryValidateAuthentication(authFrame, SessionManager.Options, out _, out var payload))
        {
            return;
        }

        var sessionKey = session.Key;
        var callbackHost = string.IsNullOrWhiteSpace(payload?.CallbackHost) ? sessionKey.Host : payload.CallbackHost;
        var callbackPort = payload?.CallbackPort ?? sessionKey.Port;
        if (string.IsNullOrWhiteSpace(callbackHost) || callbackPort <= 0)
        {
            return;
        }

        var transportMode = ResolveTransportMode(sessionKey, payload?.LocalPlatform ?? payload?.TargetPlatform);
        if (transportMode == TransportMode.WebRtcDataChannel
            && await TryConnectToPeerViaWebRtcAsync(sessionKey, callbackHost, callbackPort, CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }

        await ConnectToPeerViaGrpcAsync(sessionKey, callbackHost, callbackPort, CancellationToken.None).ConfigureAwait(false);
    }

    private static TransportFrame CreateEventFrame<T>(T domainEvent, string eventEnvelopeJson) where T : class, IDomainEvent
    {
        return new TransportFrame
        {
            EventId = domainEvent.EventId.ToString("D"),
            TypeName = domainEvent.GetType().AssemblyQualifiedName ?? string.Empty,
            EventJson = eventEnvelopeJson,
            Kind = FrameKind.Event,
        };
    }

    private sealed class EventStreamService : EventStream.EventStreamBase
    {
        private readonly GrpcEventTransport _transport;

        public EventStreamService(GrpcEventTransport transport)
        {
            _transport = transport;
        }

        public override async Task Connect(
            IAsyncStreamReader<TransportFrame> requestStream,
            IServerStreamWriter<TransportFrame> responseStream,
            ServerCallContext context)
        {
            var registration = _transport.RegisterStream(responseStream, TryParseRemoteEndPoint(context.Peer));
            try
            {
                await _transport.ProcessIncomingStreamAsync(requestStream, registration, context.CancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _transport.UnregisterStream(registration);
            }
        }
    }

    private static EndPoint? TryParseRemoteEndPoint(string? peer)
    {
        if (string.IsNullOrWhiteSpace(peer))
        {
            return null;
        }

        var schemeSeparator = peer.IndexOf(':');
        if (schemeSeparator <= 0 || schemeSeparator == peer.Length - 1)
        {
            return null;
        }

        var scheme = peer[..schemeSeparator];
        var address = peer[(schemeSeparator + 1)..];

        if (!TrySplitHostPort(address, out var host, out var port))
        {
            return null;
        }

        if (scheme.Equals("dns", StringComparison.OrdinalIgnoreCase))
        {
            return new DnsEndPoint(host, port);
        }

        if (!IPAddress.TryParse(host, out var ipAddress))
        {
            return null;
        }

        if (scheme.Equals("ipv4", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("ipv6", StringComparison.OrdinalIgnoreCase))
        {
            return new IPEndPoint(ipAddress, port);
        }

        return null;
    }

    private static bool TrySplitHostPort(string address, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        if (address[0] == '[')
        {
            var closingBracket = address.IndexOf(']');
            if (closingBracket <= 0 || closingBracket >= address.Length - 2 || address[closingBracket + 1] != ':')
            {
                return false;
            }

            host = address[1..closingBracket];
            return int.TryParse(address[(closingBracket + 2)..], out port);
        }

        var lastColon = address.LastIndexOf(':');
        if (lastColon <= 0 || lastColon >= address.Length - 1)
        {
            return false;
        }

        host = address[..lastColon];
        return int.TryParse(address[(lastColon + 1)..], out port);
    }

    private sealed class StreamRegistration : IDisposable
    {
        public StreamRegistration(Guid id, IAsyncStreamWriter<TransportFrame> writer, EndPoint? remoteEndPoint)
        {
            Id = id;
            Writer = writer;
            WriteLock = new SemaphoreSlim(1, 1);
            RemoteEndPoint = remoteEndPoint;
        }

        public Guid Id { get; }

        public IAsyncStreamWriter<TransportFrame> Writer { get; }

        public SemaphoreSlim WriteLock { get; }

        public EndPoint? RemoteEndPoint { get; }

        public bool IsAuthenticated { get; set; }

        public void Dispose()
        {
            WriteLock.Dispose();
        }
    }
}
