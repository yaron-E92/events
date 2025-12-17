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

    public GrpcSessionService(IEventSerializer? serializer = null, ResilientSessionOptions? options = null)
    {
        _serializer = serializer ?? new JsonEventSerializer();
        _options = options ?? new ResilientSessionOptions();
    }

    public event Func<IDomainEvent, Task<bool>>? EventReceived;

    public event Func<Guid, Task>? AckReceived;

    public override async Task Connect(
        IAsyncStreamReader<SessionFrame> requestStream,
        IServerStreamWriter<SessionFrame> responseStream,
        ServerCallContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        string sessionId = Guid.NewGuid().ToString("D");

        var outbound = Channel.CreateUnbounded<SessionFrame>();
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeatLoop = RunHeartbeatAsync(sessionId, outbound.Writer, heartbeatCts.Token);

        Task responsePump = PumpOutboundAsync(outbound.Reader, responseStream, cancellationToken);

        try
        {
            await foreach (SessionFrame incoming in requestStream.ReadAllAsync(cancellationToken))
            {
                GrpcSessionFrame frame = GrpcSessionFrame.FromProto(incoming);
                switch (frame.Kind)
                {
                    case GrpcSessionFrameKind.Auth:
                        ValidateAuthentication(frame.Auth);
                        sessionId = frame.SessionId ?? sessionId;
                        break;
                    case GrpcSessionFrameKind.Ping:
                        await outbound.Writer.WriteAsync(CreatePong(sessionId), cancellationToken).ConfigureAwait(false);
                        break;
                    case GrpcSessionFrameKind.Message:
                        await OnMessageReceivedAsync(frame, sessionId, outbound.Writer, cancellationToken).ConfigureAwait(false);
                        break;
                    case GrpcSessionFrameKind.Ack:
                        await OnAckReceivedAsync(frame.Ack!, cancellationToken).ConfigureAwait(false);
                        break;
                    case GrpcSessionFrameKind.Pong:
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

    private async Task OnMessageReceivedAsync(GrpcSessionFrame frame, string sessionId, ChannelWriter<SessionFrame> writer, CancellationToken cancellationToken)
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

    private Task OnAckReceivedAsync(GrpcAckPayload ack, CancellationToken token)
    {
        Func<Guid, Task>? handler = AckReceived;
        if (handler is null)
        {
            return Task.CompletedTask;
        }

        return handler(ack.EventId);
    }

    private static SessionFrame CreatePing(string sessionId)
    {
        return GrpcSessionFrame.ToProto(new GrpcSessionFrame(GrpcSessionFrameKind.Ping, sessionId, null, null, null));
    }

    private static SessionFrame CreatePong(string sessionId)
    {
        return GrpcSessionFrame.ToProto(new GrpcSessionFrame(GrpcSessionFrameKind.Pong, sessionId, null, null, null));
    }
}
