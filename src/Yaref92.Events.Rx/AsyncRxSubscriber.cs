using Yaref92.Events.Abstractions;
using Yaref92.Events.Rx.Abstractions;

namespace Yaref92.Events.Rx;

public abstract class AsyncRxSubscriber<T> : IAsyncRxSubscriber<T> where T : class, IDomainEvent
{
    public void OnNext(T value)
    {
        // Fire-and-forget async handling
        _ = OnNextAsync(value, CancellationToken.None);
    }

    public abstract Task OnNextAsync(T value, CancellationToken cancellationToken = default);

    public virtual void OnError(Exception error) { }
    public virtual void OnCompleted() { }
}
