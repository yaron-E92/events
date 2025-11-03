# Implementation Guide

This guide walks through the steps required to integrate **Yaref92.Events** and its resilient TCP transport into another application. It assumes you are building on .NET 8 or newer.

## 1. Install the Package

1. Add the NuGet package reference:
   ```bash
   dotnet add package Yaref92.Events
   ```
2. (Optional) Add the transport package if you plan to publish or receive events over TCP:
   ```bash
   dotnet add package Yaref92.Events.Transport.Tcp
   ```

## 2. Configure Services

Register the event aggregator and transport in your dependency injection container. The resilient transport relies on a single `ResilientSessionClient` instance that manages the TCP connection, reconnection/backoff, and ACK correlation.

```csharp
services.AddSingleton<IEventAggregator, NetworkedEventAggregator>();
services.AddSingleton(provider =>
{
    var logger = provider.GetRequiredService<ILogger<ResilientSessionClient>>();
    return ResilientSessionClient.Create(
        host: "events.example.com",
        port: 5050,
        options: new ResilientSessionOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(15),
            ReconnectBackoff = TimeSpan.FromSeconds(5),
            AuthenticationToken = "shared-secret"
        },
        logger);
});
services.AddSingleton<IEventTransport>(provider =>
{
    var client = provider.GetRequiredService<ResilientSessionClient>();
    var logger = provider.GetRequiredService<ILogger<TCPEventTransport>>();
    return new TCPEventTransport(client, logger);
});
```

## 3. Wire Inbound Dispatch

`ResilientSessionClient` exposes an inbound frame callback that you can hook into your aggregator. When the client receives frames, it will dispatch them to the registered callback, ensuring canonical frame IDs and ACK correlation are handled automatically.

```csharp
var aggregator = provider.GetRequiredService<NetworkedEventAggregator>();
var client = provider.GetRequiredService<ResilientSessionClient>();

client.OnFrameReceived += async frame =>
{
    var envelope = EventEnvelope.Deserialize(frame.Payload);
    await aggregator.PublishAsync(envelope);
};
```

## 4. Publish Events

Publishing is unchanged for local subscribers. When the resilient transport is registered, outbound events are serialized and sent over the TCP session, with ACKs routed back through the client.

```csharp
await aggregator.PublishAsync(new UserRegistered(domainUserId));
```

## 5. Handle Application Lifetime

- Start the client when your application boots:
  ```csharp
  await client.ConnectAsync(cancellationToken);
  ```
- Stop the client gracefully during shutdown to flush ACKs and close the session:
  ```csharp
  await client.DisposeAsync();
  ```

## 6. Deployment Considerations

- **Heartbeats:** Ensure your infrastructure allows the configured heartbeat interval to keep the session alive.
- **Authentication:** Rotate shared secrets or certificates periodically to match your security policies.
- **Observability:** Enable structured logging and metrics around reconnect attempts, ACK latency, and handler throughput.
- **Scaling:** Each application instance should maintain its own resilient session client. Use load balancing to distribute inbound connections if hosting the TCP server.

## 7. Testing

- Use the integration tests under `tests/Yaref92.Events.IntegrationTests` as reference implementations for reconnection, inbound dispatch, and ACK validation scenarios.
- Run `dotnet test` locally to validate your wiring before deploying.

## Next Steps

For detailed protocol semantics, see [`docs/networking/resilient-tcp.md`](./networking/resilient-tcp.md) and the README sections on resilient transport configuration.
