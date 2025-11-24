using Yaref92.Events.Sessions;

namespace Yaref92.Events.Abstractions;

internal interface IResilientConnection
{
    SessionKey SessionKey { get; }

    internal delegate Task SessionConnectionEstablishedHandler(IResilientConnection client, CancellationToken cancellationToken);
}
