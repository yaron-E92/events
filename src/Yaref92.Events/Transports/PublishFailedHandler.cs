using System;
using System.Threading;
using System.Threading.Tasks;

using Yaref92.Events.Abstractions;

namespace Yaref92.Events.Transports;

/// <summary>
/// Default asynchronous handler for <see cref="PublishFailed"/> events.
/// Logs the failure details so that operators can take action.
/// </summary>
public sealed class PublishFailedHandler : IAsyncEventSubscriber<PublishFailed>
{
    private readonly Func<PublishFailed, CancellationToken, Task> _handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="PublishFailedHandler"/> class.
    /// </summary>
    /// <param name="handler">
    /// Optional delegate used to process <see cref="PublishFailed"/> events. When not supplied the
    /// handler writes a diagnostic message to <see cref="Console.Error"/>.
    /// </param>
    public PublishFailedHandler(Func<PublishFailed, CancellationToken, Task>? handler = null)
    {
        _handler = handler ?? LogFailureAsync;
    }

    /// <inheritdoc />
    public Task OnNextAsync(PublishFailed domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return _handler(domainEvent, cancellationToken);
    }

    private static Task LogFailureAsync(PublishFailed domainEvent, CancellationToken cancellationToken)
    {
        var endpoint = domainEvent.Endpoint is null
            ? "unknown endpoint"
            : domainEvent.Endpoint.ToString();

        var message = $"Publish to {endpoint} failed: {domainEvent.Exception}";
        return Console.Error.WriteLineAsync(message);
    }
}
