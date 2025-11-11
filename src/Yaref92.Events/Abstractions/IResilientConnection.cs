using System.Net.Sockets;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.Abstractions;

internal interface IResilientConnection
{
    SessionKey SessionKey { get; }
}
