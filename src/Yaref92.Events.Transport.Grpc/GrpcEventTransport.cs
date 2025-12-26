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
    private readonly int _listenPort;
    private readonly IEventSerializer _serializer;
    private readonly ConcurrentDictionary<Guid, StreamRegistration> _activeStreams = new();
    private readonly ConcurrentBag<GrpcChannel> _channels = new();
    private Task? _disposeTask;
    private int _disposeState;

    internal ISessionManager SessionManager { get; }

    private event Func<IDomainEvent, Task<bool>>? EventReceived;

    event Func<IDomainEvent, Task<bool>> IEventTransport.EventReceived
    {
        add => EventReceived += value;
        remove => EventReceived -= value;
    }

    public event IEventTransport.SessionInboundConnectionDroppedHandler? SessionInboundConnectionDropped;

    public GrpcEventTransport(int listenPort, ISessionManager sessionManager, IEventSerializer? serializer = null)
    {
        _listenPort = listenPort;
        SessionManager = sessionManager;
        _serializer = serializer ?? new JsonEventSerializer();
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
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

    public Task ConnectToPeerAsync(Guid userId, string host, int port, CancellationToken cancellationToken = default)
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

        var channel = GrpcChannel.ForAddress($"http://{host}:{port}");
        _channels.Add(channel);

        var client = new EventStream.EventStreamClient(channel);
        var call = client.Connect(cancellationToken: cancellationToken);
        var registration = RegisterStream(call.RequestStream);
        _ = ProcessIncomingStreamAsync(call.ResponseStream, registration, cancellationToken)
            .ContinueWith(_ => UnregisterStream(registration), TaskScheduler.Default);
        return Task.CompletedTask;
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
    private StreamRegistration RegisterStream(IAsyncStreamWriter<TransportFrame> writer)
    {
        var registration = new StreamRegistration(Guid.NewGuid(), writer);
        _activeStreams.TryAdd(registration.Id, registration);
        return registration;
    }

    private void UnregisterStream(StreamRegistration registration)
    {
        _activeStreams.TryRemove(registration.Id, out _);
        registration.Dispose();
    }

    private async Task ProcessIncomingStreamAsync(
        IAsyncStreamReader<TransportFrame> reader,
        StreamRegistration registration,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                await HandleIncomingFrameAsync(reader.Current, registration).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HandleIncomingFrameAsync(TransportFrame frame, StreamRegistration registration)
    {
        if (frame.Kind != FrameKind.Event)
        {
            return;
        }

        var (_, domainEvent) = _serializer.Deserialize(frame.EventJson);
        if (domainEvent is null)
        {
            return;
        }

        var handler = EventReceived;
        if (handler is null)
        {
            return;
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
            var registration = _transport.RegisterStream(responseStream);
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

    private sealed class StreamRegistration : IDisposable
    {
        public StreamRegistration(Guid id, IAsyncStreamWriter<TransportFrame> writer)
        {
            Id = id;
            Writer = writer;
            WriteLock = new SemaphoreSlim(1, 1);
        }

        public Guid Id { get; }

        public IAsyncStreamWriter<TransportFrame> Writer { get; }

        public SemaphoreSlim WriteLock { get; }

        public void Dispose()
        {
            WriteLock.Dispose();
        }
    }
}
