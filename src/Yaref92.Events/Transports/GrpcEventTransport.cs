using System.Collections.Concurrent;
using System.Collections.Generic;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Serialization;

namespace Yaref92.Events.Transports;

public sealed class GrpcEventTransport : IEventTransport, IAsyncDisposable
{
    private readonly int _listenPort;
    private readonly IEventSerializer _serializer;
    private readonly ConcurrentDictionary<Guid, StreamRegistration> _activeStreams = new();
    private readonly ConcurrentBag<GrpcChannel> _channels = new();
    private Task? _disposeTask;
    private int _disposeState;
    private IHost? _host;

    private event Func<IDomainEvent, Task<bool>>? EventReceived;

    event Func<IDomainEvent, Task<bool>> IEventTransport.EventReceived
    {
        add => EventReceived += value;
        remove => EventReceived -= value;
    }

    public event IEventTransport.SessionInboundConnectionDroppedHandler? SessionInboundConnectionDropped;

    public GrpcEventTransport(int listenPort, IEventSerializer? serializer = null)
    {
        _listenPort = listenPort;
        _serializer = serializer ?? new JsonEventSerializer();
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    public Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        if (_host is not null)
        {
            return Task.CompletedTask;
        }

        _host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel(options =>
                {
                    options.ListenAnyIP(_listenPort, listenOptions =>
                    {
                        listenOptions.Protocols = HttpProtocols.Http2;
                    });
                });
                webBuilder.ConfigureServices(services =>
                {
                    services.AddGrpc();
                    services.AddSingleton(this);
                    services.AddSingleton<EventStreamService>();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGrpcService<EventStreamService>();
                    });
                });
            })
            .Build();

        return _host.StartAsync(cancellationToken);
    }

    public Task ConnectToPeerAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host cannot be null or whitespace.", nameof(host));
        }

        var channel = GrpcChannel.ForAddress($"http://{host}:{port}");
        _channels.Add(channel);

        var client = new global::EventStream.EventStreamClient(channel);
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

    private async Task DisposeAsyncCore()
    {
        if (_host is not null)
        {
            await _host.StopAsync().ConfigureAwait(false);
            _host.Dispose();
            _host = null;
        }

        foreach (var channel in _channels)
        {
            channel.Dispose();
        }
    }

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
        if (frame.Kind != FrameKind.EVENT)
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
                Kind = FrameKind.ACK,
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
            Kind = FrameKind.EVENT,
        };
    }

    private sealed class EventStreamService : global::EventStream.EventStreamBase
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
