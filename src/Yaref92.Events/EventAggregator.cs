using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Yaref92.Events.Abstractions;
using System.Collections.Immutable;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Yaref92.Events;

/// <summary>
/// A thread-safe event aggregator that manages event types, subscriptions, and event publishing.
/// This class provides a centralized mechanism for decoupled communication between components
/// through domain events.
/// </summary>
/// <remarks>
/// <para>
/// The EventAggregator is designed to be thread-safe and memory-leak resistant. It uses
/// concurrent collections internally to ensure safe access from multiple threads.
/// </para>
/// <para>
/// Key features:
/// <list type="bullet">
/// <item><description>Type-safe event registration and publishing</description></item>
/// <item><description>Thread-safe subscription management</description></item>
/// <item><description>Automatic memory leak prevention through proper unsubscription</description></item>
/// <item><description>Comprehensive logging support</description></item>
/// <item><description>Null argument validation with descriptive exceptions</description></item>
/// </list>
/// </para>
/// <para>
/// Usage pattern:
/// <code>
/// var aggregator = new EventAggregator(logger);
/// aggregator.RegisterEventType&lt;UserRegisteredEvent&gt;();
/// aggregator.SubscribeToEventType(new WelcomeEmailSender());
/// aggregator.PublishEvent(new UserRegisteredEvent("user-123"));
/// </code>
/// </para>
/// </remarks>
/// <example>
/// <para>Basic usage example:</para>
/// <code>
/// public class UserRegisteredEvent : IDomainEvent
/// {
///     public string UserId { get; }
///     public DateTime DateTimeOccurredUtc { get; } = DateTime.UtcNow;
///     public UserRegisteredEvent(string userId) => UserId = userId;
/// }
/// 
/// public class WelcomeEmailSender : IEventSubscriber&lt;UserRegisteredEvent&gt;
/// {
///     public void OnNext(UserRegisteredEvent @event) => Console.WriteLine($"Welcome {@event.UserId}!");
/// }
/// 
/// var aggregator = new EventAggregator();
/// aggregator.RegisterEventType&lt;UserRegisteredEvent&gt;();
/// aggregator.SubscribeToEventType(new WelcomeEmailSender());
/// aggregator.PublishEvent(new UserRegisteredEvent("user-123"));
/// </code>
/// </example>
public class EventAggregator : IEventAggregator
{
    private readonly ConcurrentDictionary<Type, byte> _eventTypes = new();
    
    /// <summary>
    /// Gets the set of registered event types.
    /// </summary>
    /// <value>
    /// An immutable set containing all registered event types.
    /// </value>
    /// <remarks>
    /// This property returns an immutable snapshot of the registered event types.
    /// Changes to the underlying collection will be reflected in subsequent calls.
    /// </remarks>
    public ISet<Type> EventTypes => _eventTypes.Keys.ToImmutableHashSet();

    private readonly ILogger<EventAggregator>? _logger;
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<IEventSubscriber, byte>> _subscribersByType = new();
    
    /// <summary>
    /// Gets the collection of all current subscribers.
    /// </summary>
    /// <value>
    /// An immutable collection containing all current subscribers across all event types.
    /// </value>
    /// <remarks>
    /// This property returns an immutable snapshot of all subscribers.
    /// Changes to the underlying collections will be reflected in subsequent calls.
    /// </remarks>
    public IReadOnlyCollection<IEventSubscriber> Subscribers => _subscribersByType.Values.SelectMany(dict => dict.Keys).ToImmutableHashSet();

    /// <summary>
    /// Initializes a new instance of the <see cref="EventAggregator"/> class without logging.
    /// </summary>
    /// <remarks>
    /// Use this constructor when logging is not required. All operations will still be performed
    /// but no log messages will be generated.
    /// </remarks>
    public EventAggregator() : this(null) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="EventAggregator"/> class with optional logging.
    /// </summary>
    /// <param name="logger">
    /// The logger instance to use for logging operations. Can be null to disable logging.
    /// </param>
    /// <remarks>
    /// When a logger is provided, the aggregator will log:
    /// <list type="bullet">
    /// <item><description>Warning when attempting to register duplicate event types</description></item>
    /// <item><description>Warning when attempting to subscribe duplicate subscribers</description></item>
    /// <item><description>Error when attempting to publish null events</description></item>
    /// <item><description>Error when attempting to unsubscribe null subscribers</description></item>
    /// </list>
    /// </remarks>
    public EventAggregator(ILogger<EventAggregator>? logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers a new event type for publishing and subscription.
    /// </summary>
    /// <typeparam name="T">The event type to register. Must be a class implementing <see cref="IDomainEvent"/>.</typeparam>
    /// <returns>
    /// <c>true</c> if the event type was successfully registered; <c>false</c> if it was already registered.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Event types must be registered before any subscribers can subscribe to them or before
    /// events of that type can be published. Registration is idempotent - calling this method
    /// multiple times with the same type will only register it once.
    /// </para>
    /// <para>
    /// This method is thread-safe and can be called concurrently from multiple threads.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var aggregator = new EventAggregator();
    /// bool wasRegistered = aggregator.RegisterEventType&lt;UserRegisteredEvent&gt;();
    /// Console.WriteLine($"Event type registered: {wasRegistered}");
    /// </code>
    /// </example>
    public bool RegisterEventType<T>() where T : class, IDomainEvent
    {
        var added = _eventTypes.TryAdd(typeof(T), 0);
        if (!added)
        {
            _logger?.LogWarning("Event type {EventType} is already registered.", typeof(T).FullName);
        }
        return added;
    }

    /// <summary>
    /// Publishes an event synchronously to all registered subscribers of the specified event type.
    /// </summary>
    /// <typeparam name="T">The event type. Must be a registered event type.</typeparam>
    /// <param name="domainEvent">The event instance to publish. Cannot be null.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="domainEvent"/> is null.
    /// </exception>
    /// <exception cref="MissingEventTypeException">
    /// Thrown when the event type <typeparamref name="T"/> has not been registered.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method publishes the event to all subscribers that have subscribed to the specified
    /// event type. The event is delivered synchronously to each subscriber in the order they
    /// were registered.
    /// </para>
    /// <para>
    /// If any subscriber throws an exception during event processing, the exception will not
    /// be caught by this method. It is the responsibility of the subscriber to handle its own
    /// exceptions appropriately.
    /// </para>
    /// <para>
    /// This method is thread-safe and can be called concurrently from multiple threads.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var aggregator = new EventAggregator();
    /// aggregator.RegisterEventType&lt;UserRegisteredEvent&gt;();
    /// aggregator.SubscribeToEventType(new WelcomeEmailSender());
    /// 
    /// // Publish the event
    /// aggregator.PublishEvent(new UserRegisteredEvent("user-123"));
    /// </code>
    /// </example>
    public virtual void PublishEvent<T>(T domainEvent) where T : class, IDomainEvent
    {
        ValidateEvent(domainEvent);

        if (_subscribersByType.TryGetValue(typeof(T), out var subscribers))
        {
            foreach (var subscriber in subscribers.Keys.OfType<IEventSubscriber<T>>())
            {
                subscriber.OnNext(domainEvent);
            }
        }
    }

    /// <summary>
    /// Publishes an event asynchronously to all registered subscribers of the specified event type.
    /// </summary>
    /// <typeparam name="T">The event type. Must be a registered event type.</typeparam>
    /// <param name="domainEvent">The event instance to publish. Cannot be null.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="domainEvent"/> is null.
    /// </exception>
    /// <exception cref="MissingEventTypeException">
    /// Thrown when the event type <typeparamref name="T"/> has not been registered.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method publishes the event to all subscribers that have subscribed to the specified
    /// event type. The event is delivered asynchronously to each subscriber.
    /// </para>
    /// <para>
    /// If any subscriber throws an exception during event processing, the exception will not
    /// be caught by this method. It is the responsibility of the subscriber to handle its own
    /// exceptions appropriately.
    /// </para>
    /// <para>
    /// This method is thread-safe and can be called concurrently from multiple threads.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var aggregator = new EventAggregator();
    /// aggregator.RegisterEventType&lt;UserRegisteredEvent&gt;();
    /// aggregator.SubscribeToEventType(new WelcomeEmailSender());
    /// 
    /// // Publish the event
    /// await aggregator.PublishEventAsync(new UserRegisteredEvent("user-123"));
    /// </code>
    /// </example>
    public virtual async Task PublishEventAsync<T>(T domainEvent) where T : class, IDomainEvent
    {
        ValidateEvent(domainEvent);

        if (_subscribersByType.TryGetValue(typeof(T), out var subscribers))
        {
            var tasks = new List<Task>();
            foreach (var subscriber in subscribers.Keys)
            {
                if (subscriber is IAsyncEventSubscriber<T> asyncSubscriber)
                {
                    tasks.Add(asyncSubscriber.OnNextAsync(domainEvent));
                }
                else if (subscriber is IEventSubscriber<T> syncSubscriber)
                {
                    syncSubscriber.OnNext(domainEvent);
                }
            }
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
            }
        }
    }

    /// <summary>
    /// Validates that an event is not null and that its type has been registered.
    /// </summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="domainEvent">The event to validate.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="domainEvent"/> is null.
    /// </exception>
    /// <exception cref="MissingEventTypeException">
    /// Thrown when the event type <typeparamref name="T"/> has not been registered.
    /// </exception>
    /// <remarks>
    /// This method is called internally by <see cref="PublishEvent{T}(T)"/> to ensure
    /// the event is valid before publishing. It can be overridden in derived classes
    /// to add additional validation logic.
    /// </remarks>
    protected void ValidateEvent<T>(T domainEvent) where T : class, IDomainEvent
    {
        ValidateEventRegistration<T>();

        if (domainEvent is null)
        {
            _logger?.LogError("Attempted to publish a null event of type {EventType}.", typeof(T).FullName);
            throw new ArgumentNullException(nameof(domainEvent), "Cannot publish a null event.");
        }
    }

    /// <summary>
    /// Validates that an event type has been registered.
    /// </summary>
    /// <typeparam name="T">The event type to validate.</typeparam>
    /// <exception cref="MissingEventTypeException">
    /// Thrown when the event type <typeparamref name="T"/> has not been registered.
    /// </exception>
    /// <remarks>
    /// This method is called internally to ensure that event types are registered before
    /// they can be used for publishing or subscription. It can be overridden in derived
    /// classes to add additional validation logic.
    /// </remarks>
    protected void ValidateEventRegistration<T>() where T : class, IDomainEvent
    {
        if (!_eventTypes.ContainsKey(typeof(T)))
        {
            throw new MissingEventTypeException($"The event type {nameof(T)} was not registered");
        }
    }

    /// <summary>
    /// Subscribes a synchronous subscriber to an event type.
    /// </summary>
    /// <typeparam name="T">The event type to subscribe to. Must be a registered event type.</typeparam>
    /// <param name="subscriber">The subscriber instance. Cannot be null.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="subscriber"/> is null.
    /// </exception>
    /// <exception cref="MissingEventTypeException">
    /// Thrown when the event type <typeparamref name="T"/> has not been registered.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method adds a subscriber to the list of subscribers for the specified event type.
    /// When events of this type are published, the subscriber's <see cref="IEventSubscriber{T}.OnNext(T)"/>
    /// method will be called.
    /// </para>
    /// <para>
    /// Subscription is idempotent - subscribing the same subscriber multiple times will only
    /// result in one subscription. A warning will be logged if a duplicate subscription is attempted.
    /// </para>
    /// <para>
    /// This method is thread-safe and can be called concurrently from multiple threads.
    /// </para>
    /// <para>
    /// <strong>Important:</strong> Remember to call <see cref="UnsubscribeFromEventType{T}(IEventSubscriber{T})"/>
    /// when the subscriber is no longer needed to prevent memory leaks.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var aggregator = new EventAggregator();
    /// aggregator.RegisterEventType&lt;UserRegisteredEvent&gt;();
    /// 
    /// var emailSender = new WelcomeEmailSender();
    /// aggregator.SubscribeToEventType(emailSender);
    /// 
    /// // Later, when the subscriber is no longer needed:
    /// aggregator.UnsubscribeFromEventType(emailSender);
    /// </code>
    /// </example>
    public virtual void SubscribeToEventType<T>(IEventSubscriber<T> subscriber) where T : class, IDomainEvent
    {
        ValidateEventRegistration<T>();
        SubscribeSubscriber<T>(subscriber);
    }

    /// <summary>
    /// Adds a subscriber (sync or async) to the internal subscriber dictionary for the event type.
    /// </summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="subscriber">The subscriber instance.</param>
    /// <remarks>
    /// Used internally by both sync and async subscription methods.
    /// </remarks>
    protected void SubscribeSubscriber<T>(IEventSubscriber subscriber) where T : class, IDomainEvent
    {
        var dict = _subscribersByType.GetOrAdd(typeof(T), _ => new ConcurrentDictionary<IEventSubscriber, byte>());
        if (!dict.TryAdd(subscriber, 0))
        {
            _logger?.LogWarning("Subscriber {SubscriberType} is already subscribed to event type {EventType}.",
                subscriber?.GetType().FullName, typeof(T).FullName);
        }
    }

    /// <summary>
    /// Subscribes an asynchronous subscriber to an event type.
    /// </summary>
    /// <typeparam name="T">The event type to subscribe to. Must be a registered event type.</typeparam>
    /// <param name="subscriber">The async subscriber instance. Cannot be null.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="subscriber"/> is null.</exception>
    /// <exception cref="MissingEventTypeException">Thrown when the event type <typeparamref name="T"/> has not been registered.</exception>
    /// <remarks>
    /// This method adds an async subscriber to the list of subscribers for the specified event type.
    /// Subscription is idempotent; duplicate subscriptions are ignored with a warning.
    /// </remarks>
    public virtual void SubscribeToEventType<T>(IAsyncEventSubscriber<T> subscriber) where T : class, IDomainEvent
    {
        ValidateEventRegistration<T>();
        SubscribeSubscriber<T>(subscriber);
    }

    /// <summary>
    /// Unsubscribes a synchronous subscriber from an event type.
    /// </summary>
    /// <typeparam name="T">The event type to unsubscribe from. Must be a registered event type.</typeparam>
    /// <param name="subscriber">The subscriber instance to unsubscribe. Cannot be null.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="subscriber"/> is null.
    /// </exception>
    /// <exception cref="MissingEventTypeException">
    /// Thrown when the event type <typeparamref name="T"/> has not been registered.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method removes a subscriber from the list of subscribers for the specified event type.
    /// After unsubscribing, the subscriber will no longer receive events of this type.
    /// </para>
    /// <para>
    /// Unsubscription is idempotent - unsubscribing a subscriber that is not subscribed will
    /// not cause any errors.
    /// </para>
    /// <para>
    /// This method is thread-safe and can be called concurrently from multiple threads.
    /// </para>
    /// <para>
    /// <strong>Important:</strong> Always call this method when a subscriber is no longer needed
    /// to prevent memory leaks, especially for long-lived aggregators.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var aggregator = new EventAggregator();
    /// aggregator.RegisterEventType&lt;UserRegisteredEvent&gt;();
    /// 
    /// var emailSender = new WelcomeEmailSender();
    /// aggregator.SubscribeToEventType(emailSender);
    /// 
    /// // When the subscriber is no longer needed:
    /// aggregator.UnsubscribeFromEventType(emailSender);
    /// </code>
    /// </example>
    public virtual void UnsubscribeFromEventType<T>(IEventSubscriber<T> subscriber) where T : class, IDomainEvent
    {
        UnsubscribeSubscriber<T>(subscriber);
    }

    /// <summary>
    /// Removes a subscriber (sync or async) from the internal subscriber dictionary for the event type.
    /// </summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="subscriber">The subscriber instance.</param>
    /// <remarks>
    /// Used internally by both sync and async unsubscription methods.
    /// </remarks>
    protected void UnsubscribeSubscriber<T>(IEventSubscriber subscriber) where T : class, IDomainEvent
    {
        ValidateSubscriber(subscriber, typeof(IEventSubscriber<T>));
        ValidateEventRegistration<T>();
        TryToRemoveSubscriber<T>(subscriber);
    }

    /// <summary>
    /// Validates that a subscriber is not null and is of the expected type.
    /// </summary>
    /// <param name="subscriber">The subscriber instance.</param>
    /// <param name="subscriberType">The expected subscriber type.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="subscriber"/> is null.</exception>
    protected void ValidateSubscriber(IEventSubscriber subscriber, Type subscriberType)
    {
        if (subscriber is null)
        {
            _logger?.LogError("Attempted to unsubscribe a null subscriber of type {SubscriberType}.", subscriberType.FullName);
            throw new ArgumentNullException(nameof(subscriber));
        }
    }

    /// <summary>
    /// Attempts to remove a subscriber from the internal subscriber dictionary for the event type.
    /// </summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="subscriber">The subscriber instance.</param>
    private void TryToRemoveSubscriber<T>(IEventSubscriber subscriber) where T : class, IDomainEvent
    {
        if (_subscribersByType.TryGetValue(typeof(T), out var dict))
        {
            dict.TryRemove(subscriber, out _);
        }
    }

    /// <summary>
    /// Unsubscribes an asynchronous subscriber from an event type.
    /// </summary>
    /// <typeparam name="T">The event type to unsubscribe from. Must be a registered event type.</typeparam>
    /// <param name="subscriber">The async subscriber instance to unsubscribe. Cannot be null.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="subscriber"/> is null.</exception>
    /// <exception cref="MissingEventTypeException">Thrown when the event type <typeparamref name="T"/> has not been registered.</exception>
    /// <remarks>
    /// This method removes an async subscriber from the list of subscribers for the specified event type.
    /// Unsubscription is idempotent; unsubscribing a non-existent subscriber is a no-op.
    /// </remarks>
    public virtual void UnsubscribeFromEventType<T>(IAsyncEventSubscriber<T> subscriber) where T : class, IDomainEvent
    {
        UnsubscribeSubscriber<T>(subscriber);
    }
}
