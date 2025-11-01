using Yaref92.Events.Transports;

namespace Yaref92.Events.Abstractions;

public interface IResilientPeerSession : IAsyncDisposable
{
    SessionKey SessionKey { get; }

    string SessionToken { get; }

    ResilientSessionClient PersistentClient { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task PublishAsync(string payload, CancellationToken cancellationToken);
}
