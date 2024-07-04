namespace Yaref92.Events;

/// <summary>
/// This Exception denotes a missing event type
/// in the event aggregator
/// </summary>
internal class MissingEventTypeException(string? message) : Exception(message)
{
}
