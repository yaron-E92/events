using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Serialization;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transport.Grpc.Models;
using Yaref92.Events.Transport.Grpc.Protos;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Transport.Grpc;

public sealed class GrpcClientConnectionManager : IAsyncDisposable
{
    private readonly IEventSerializer _serializer;
    private readonly ResilientSessionOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingAcks = new();
    private readonly Channel<GrpcSessionFrame> _outgoingFrames = Channel.CreateUnbounded<GrpcSessionFrame>();
    private readonly CancellationTokenSource _cts = new();

    private AsyncDuplexStreamingCall<SessionFrame, SessionFrame>? _activeCall;
    private Task? _sendLoop;
    private Task? _receiveLoop;
    private Task? _heartbeatLoop;

    public event Func<IDomainEvent, Task<bool>>? EventReceived;

    public GrpcClientConnectionManager(IEventSerializer? serializer = null, ResilientSessionOptions? options = null)
    {
        _serializer = serializer ?? new JsonEventSerializer();
        _options = options ?? new ResilientSessionOptions();
    }

    public async Task ConnectAsync(string host, int port, string? authenticationSecret = null, CancellationToken cancellationToken = default)
    {
        var address = new UriBuilder(Uri.UriSchemeHttp, host, port).Uri;
        var client = new SessionTransport.SessionTransportClient(GrpcChannel.ForAddress(address));

        await EnsureConnectedAsync(client, authenticationSecret, cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishAsync<T>(T domainEvent, string? deduplicationId = null, CancellationToken cancellationToken = default)
        where T : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        string envelopePayload = _serializer.Serialize(domainEvent);
        EventEnvelope envelope = JsonSerializer.Deserialize<EventEnvelope>(envelopePayload, _jsonOptions)
            ?? throw new InvalidOperationException("Unable to serialize event envelope for transmission.");

        GrpcSessionFrame frame = new(
            GrpcSessionFrameKind.Message,
            null,
            null,
            new GrpcMessagePayload(domainEvent.EventId, envelope, deduplicationId),
            null);

        var ackCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAcks.TryAdd(frame.Message!.DeduplicationCorrelation, ackCompletion);

        await _outgoingFrames.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

        using var registration = cancellationToken.Register(() => ackCompletion.TrySetCanceled(cancellationToken));
        await ackCompletion.Task.ConfigureAwait(false);
    }

    private async Task EnsureConnectedAsync(SessionTransport.SessionTransportClient client, string? authenticationSecret, CancellationToken cancellationToken)
    {
        TimeSpan currentDelay = _options.BackoffInitialDelay;

        while (!_cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                _activeCall = client.Connect(cancellationToken: cancellationToken);
                await SendAuthenticationAsync(authenticationSecret, cancellationToken).ConfigureAwait(false);
                _sendLoop = Task.Run(() => SendLoopAsync(_cts.Token), _cts.Token);
                _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
                _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_cts.Token), _cts.Token);
                return;
            }
            catch when (!cancellationToken.IsCancellationRequested && !_cts.IsCancellationRequested)
            {
                await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
                currentDelay = TimeSpan.FromTicks(Math.Min(_options.BackoffMaxDelay.Ticks, currentDelay.Ticks * 2));
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, token).ConfigureAwait(false);
                await _outgoingFrames.Writer.WriteAsync(
                        new GrpcSessionFrame(GrpcSessionFrameKind.Ping, null, null, null, null),
                        token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SendAuthenticationAsync(string? authenticationSecret, CancellationToken cancellationToken)
    {
        if (!_options.RequireAuthentication && string.IsNullOrWhiteSpace(authenticationSecret))
        {
            return;
        }

        GrpcSessionFrame authFrame = new(
            GrpcSessionFrameKind.Auth,
            null,
            new GrpcAuthPayload(string.Empty, authenticationSecret ?? _options.AuthenticationToken, null),
            null,
            null);

        await _outgoingFrames.Writer.WriteAsync(authFrame, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        if (_activeCall is null)
        {
            return;
        }

        var writer = _activeCall.RequestStream;
        while (!token.IsCancellationRequested)
        {
            try
            {
                GrpcSessionFrame frame = await _outgoingFrames.Reader.ReadAsync(token).ConfigureAwait(false);
                await writer.WriteAsync(GrpcSessionFrame.ToProto(frame)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                break;
            }
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        if (_activeCall is null)
        {
            return;
        }

        var reader = _activeCall.ResponseStream;
        while (!token.IsCancellationRequested && await reader.MoveNext(token).ConfigureAwait(false))
        {
            GrpcSessionFrame frame = GrpcSessionFrame.FromProto(reader.Current);
            switch (frame.Kind)
            {
                case GrpcSessionFrameKind.Ack:
                    ResolveAck(frame.Ack);
                    break;
                case GrpcSessionFrameKind.Ping:
                    await _outgoingFrames.Writer.WriteAsync(new GrpcSessionFrame(GrpcSessionFrameKind.Pong, frame.SessionId, null, null, null), token)
                        .ConfigureAwait(false);
                    break;
                case GrpcSessionFrameKind.Message:
                    await HandleInboundMessageAsync(frame, token).ConfigureAwait(false);
                    break;
            }
        }
    }

    private void ResolveAck(GrpcAckPayload? ack)
    {
        if (ack is null)
        {
            return;
        }

        string correlation = ack.DeduplicationCorrelation;
        if (_pendingAcks.TryRemove(correlation, out TaskCompletionSource<bool>? completion))
        {
            completion.TrySetResult(true);
        }
    }

    private async Task HandleInboundMessageAsync(GrpcSessionFrame frame, CancellationToken token)
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
            GrpcSessionFrame ackFrame = new(GrpcSessionFrameKind.Ack, frame.SessionId, null, null, new GrpcAckPayload(frame.Message.EventId, frame.Message.DeduplicationId));
            await _outgoingFrames.Writer.WriteAsync(ackFrame, token).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _outgoingFrames.Writer.TryComplete();
        try
        {
            if (_sendLoop is not null)
            {
                await _sendLoop.ConfigureAwait(false);
            }
            if (_receiveLoop is not null)
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            if (_heartbeatLoop is not null)
            {
                await _heartbeatLoop.ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (TaskCompletionSource<bool> pending in _pendingAcks.Values)
            {
                pending.TrySetCanceled();
            }

            _activeCall?.Dispose();
            _cts.Dispose();
        }
    }
}
