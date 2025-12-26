using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transport.Tcp.Abstractions;

public interface IResilientConnection
{
    SessionKey SessionKey { get; }

    internal delegate Task SessionConnectionEstablishedHandler(IResilientConnection client, CancellationToken cancellationToken);
}
