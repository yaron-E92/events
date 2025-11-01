using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Threading;

using Yaref92.Events.Abstractions;
using Yaref92.Events.Rx.Abstractions;

namespace Yaref92.Events.Rx;

/// <summary>
/// An event aggregator that extends the base <see cref="EventAggregator"/> with Reactive Extensions (Rx) support.
/// This class provides both traditional synchronous event publishing and Rx-based event streaming capabilities.
/// </summary>
/// <remarks>
/// <para>
/// The RxEventAggregator combines the functionality of the base EventAggregator with Rx capabilities,
/// allowing you to:
/// <list type="bullet">
/// <item><description>Use traditional synchronous event publishing and subscription</description></item>
/// <item><description>Subscribe to events using Rx observables and LINQ operators</description></item>
/// <item><description>Compose complex event processing pipelines</description></item>
/// <item><description>Handle events asynchronously with Rx operators</description></item>
/// </list>
/// </para>
/// <para>
/// This class is thread-safe and implements <see cref="IDisposable"/> to properly clean up Rx subscriptions.
/// Always dispose of this instance when it's no longer needed to prevent memory leaks.
/// </para>
/// <para>
/// Key features:
/// <list type="bullet">
/// <item><description>Inherits all thread-safety and memory management features from <see cref="EventAggregator"/></description></item>
/// <item><description>Provides an <see cref="IObservable{T}"/> event stream for Rx-based subscriptions</description></item>
/// <item><description>Automatically manages Rx subscription lifecycle</description></item>
/// <item><description>Supports both traditional and Rx-based subscribers simultaneously</description></item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <para>Basic usage with Rx:</para>
/// <code>
/// using var aggregator = new RxEventAggregator();
/// aggregator.RegisterEventType&lt;UserRegisteredEvent&gt;();
/// 
/// // Subscribe using Rx
/// var subscription = aggregator.EventStream
///     .OfType&lt;UserRegisteredEvent&gt;()
///     .Where(e => e.UserId.StartsWith("admin"))
///     .Subscribe(e => Console.WriteLine($"Admin user registered: {e.UserId}"));
/// 
/// // Publish events
/// aggregator.PublishEvent(new UserRegisteredEvent("admin-123"));
/// aggregator.PublishEvent(new UserRegisteredEvent("user-456"));
/// 
/// // Clean up
/// subscription.Dispose();
/// </code>
/// <para>Using Rx subscribers:</para>
/// <code>
/// public class AuditLogger : IRxSubscriber&lt;UserRegisteredEvent&gt;
/// {
///     public void OnNext(UserRegisteredEvent @event) => Console.WriteLine($"Audit: {@event.UserId}");
/// }
/// 
/// using var aggregator = new RxEventAggregator();
/// aggregator.RegisterEventType&lt;UserRegisteredEvent&gt;();
/// aggregator.SubscribeToEventType(new AuditLogger());
/// 
/// aggregator.PublishEvent(new UserRegisteredEvent("user-123"));
/// </code>
/// </example>
public sealed class RxEventAggregator : EventAggregator, IDisposable
{
    private readonly ISubject<IDomainEvent> _subject;
    private readonly IObservable<IDomainEvent> _eventStream;
    private readonly ConcurrentDictionary<(Type, IRxSubscriber), IDisposable> _rxSubscriptions = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RxEventAggregator"/> class.
    /// </summary>
    /// <remarks>
    /// This constructor creates a new RxEventAggregator with a synchronized Rx subject
    /// to ensure thread-safe event publishing. The event stream is immediately available
    /// for subscription.
    /// </remarks>
    public RxEventAggregator() : base()
    {
        _subject = Subject.Synchronize(new Subject<IDomainEvent>());
        _eventStream = _subject.AsObservable();
    }

    /// <summary>
    /// Gets the observable event stream that can be used for Rx-based subscriptions.
    /// </summary>
    /// <value>
    /// An observable stream of all published domain events.
    /// </value>
    /// <remarks>
    /// <para>
    /// This property provides access to the Rx event stream, allowing you to use all
    /// standard Rx operators and LINQ methods for event processing.
    /// </para>
    /// <para>
    /// The stream contains all published events regardless of their type. Use
    /// <c>OfType&lt;TEvent&gt;()</c> to filter for specific event types.
    /// </para>
    /// <para>
    /// This stream is thread-safe and can be subscribed to from multiple threads.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var subscription = aggregator.EventStream
    ///     .OfType&lt;UserRegisteredEvent&gt;()
    ///     .Where(e => e.UserId.StartsWith("admin"))
    ///     .Select(e => new AdminUserEvent(e.UserId))
    ///     .Subscribe(e => ProcessAdminUser(e));
    /// </code>
    /// </example>
    public IObservable<IDomainEvent> EventStream => _eventStream;

    /// <summary>
    /// Subscribes a subscriber to an event type, with special handling for Rx subscribers.
    /// </summary>
    /// <typeparam name="T">The event type to subscribe to. Must be a registered event type.</typeparam>
    /// <param name="subscriber">The subscriber instance. Can be either a traditional subscriber or an Rx subscriber.</param>
    /// <remarks>
    /// <para>
    /// This method extends the base subscription behavior to support Rx subscribers.
    /// If the subscriber implements <see cref="IRxSubscriber{T}"/>, it will be subscribed
    /// to the Rx event stream. Otherwise, it will be handled by the base EventAggregator.
    /// </para>
    /// <para>
    /// Rx subscribers are automatically managed and will be disposed when the aggregator
    /// is disposed or when they are unsubscribed.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Traditional subscriber
    /// aggregator.SubscribeToEventType(new WelcomeEmailSender());
    /// 
    /// // Rx subscriber
    /// aggregator.SubscribeToEventType(new AuditLogger());
    /// </code>
    /// </example>
    public override void SubscribeToEventType<T>(IEventHandler<T> subscriber)
    {
        ValidateEventRegistration<T>();
        if (subscriber is IRxSubscriber<T> rxSubscriber)
        {
            SubscribeRxSubscriber<T>(rxSubscriber);
        }
        else
        {
            SubscribeSubscriber<T>(subscriber);
        }
    }

    /// <summary>
    /// Subscribes an asynchronous subscriber to an event type, with special handling for Rx async subscribers.
    /// </summary>
    /// <typeparam name="T">The event type to subscribe to. Must be a registered event type.</typeparam>
    /// <param name="subscriber">The async subscriber instance. Cannot be null.</param>
    /// <remarks>
    /// If the subscriber implements <see cref="IAsyncRxSubscriber{T}"/>, it will be subscribed to the Rx event stream and managed for disposal.
    /// Otherwise, it will be handled by the base EventAggregator logic.
    /// </remarks>
    public override void SubscribeToEventType<T>(IAsyncEventHandler<T> subscriber)
    {
        ValidateEventRegistration<T>();
        if (subscriber is IAsyncRxSubscriber<T> rxSubscriber)
        {
            SubscribeRxSubscriber<T>(rxSubscriber);
        }
        else
        {
            SubscribeSubscriber<T>(subscriber);
        }
    }

    /// <summary>
    /// Subscribes an Rx subscriber (sync or async) to the event stream for a specific event type.
    /// </summary>
    /// <typeparam name="T">The event type to subscribe to.</typeparam>
    /// <param name="subscriber">The Rx subscriber to subscribe.</param>
    /// <remarks>
    /// The subscription is tracked internally and will be automatically disposed when the aggregator is disposed or when the subscriber is unsubscribed.
    /// </remarks>
    private void SubscribeRxSubscriber<T>(IRxSubscriber subscriber) where T : class, IDomainEvent
    {
        var subscription = _eventStream.OfType<T>().Subscribe((IObserver<T>) subscriber);
        _rxSubscriptions.TryAdd((typeof(T), subscriber), subscription);
    }

    /// <summary>
    /// Unsubscribes a subscriber from an event type, with special handling for Rx subscribers.
    /// </summary>
    /// <typeparam name="T">The event type to unsubscribe from. Must be a registered event type.</typeparam>
    /// <param name="subscriber">The subscriber instance to unsubscribe.</param>
    /// <remarks>
    /// <para>
    /// This method extends the base unsubscription behavior to support Rx subscribers.
    /// If the subscriber is an Rx subscriber, its Rx subscription will be properly
    /// disposed. Otherwise, it will be handled by the base EventAggregator.
    /// </para>
    /// </remarks>
    public override void UnsubscribeFromEventType<T>(IEventHandler<T> subscriber)
    {
        if (subscriber is IRxSubscriber<T> rxSubscriber)
        {
            UnsubscribeRxSubscriber<T>(rxSubscriber);
        }
        else
        {
            UnsubscribeSubscriber<T>(subscriber);
        }
    }

    /// <summary>
    /// Unsubscribes an asynchronous subscriber from an event type, with special handling for Rx async subscribers.
    /// </summary>
    /// <typeparam name="T">The event type to unsubscribe from. Must be a registered event type.</typeparam>
    /// <param name="subscriber">The async subscriber instance to unsubscribe. Cannot be null.</param>
    /// <remarks>
    /// If the subscriber implements <see cref="IAsyncRxSubscriber{T}"/>, its Rx subscription will be disposed.
    /// Otherwise, it will be handled by the base EventAggregator logic.
    /// </remarks>
    public override void UnsubscribeFromEventType<T>(IAsyncEventHandler<T> subscriber)
    {
        if (subscriber is IAsyncRxSubscriber<T> rxSubscriber)
        {
            UnsubscribeRxSubscriber<T>(rxSubscriber);
        }
        else
        {
            UnsubscribeSubscriber<T>(subscriber);
        }
    }

    /// <summary>
    /// Unsubscribes an Rx subscriber (sync or async) and disposes its Rx subscription.
    /// </summary>
    /// <typeparam name="T">The event type to unsubscribe from.</typeparam>
    /// <param name="subscriber">The Rx subscriber to unsubscribe.</param>
    /// <remarks>
    /// This method removes the Rx subscription from the tracking dictionary and disposes it to prevent memory leaks.
    /// </remarks>
    private void UnsubscribeRxSubscriber<T>(IRxSubscriber subscriber) where T : class, IDomainEvent
    {
        ValidateSubscriber((IEventHandler) subscriber, typeof(IEventHandler<T>));
        ValidateEventRegistration<T>();
        if (_rxSubscriptions.TryRemove((typeof(T), subscriber), out var disposable))
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Publishes an event to both the Rx event stream and traditional subscribers.
    /// </summary>
    /// <typeparam name="T">The event type. Must be a registered event type.</typeparam>
    /// <param name="domainEvent">The event instance to publish. Cannot be null.</param>
    /// <remarks>
    /// <para>
    /// This method extends the base publishing behavior to also publish events to
    /// the Rx event stream. Events are first validated, then published to the Rx
    /// stream, and finally delivered to traditional subscribers.
    /// </para>
    /// <para>
    /// This ensures that both Rx subscribers and traditional subscribers receive
    /// the same events in a consistent manner.
    /// </para>
    /// </remarks>
    public override void PublishEvent<T>(T domainEvent)
    {
        ValidateEvent(domainEvent);
        _subject.OnNext(domainEvent);
        base.PublishEvent(domainEvent);
    }

    /// <summary>
    /// Publishes an event to both the Rx event stream and traditional subscribers asynchronously.
    /// </summary>
    /// <typeparam name="T">The event type. Must be a registered event type.</typeparam>
    /// <param name="domainEvent">The event instance to publish. Cannot be null.</param>
    /// <param name="cancellationToken">The cancellation token to use for the asynchronous operation.</param>
    /// <remarks>
    /// <para>
    /// This method extends the base publishing behavior to also publish events to
    /// the Rx event stream. Events are first validated, then published to the Rx
    /// stream, and finally delivered to traditional subscribers.
    /// </para>
    /// <para>
    /// This ensures that both Rx subscribers and traditional subscribers receive
    /// the same events in a consistent manner.
    /// </para>
    /// </remarks>
    public override async Task PublishEventAsync<T>(T domainEvent, CancellationToken cancellationToken = default)
    {
        ValidateEvent(domainEvent);
        _subject.OnNext(domainEvent);
        await base.PublishEventAsync(domainEvent, cancellationToken);
    }

    /// <summary>
    /// Disposes all tracked Rx subscriptions and clears the subscription dictionary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method should be called when the RxEventAggregator is no longer needed
    /// to prevent memory leaks. It disposes all active Rx subscriptions and clears
    /// the internal tracking dictionary.
    /// </para>
    /// <para>
    /// After disposal, the aggregator should not be used for publishing or subscription
    /// operations.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using var aggregator = new RxEventAggregator();
    /// // ... use the aggregator ...
    /// // Disposal happens automatically due to using statement
    /// </code>
    /// </example>
    public void Dispose()
    {
        foreach (var disposable in _rxSubscriptions.Values)
        {
            disposable.Dispose();
        }
        _rxSubscriptions.Clear();
    }
} 
