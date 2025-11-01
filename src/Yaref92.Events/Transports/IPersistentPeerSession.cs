namespace Yaref92.Events.Transports;

internal interface IPersistentPeerSession : IAsyncDisposable
{
    string SessionKey { get; }

    string SessionToken { get; }

    PersistentSessionClient PersistentClient { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task PublishAsync(string payload, CancellationToken cancellationToken);
}
