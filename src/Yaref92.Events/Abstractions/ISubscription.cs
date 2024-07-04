using System.Reactive.Disposables;

namespace Yaref92.Events.Abstractions;

public interface ISubscription
{
    IDisposable ObservableSubscription { get; set; }

    void AddSubscription(IDisposable subscription)
    {
        ObservableSubscription = subscription;
    }

    void Dispose()
    {
        if (ObservableSubscription != null && ObservableSubscription != Disposable.Empty)
        {
            ObservableSubscription.Dispose();
        }
    }
}
