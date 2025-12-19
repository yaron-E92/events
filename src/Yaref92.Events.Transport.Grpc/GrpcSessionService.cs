using System.Text.Json;
using System.Threading.Channels;
using Grpc.Core;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Serialization;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transport.Grpc.Models;
using Yaref92.Events.Transport.Grpc.Protos;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Transport.Grpc;

public sealed class GrpcSessionService : SessionTransport.SessionTransportBase
{
    private readonly IEventSerializer _serializer;
    private readonly ResilientSessionOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SessionKey _defaultSessionKey = new(Guid.Empty, "grpc", 0);

    public GrpcSessionService(IEventSerializer? serializer = null, ResilientSessionOptions? options = null)
    {
        _serializer = serializer ?? new JsonEventSerializer();
        _options = options ?? new ResilientSessionOptions();
    }

    public event Func<IDomainEvent, Task<bool>>? EventReceived;

    public event Func<IDomainEvent, SessionKey, Task>? InboundEventReceived;

    public event Func<Guid, SessionKey, Task>? InboundAckReceived;

    public event Func<SessionKey, Task>? InboundPingReceived;

    public event Func<Guid, Task>? AckReceived;

    public event Func<SessionKey, CancellationToken, Task>? SessionConnectionAccepted;

    public event Func<SessionKey, CancellationToken, Task>? SessionConnectionClosed;

    public override async Task Connect(
        IAsyncStreamReader<SessionFrame> requestStream,
        IServerStreamWriter<SessionFrame> responseStream,
        ServerCallContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        string sessionId = Guid.NewGuid().ToString("D");
        SessionKey sessionKey = ResolveSessionKey(context) ?? _defaultSessionKey;
        var authState = new StreamAuthenticationState(_options.RequireAuthentication);

        var outbound = Channel.CreateUnbounded<SessionFrame>();
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeatLoop = RunHeartbeatAsync(sessionId, outbound.Writer, heartbeatCts.Token);

        Task responsePump = PumpOutboundAsync(outbound.Reader, responseStream, cancellationToken);

        try
        {
            if (SessionConnectionAccepted is not null)
            {
                await SessionConnectionAccepted.Invoke(sessionKey, cancellationToken).ConfigureAwait(false);
            }

            await foreach (SessionFrame incoming in requestStream.ReadAllAsync(cancellationToken))
            {
                GrpcSessionFrame frame = GrpcSessionFrame.FromProto(incoming);
                switch (frame.Kind)
                {
                    case GrpcSessionFrameKind.Auth:
                        ValidateAuthentication(frame.Auth);
                        sessionId = frame.SessionId ?? sessionId;
                        authState.MarkAuthenticated();
                        break;
                    case GrpcSessionFrameKind.Ping:
                        authState.EnsureAuthenticated();
                        if (InboundPingReceived is not null)
                        {
                            await InboundPingReceived.Invoke(sessionKey).ConfigureAwait(false);
                        }
                        await outbound.Writer.WriteAsync(CreatePong(sessionId), cancellationToken).ConfigureAwait(false);
                        break;
                    case GrpcSessionFrameKind.Message:
                        authState.EnsureAuthenticated();
                        await OnMessageReceivedAsync(frame, sessionId, sessionKey, outbound.Writer, cancellationToken).ConfigureAwait(false);
                        break;
                    case GrpcSessionFrameKind.Ack:
                        authState.EnsureAuthenticated();
                        await OnAckReceivedAsync(frame.Ack!, sessionKey, cancellationToken).ConfigureAwait(false);
                        break;
                    case GrpcSessionFrameKind.Pong:
                        authState.EnsureAuthenticated();
                        // keep-alive; no-op
                        break;
                }
            }
        }
        finally
        {
            heartbeatCts.Cancel();
            outbound.Writer.TryComplete();
            await Task.WhenAll(responsePump, heartbeatLoop).ConfigureAwait(false);

            if (SessionConnectionClosed is not null)
            {
                await SessionConnectionClosed.Invoke(sessionKey, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void ValidateAuthentication(GrpcAuthPayload? auth)
    {
        if (!_options.RequireAuthentication)
        {
            return;
        }

        if (auth is null || string.IsNullOrWhiteSpace(auth.Secret))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Authentication required."));
        }

        if (!string.Equals(auth.Secret, _options.AuthenticationToken, StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid authentication secret."));
        }
    }

    private Task PumpOutboundAsync(ChannelReader<SessionFrame> reader, IServerStreamWriter<SessionFrame> writer, CancellationToken token)
    {
        return Task.Run(async () =>
        {
            await foreach (SessionFrame frame in reader.ReadAllAsync(token))
            {
                await writer.WriteAsync(frame).ConfigureAwait(false);
            }
        }, token);
    }

    private async Task RunHeartbeatAsync(string sessionId, ChannelWriter<SessionFrame> writer, CancellationToken token)
    {
        TimeSpan interval = _options.HeartbeatInterval;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
                await writer.WriteAsync(CreatePing(sessionId), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task OnMessageReceivedAsync(
        GrpcSessionFrame frame,
        string sessionId,
        SessionKey sessionKey,
        ChannelWriter<SessionFrame> writer,
        CancellationToken cancellationToken)
    {
        if (frame.Message is null)
        {
            return;
        }

        EventEnvelope envelope = frame.Message.Envelope;
        string envelopeJson = JsonSerializer.Serialize(envelope, _jsonOptions);
        (_, IDomainEvent? domainEvent) = _serializer.Deserialize(envelopeJson);

        if (domainEvent is null)
        {
            return;
        }

        if (InboundEventReceived is not null)
        {
            await InboundEventReceived.Invoke(domainEvent, sessionKey).ConfigureAwait(false);
        }

        Func<IDomainEvent, Task<bool>>? handler = EventReceived;
        if (handler is null)
        {
            return;
        }

        bool handled = await handler(domainEvent).ConfigureAwait(false);
        if (handled)
        {
            SessionFrame ackFrame = GrpcSessionFrame.ToProto(new GrpcSessionFrame(
                GrpcSessionFrameKind.Ack,
                sessionId,
                null,
                null,
                new GrpcAckPayload(frame.Message.EventId, frame.Message.DeduplicationId)));

            await writer.WriteAsync(ackFrame, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OnAckReceivedAsync(GrpcAckPayload ack, SessionKey sessionKey, CancellationToken token)
    {
        if (InboundAckReceived is not null)
        {
            await InboundAckReceived.Invoke(ack.EventId, sessionKey).ConfigureAwait(false);
        }

        Func<Guid, Task>? handler = AckReceived;
        if (handler is null)
        {
            return;
        }

        await handler(ack.EventId).ConfigureAwait(false);
    }

    private static SessionFrame CreatePing(string sessionId)
    {
        return GrpcSessionFrame.ToProto(new GrpcSessionFrame(GrpcSessionFrameKind.Ping, sessionId, null, null, null));
    }

    private static SessionFrame CreatePong(string sessionId)
    {
        return GrpcSessionFrame.ToProto(new GrpcSessionFrame(GrpcSessionFrameKind.Pong, sessionId, null, null, null));
    }

    private static SessionKey? ResolveSessionKey(ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Peer))
        {
            return null;
        }

        if (!TryParsePeer(context.Peer, out string host, out int port))
        {
            return null;
        }

        return new SessionKey(Guid.Empty, host, port);
    }

    private static bool TryParsePeer(string peer, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        int separatorIndex = peer.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == peer.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(peer[(separatorIndex + 1)..], out port))
        {
            return false;
        }

        string rawHost = peer[..separatorIndex];
        int hostSeparator = rawHost.LastIndexOf(':');
        if (hostSeparator >= 0)
        {
            rawHost = rawHost[(hostSeparator + 1)..];
        }

        host = rawHost.Trim('[', ']');
        return !string.IsNullOrWhiteSpace(host);
    }

    private sealed class StreamAuthenticationState
    {
        private bool _isAuthenticated;

        public StreamAuthenticationState(bool requiresAuthentication)
        {
            _isAuthenticated = !requiresAuthentication;
        }

        public void MarkAuthenticated()
        {
            _isAuthenticated = true;
        }

        public void EnsureAuthenticated()
        {
            if (_isAuthenticated)
            {
                return;
            }

            throw new RpcException(new Status(StatusCode.Unauthenticated, "Authentication required."));
        }
    }
}
