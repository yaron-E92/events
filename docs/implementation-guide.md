# Implementation Guide

This guide walks through the steps required to integrate **Yaref92.Events** and its resilient TCP transport into another application. It assumes you are building on .NET 8 or newer.

## 1. Install the Packages

1. Add the core package reference:
   ```bash
   dotnet add package Yaref92.Events
   ```
2. (Optional) Add the TCP transport package when you need to publish or receive events over the network:
   ```bash
   dotnet add package Yaref92.Events.Transport.Tcp
   ```

## 2. Compose the Aggregator and Transport

The resilient transport is exposed through `TCPEventTransport(int listenPort, IEventSerializer?, TimeSpan?, string?)`. It wires a shared `SessionManager` into both the inbound listener (`PersistentPortListener`) and outbound publisher (`PersistentEventPublisher`) so events stay durable across reconnect attempts.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yaref92.Events;
using Yaref92.Events.Serialization;
using Yaref92.Events.Transports;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton<EventAggregator>();
        services.AddSingleton<TCPEventTransport>(sp => new TCPEventTransport(
            listenPort: 5050,
            serializer: new JsonEventSerializer(),
            heartbeatInterval: TimeSpan.FromSeconds(15),
            authenticationToken: "shared-secret"));

        services.AddSingleton<NetworkedEventAggregator>(sp =>
        {
            var local = sp.GetRequiredService<EventAggregator>();
            var transport = sp.GetRequiredService<TCPEventTransport>();
            return new NetworkedEventAggregator(local, transport);
        });
    });
```

The `NetworkedEventAggregator` subscribes to `IEventTransport.EventReceived`, so any domain events raised by the transport are immediately re-published to in-process subscribers without extra plumbing.

## 3. Observe the Session Pipeline

`TCPEventTransport` wires a resilient listener and publisher around a shared `SessionManager`, which coordinates:

- Session creation and deduplication based on `SessionKey`.
- Long-running `ResilientInboundConnection` instances owned by `PersistentPortListener`.
- `ResilientCompositSessionConnection` objects that persist outbound envelopes and maintain the durable outbox.

`PersistentPortListener` raises `IInboundConnectionManager.EventReceived` whenever a `ResilientInboundConnection` produces a domain event; `TCPEventTransport` subscribes to it and forwards the callback through `IEventTransport.EventReceived`, which is what `NetworkedEventAggregator` consumes. Because the same delegate chain controls acknowledgements, completing the handler successfully indicates that the event was processed and allows `PersistentEventPublisher.AcknowledgeEventReceipt` to trim the outbox for that session.

## 4. Start Listening and Join Peers

Call `StartListeningAsync` during application startup so that inbound peers can authenticate and exchange frames:

```csharp
await transport.StartListeningAsync(cancellationToken);
```

To proactively connect to remote peers, use the same transport instance:

```csharp
await transport.ConnectToPeerAsync("peer-host", 5050, cancellationToken);
```

Because the listener and publisher share the same `SessionManager`, reconnect attempts reuse the existing session state, resend unacknowledged envelopes, and keep the inbound heartbeat timers aligned.

## 5. Publish and Subscribe

Register event types and subscribers on the `NetworkedEventAggregator` just like the in-memory aggregator:

```csharp
var aggregator = host.Services.GetRequiredService<NetworkedEventAggregator>();
aggregator.RegisterEventType<UserRegistered>();

aggregator.SubscribeToEventType(new UserRegisteredHandler());
await aggregator.PublishEventAsync(new UserRegistered(Guid.NewGuid()));
```

Outbound events are serialized via the transport’s `IEventSerializer` and published over every authenticated session. Inbound events arrive through `IInboundConnectionManager.EventReceived`, bubble up to `IEventTransport.EventReceived`, and finally land in your local handlers.

## 6. Handle Application Lifetime

- **Startup:** Resolve `TCPEventTransport` from the host and invoke `StartListeningAsync` before accepting traffic.
- **Shutdown:** Dispose the host (or call `await transport.DisposeAsync()`) to flush ACKs, cancel heartbeat loops, and close the listener gracefully.

## 7. Deployment Considerations

- **Heartbeats:** Tune the optional `heartbeatInterval` constructor argument to balance failure detection and background traffic.
- **Authentication:** Supply `authenticationToken` when your peers must present shared credentials. Leave it `null` for anonymous meshes on trusted networks.
- **Observability:** Subscribe to `IInboundConnectionManager.EventReceived` and `IEventTransport.SessionInboundConnectionDropped` to emit metrics about reconnect attempts, ACK latency, and handler throughput.
- **Testing:** Use `dotnet test` locally and review the integration tests under `tests/Yaref92.Events.IntegrationTests` for concrete examples of reconnection flows.

For detailed protocol semantics, see [`docs/networking/resilient-tcp.md`](./networking/resilient-tcp.md) and the README section on resilient transport configuration.

## 8. Documentation Completeness Checklist

Before tagging a release or onboarding new teams, verify that the following artifacts reflect the current codebase:

1. **README** – Confirms the feature list, installation steps, quick start, API surface, and migration guidance. This document acts as the landing page for developers evaluating the aggregator.
2. **Implementation Guide (this file)** – Explains how to compose the aggregator, transport, and diagnostics end-to-end, including operational responsibilities and deployment considerations.
3. **Resilient TCP Transport reference** – Documents frame formats, authentication flows, heartbeat defaults, persistent outbox behavior, and wiring examples for `TCPEventTransport`.

If any feature work touches the aggregator lifecycle, transport configuration, or event contracts, update the relevant section(s) above in the same pull request.

## 9. Testing Completeness Checklist

Run `dotnet test Yaref92.Events.sln` and ensure the following suites pass before releasing:

- **`tests/Yaref92.Events.UnitTests`** – Covers the core in-memory aggregator behaviors (registration, publishing, deduplication windows, memory management, and logging hooks).
- **`tests/Yaref92.Events.Rx.UnitTests`** – Validates the optional Rx integration so reactive subscribers continue to honor the aggregator contracts.
- **`tests/Yaref92.Events.IntegrationTests`** – Exercises the resilient TCP transport over real sockets, including authentication, heartbeat monitoring, reconnect backoff, ACK replay, and aggregator rehydration.

Record the test run (including the command output) in the release PR description or changelog entry for traceability.
