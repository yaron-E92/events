namespace Yaref92.Events.Abstractions;

public interface ISubscription : IDisposable
{
    IDisposable ObservableSubscription { get; set; }

    void AddSubscription(IDisposable subscription) => ObservableSubscription = subscription;
}
