using System.Collections.Concurrent;
using System.Net.Sockets;

using Yaref92.Events.Abstractions;

namespace Yaref92.Events.Transports;

internal sealed class ResilientPeerSession : IPersistentPeerSession
{
    private readonly PersistentSessionClient _client;
    private readonly ConcurrentDictionary<TcpClient, Task> _receiveTasks = new();
    private readonly Func<string, string, CancellationToken, Task> _payloadHandler;

    public ResilientPeerSession(
        string host,
        int port,
        ResilientSessionOptions options,
        Func<string, string, CancellationToken, Task> payloadHandler,
        IEventAggregator? eventAggregator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(payloadHandler);

        SessionKey = $"{host}:{port}";
        _payloadHandler = payloadHandler;
        _client = new PersistentSessionClient(host, port, OnClientConnectedAsync, options, eventAggregator);
    }

    public string SessionKey { get; }

    public string SessionToken => _client.SessionToken;

    public PersistentSessionClient PersistentClient => _client;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _client.StartAsync(cancellationToken);
    }

    public Task PublishAsync(string payload, CancellationToken cancellationToken)
    {
        return _client.EnqueueEventAsync(payload, cancellationToken);
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

    private Task OnClientConnectedAsync(PersistentSessionClient session, TcpClient client, CancellationToken cancellationToken)
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

    private async Task ReceiveLoopAsync(PersistentSessionClient session, TcpClient client, CancellationToken cancellationToken)
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

                await HandleFrameAsync(session, result.Frame!, cancellationToken).ConfigureAwait(false);
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

    private async Task HandleFrameAsync(PersistentSessionClient session, SessionFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Kind)
        {
            case SessionFrameKind.Message when frame.Payload is not null:
                await _payloadHandler(SessionKey, frame.Payload, cancellationToken).ConfigureAwait(false);
                session.RecordRemoteActivity();
                if (frame.Id is long messageId)
                {
                    session.EnqueueControlMessage(SessionFrame.CreateAck(messageId));
                }
                break;
            case SessionFrameKind.Ack when frame.Id is long ackId:
                session.Acknowledge(ackId);
                session.RecordRemoteActivity();
                break;
            case SessionFrameKind.Ping:
                session.EnqueueControlMessage(SessionFrame.CreatePong());
                session.RecordRemoteActivity();
                break;
            case SessionFrameKind.Pong:
                session.RecordRemoteActivity();
                break;
            case SessionFrameKind.Auth:
                session.RecordRemoteActivity();
                break;
        }
    }
}
