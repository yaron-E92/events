using System.Reactive.Disposables;

using Yaref92.Events.Abstractions;

namespace Yaref92.Events;

public class Subscription : ISubscription
{
    public IDisposable ObservableSubscription { get; private set; } = Disposable.Empty;

    public void AddSubscription(IDisposable subscription)
    {
        ObservableSubscription = subscription;
    }
}
