using System.Collections.Generic;
using System.Linq;
using System.Net;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transports.ConnectionManagers;

internal sealed class OutboundConnectionManager(SessionManager sessionManager) : IOutboundConnectionManager
{
    private readonly CancellationTokenSource _cts = new();

    public SessionManager SessionManager { get; } = sessionManager;

    public void QueueEventBroadcast(Guid eventId, string eventEnvelopeJson)
    {
        ArgumentNullException.ThrowIfNull(eventEnvelopeJson);

        foreach (IOutboundResilientConnection connection in EnumerateDistinctOutboundConnections())
        {
            connection.EnqueueFrame(SessionFrame.CreateEventFrame(eventId, eventEnvelopeJson));
        }
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        foreach (IOutboundResilientConnection connection in EnumerateDistinctOutboundConnections())
        {
            if (connection is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private IEnumerable<IOutboundResilientConnection> EnumerateDistinctOutboundConnections() =>
        SessionManager.AuthenticatedSessions
            .Concat(SessionManager.ValidAnonymousSessions)
            .DistinctBy(session => session.Key)
            .Select(session => session.OutboundConnection);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"{nameof(OutboundConnectionManager)} disposal failed: {ex}")
                .ConfigureAwait(false);
        }
        finally
        {
            _cts.Dispose();
        }
    }

    public Task ConnectAsync(Guid userId, string host, int port, CancellationToken cancellationToken)
    {
        SessionKey sessionKey = new(userId, host, port)
        {
            IsAnonymousKey = userId == Guid.Empty,
        };
        if (sessionKey.IsAnonymousKey)
        {
            SessionManager.HydrateAnonymousSessionId(sessionKey, new DnsEndPoint(host, port));
        }
        return ConnectAsync(sessionKey, cancellationToken);
    }

    public Task ConnectAsync(SessionKey sessionKey, CancellationToken cancellationToken)
    {
        var outboundConnection = SessionManager.GetOrGenerate(sessionKey).OutboundConnection;
        return outboundConnection.InitAsync(cancellationToken);
    }

    public async Task<bool> TryReconnectAsync(SessionKey sessionKey, CancellationToken token)
    {
        var outboundConnection = SessionManager.GetOrGenerate(sessionKey).OutboundConnection;
        return await outboundConnection.RefreshConnectionAsync(token).ConfigureAwait(false);
    }

    public void SendAck(Guid eventId, SessionKey sessionKey)
    {
        SessionManager.GetOrGenerate(sessionKey)
            .OutboundConnection
            .EnqueueFrame(SessionFrame.CreateAck(eventId));
    }

    public Task OnAckReceived(Guid eventId, SessionKey sessionKey)
    {
        SessionManager.GetOrGenerate(sessionKey).OutboundConnection.OnAckReceived(eventId);
        return Task.CompletedTask;
    }

    public void SendPong(SessionKey sessionKey)
    {
        SessionManager.GetOrGenerate(sessionKey)
            .OutboundConnection
            .EnqueueFrame(SessionFrame.CreatePong());
    }
}
