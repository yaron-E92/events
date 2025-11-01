using System.Text.Json;

namespace Yaref92.Events.Transports;

public sealed class PersistentSessionListener : IAsyncDisposable
{
    private readonly ResilientTcpServer _server;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    public PersistentSessionListener(int port, ResilientSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _server = new ResilientTcpServer(port, options, HandleInboundPayloadAsync);
        _server.SetSessionJoinedHandler(OnSessionJoinedInternalAsync);
        _server.SetSessionLeftHandler(OnSessionLeftInternalAsync);
    }

    public event Func<string, EventEnvelope, string, CancellationToken, Task>? EnvelopeReceived;

    public event Func<string, CancellationToken, Task>? SessionJoined;

    public event Func<string, CancellationToken, Task>? SessionLeft;

    internal Func<string, string, CancellationToken, Task> PayloadHandler => HandleInboundPayloadAsync;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return _server.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return _server.StopAsync(cancellationToken);
    }

    public void RegisterPersistentSession(IPersistentPeerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _server.RegisterPersistentClient(session.SessionToken, session.PersistentClient);
    }

    public void Broadcast(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        _server.QueueBroadcast(payload);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    private async Task HandleInboundPayloadAsync(string sessionKey, string payload, CancellationToken cancellationToken)
    {
        EventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EventEnvelope>(payload, _serializerOptions);
        }
        catch (JsonException ex)
        {
            await Console.Error.WriteLineAsync($"Failed to deserialize event envelope: {ex}").ConfigureAwait(false);
            return;
        }

        if (envelope is null)
        {
            return;
        }

        var handler = EnvelopeReceived;
        if (handler is not null)
        {
            await handler.Invoke(sessionKey, envelope, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task OnSessionJoinedInternalAsync(string sessionKey, CancellationToken cancellationToken)
    {
        var handler = SessionJoined;
        return handler is null
            ? Task.CompletedTask
            : handler.Invoke(sessionKey, cancellationToken);
    }

    private Task OnSessionLeftInternalAsync(string sessionKey, CancellationToken cancellationToken)
    {
        var handler = SessionLeft;
        return handler is null
            ? Task.CompletedTask
            : handler.Invoke(sessionKey, cancellationToken);
    }
}
