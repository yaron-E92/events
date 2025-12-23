using Yaref92.Events.Transports;

namespace Yaref92.Events.Transport.Tcp.Abstractions;

internal interface IConnectionManager : IAsyncDisposable
{
    TcpSessionManager SessionManager { get; }
}
