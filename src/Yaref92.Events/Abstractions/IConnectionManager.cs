using Yaref92.Events.Transports;

namespace Yaref92.Events.Abstractions;

internal interface IConnectionManager : IAsyncDisposable
{
    SessionManager SessionManager { get; }
}
