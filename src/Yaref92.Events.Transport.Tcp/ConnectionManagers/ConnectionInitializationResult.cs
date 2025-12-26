using Yaref92.Events.Abstractions;

namespace Yaref92.Events.Transport.Tcp.ConnectionManagers;

internal readonly record struct ConnectionInitializationResult(
        IResilientPeerSession? Session,
        bool IsSuccess,
        CancellationTokenSource? ConnectionCancellation)
{
    public static ConnectionInitializationResult Success(
        IResilientPeerSession session,
        CancellationTokenSource cancellation) => new(session, true, cancellation);

    public static ConnectionInitializationResult Failed() => new(null, false, null);
}
