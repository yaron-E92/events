using Yaref92.Events.Abstractions;

namespace Yaref92.Events.Rx.Abstractions;

/// <summary>
/// Marker interface for Rx-based event subscribers.
/// </summary>
/// <remarks>
/// This interface serves as a marker to identify Rx subscribers and distinguish them
/// from traditional event subscribers. It provides no functionality on its own.
/// </remarks>
public interface IRxSubscriber
{
}

/// <summary>
/// Strongly typed Rx subscriber to a domain event that combines traditional event subscription
/// with Reactive Extensions observer capabilities.
/// </summary>
/// <typeparam name="T">The domain event type. Must be a class implementing <see cref="IDomainEvent"/>.</typeparam>
/// <remarks>
/// <para>
/// The <see cref="IRxSubscriber{T}"/> interface combines the functionality of:
/// <list type="bullet">
/// <item><description><see cref="IEventHandler{T}"/> - Traditional event subscription</description></item>
/// <item><description><see cref="IObserver{T}"/> - Rx observer pattern</description></item>
/// <item><description><see cref="IRxSubscriber"/> - Rx subscriber marker</description></item>
/// </list>
/// </para>
/// <para>
/// This interface allows subscribers to be used with both traditional event publishing
/// and Rx-based event streaming. When used with <see cref="RxEventAggregator"/>, Rx subscribers
/// are automatically managed and their subscriptions are properly disposed.
/// </para>
/// <para>
/// Key benefits:
/// <list type="bullet">
/// <item><description>Compatible with both traditional and Rx event systems</description></item>
/// <item><description>Automatic subscription lifecycle management</description></item>
/// <item><description>Access to Rx observer methods (OnNext, OnError, OnCompleted)</description></item>
/// <item><description>Type-safe event handling</description></item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <para>Basic Rx subscriber implementation:</para>
/// <code>
/// public class AuditLogger : IRxSubscriber&lt;UserRegisteredEvent&gt;
/// {
///     public void OnNext(UserRegisteredEvent @event)
///     {
///         Console.WriteLine($"Audit: User {@event.UserId} registered at {@event.DateTimeOccurredUtc}");
///     }
///     
///     public void OnError(Exception error)
///     {
///         Console.WriteLine($"Audit error: {error.Event}");
///     }
///     
///     public void OnCompleted()
///     {
///         Console.WriteLine("Audit logging completed");
///     }
/// }
/// </code>
/// <para>Usage with RxEventAggregator:</para>
/// <code>
/// using var aggregator = new RxEventAggregator();
/// aggregator.RegisterEventType&lt;UserRegisteredEvent&gt;();
/// aggregator.SubscribeToEventType(new AuditLogger());
/// 
/// aggregator.PublishEvent(new UserRegisteredEvent("user-123"));
/// </code>
/// </example>
public interface IRxSubscriber<T> : IRxSubscriber, IEventHandler<T>, IObserver<T> where T : class, IDomainEvent
{
}
public interface IAsyncRxSubscriber<T> : IRxSubscriber, IAsyncEventHandler<T>, IObserver<T> where T : class, IDomainEvent
{
}
