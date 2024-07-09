using System.Reactive.Disposables;

namespace Yaref92.Events.Abstractions;

public interface ISubscription
{
    IDisposable ObservableSubscription { get; }

    void AddSubscription(IDisposable subscription);

    void Dispose()
    {
        if (ObservableSubscription != null && ObservableSubscription != Disposable.Empty)
        {
            ObservableSubscription.Dispose();
        }
    }
}
