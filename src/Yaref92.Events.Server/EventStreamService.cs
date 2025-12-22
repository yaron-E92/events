using Grpc.Core;

using Yaref92.Events.Transports;

namespace Yaref92.Events.Server;

internal sealed class EventStreamService : global::EventStream.EventStreamBase
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
