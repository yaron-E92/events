using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Transports;

internal sealed class ResilientPeerSession : IResilientPeerSession
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> _publishCache = new();

    private readonly ResilientSessionClient _client;
    private readonly IEventAggregator? _localAggregator;
    private readonly IEventSerializer _eventSerializer;
    private readonly ConcurrentDictionary<TcpClient, Task> _receiveTasks = new();

    public ResilientPeerSession(SessionKey sessionKey,
        ResilientSessionOptions options,
        IEventAggregator? eventAggregator, IEventSerializer eventSerializer)
    {
        SessionKey = sessionKey;
        Options = options;
        _client = new ResilientSessionClient(sessionKey, options, eventAggregator);
        _localAggregator = eventAggregator;
        _eventSerializer = eventSerializer;
        _ = StartAsync(CancellationToken.None);
    }

    public string SessionToken => _client.SessionToken;

    public ResilientSessionClient PersistentClient => _client;

    public SessionKey SessionKey { get; }
    public ResilientSessionOptions Options { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return PersistentClient.StartAsync(cancellationToken);
    }

    public Task PublishAsync(string payload, CancellationToken cancellationToken)
    {
        return PersistentClient.EnqueueEventAsync(payload, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var task in _receiveTasks.Values)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                await Console.Error.WriteLineAsync($"{nameof(ResilientPeerSession)} receive loop terminated: {ex}").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _receiveTasks.Clear();
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private Task OnClientConnectedAsync(ResilientSessionClient session, TcpClient client, CancellationToken cancellationToken)
    {
        var receiveTask = Task.Run(() => ReceiveLoopAsync(session, client, cancellationToken), cancellationToken);
        _receiveTasks[client] = receiveTask;
        receiveTask.ContinueWith(_ =>
        {
            _receiveTasks.TryRemove(client, out _);
            client.Dispose();
        }, TaskContinuationOptions.ExecuteSynchronously);

        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(ResilientSessionClient session, TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var lengthBuffer = new byte[4];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await SessionFrameIO.ReadFrameAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    break;
                }

                await HandleIncomingFrameAsync(session, result.Frame!, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            await Console.Error.WriteLineAsync($"Persistent receive loop terminated: {ex}").ConfigureAwait(false);
        }
    }

    private async Task HandleIncomingFrameAsync(ResilientSessionClient sessionClient, SessionFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Kind)
        {
            case SessionFrameKind.Event when frame.Payload is not null:
                await PublishReflectedEventLocally(frame.Payload, cancellationToken).ConfigureAwait(false);

                sessionClient.RecordRemoteActivity();
                if (frame.Id is Guid messageId)
                {
                    sessionClient.EnqueueControlMessage(SessionFrame.CreateAck(messageId));
                }
                break;
            case SessionFrameKind.Ack when frame.Id is Guid ackId:
                sessionClient.Acknowledge(ackId);
                sessionClient.RecordRemoteActivity();
                break;
            case SessionFrameKind.Ping:
                sessionClient.EnqueueControlMessage(SessionFrame.CreatePong());
                sessionClient.RecordRemoteActivity();
                break;
            case SessionFrameKind.Pong:
                sessionClient.RecordRemoteActivity();
                break;
            case SessionFrameKind.Auth:
                sessionClient.RecordRemoteActivity();
                break;
        }
    }

    private async Task PublishReflectedEventLocally(string payload, CancellationToken cancellationToken)
    {
        (Type? eventType, IDomainEvent? domainEvent) = _eventSerializer.Deserialize(payload);
        // Build the EventReceived<TEvent> type dynamically
        Type eventReceivedType = typeof(EventReceived<>).MakeGenericType(eventType);
        // Find the (DateTime, TEvent) constructor explicitly
        ConstructorInfo ctor = eventReceivedType.GetConstructor([typeof(DateTime), eventType])!
            ?? throw new InvalidOperationException($"Missing expected constructor on {eventReceivedType}");

        // Instantiate EventReceived<TEvent>(DateTime.UtcNow, (TEvent)domainEvent)
        object eventReceivedInstance = ctor.Invoke([DateTime.UtcNow, domainEvent]);

        // Get and specialize PublishEventAsync<TEvent>
        MethodInfo publishMethod = _publishCache.GetOrAdd(eventType, static t =>
            typeof(IEventAggregator)
                .GetMethod(nameof(IEventAggregator.PublishEventAsync))!
                .MakeGenericMethod(t));

        // Call it asynchronously
        var task = (Task) publishMethod.Invoke(_localAggregator, [eventReceivedInstance, cancellationToken])!;
        await task.ConfigureAwait(false);
    }
}
