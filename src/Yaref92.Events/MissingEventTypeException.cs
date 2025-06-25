namespace Yaref92.Events;

/// <summary>
/// Exception thrown when attempting to use an event type that has not been registered with the event aggregator.
/// </summary>
/// <remarks>
/// <para>
/// This exception is thrown when:
/// <list type="bullet">
/// <item><description>Attempting to publish an event of an unregistered type</description></item>
/// <item><description>Attempting to subscribe to an unregistered event type</description></item>
/// <item><description>Attempting to unsubscribe from an unregistered event type</description></item>
/// </list>
/// </para>
/// <para>
/// To resolve this exception, ensure that the event type is registered using
/// <see cref="IEventAggregator.RegisterEventType{T}()"/> before attempting to use it.
/// </para>
/// </remarks>
/// <example>
/// <para>Example of what causes this exception:</para>
/// <code>
/// var aggregator = new EventAggregator();
/// 
/// // This will throw MissingEventTypeException because UserRegisteredEvent is not registered
/// aggregator.PublishEvent(new UserRegisteredEvent("user-123"));
/// </code>
/// <para>To fix, register the event type first:</para>
/// <code>
/// var aggregator = new EventAggregator();
/// aggregator.RegisterEventType&lt;UserRegisteredEvent&gt;();
/// 
/// // Now this will work
/// aggregator.PublishEvent(new UserRegisteredEvent("user-123"));
/// </code>
/// </example>
internal class MissingEventTypeException(string? message) : Exception(message)
{
}
