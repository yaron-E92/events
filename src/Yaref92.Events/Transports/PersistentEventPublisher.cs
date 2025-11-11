using System;
using System.Threading;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Sessions;
using Yaref92.Events.Transports.ConnectionManagers;

namespace Yaref92.Events.Transports;

internal class PersistentEventPublisher(SessionManager sessionManager) : IPersistentFramePublisher
{
    private Task? _disposeTask;
    private int _disposeState;

    public IOutboundConnectionManager ConnectionManager { get; } = new OutboundConnectionManager(sessionManager);

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
        try
        {
            await ConnectionManager.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"{nameof(PersistentEventPublisher)} disposal failed: {ex}")
                .ConfigureAwait(false);
        }
    }

    public Task PublishToAllAsync(Guid eventId, string eventEnvelopePayload, CancellationToken cancellationToken)
    {
        return Task.Run(() => ConnectionManager.QueueEventBroadcast(eventId, eventEnvelopePayload), cancellationToken);
    }

    /// <summary>
    /// Using the ConnectionManager, send an Ack for the given eventId
    /// using the correct outbound connection.
    /// </summary>
    /// <param name="eventId">Id of the event that the transport received successfully</param>
    /// <param name="sessionKey">Key for the session representing the remote receiver of the ack</param>
    public void AcknowledgeEventReceipt(Guid eventId, SessionKey sessionKey)
    {
        ConnectionManager.SendAck(eventId, sessionKey);
    }
}
