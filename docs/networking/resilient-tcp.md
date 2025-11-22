# Resilient TCP Transport

The resilient TCP transport is the recommended way to host `NetworkedEventAggregator` in production. It layers reconnection logic, heartbeat monitoring, optional authentication, and a persistent outbox on top of the base `TCPEventTransport` so that transient network failures do not interrupt event delivery.

## Session Frames

Resilient sessions exchange a small set of frame types over the wire. Each frame is encoded as JSON using the helpers defined in [`src/Yaref92.Events/Sessions/SessionFrame.cs`](../../src/Yaref92.Events/Sessions/SessionFrame.cs) and carries a canonical `Guid` identifier in the `id` field so that senders and receivers always agree on the inflight frame:

| Frame | Purpose |
| --- | --- |
| `AUTH` | Client credentials. Carries the session token and optional shared secret. The server must accept the token before any events can flow. |
| `PING` | Heartbeat probes sent by both sides to prove liveness. |
| `PONG` | Heartbeat response. Resetting the remote activity timer suppresses reconnection attempts. |
| `MSG` | Event payload. Contains the envelope produced by `IEventSerializer`. Every message uses the sender-assigned `Guid` identifier for deduplication and replay. |
| `ACK` | Acknowledges that a specific message identifier from the sender's outbox has been durably processed. The receiver echoes the `Guid` identifier so inflight tracking can survive reconnects. |

The transport automatically serializes and dispatches these frames; application code only needs to publish and subscribe to domain events.

## Inbound session lifecycle

`PersistentPortListener` hosts the TCP listener, handles the initial authentication handshake, and passes authenticated sockets to the correct `ResilientInboundConnection`. Each inbound connection keeps the receive loop, heartbeat timer, and deduplication state for one peer while sharing the process-wide `SessionManager`. When a peer reconnects, the existing `ResilientInboundConnection` is rehydrated so inflight frames continue flowing without forcing a new aggregator registration.

## Persistent publisher responsibilities

Outbound delivery is coordinated by `PersistentEventPublisher`, which spins up a `ResilientCompositSessionConnection` per session key. Each composite connection owns both a `ResilientOutboundConnection` and the paired `ResilientInboundConnection`, giving it everything it needs to:

- Create the session token that carries the `SessionKey` and optional authentication secret.
- Persist the outbox to disk and replay unacknowledged frames after reconnect.
- Drive exponential backoff and retry loops when sockets drop or authentication fails.
- Raise callbacks for inbound frames so the listener’s `IInboundConnectionManager.EventReceived` pipeline can dispatch events or respond with ACKs.
- Correlate ACK frames with inflight entries and trim the durable outbox.

Callers enqueue event payloads through `TCPEventTransport.PublishEventAsync`; the publisher takes care of persistence, reconnect orchestration, and heartbeat monitoring behind the scenes.

## Authentication Modes

Authentication is controlled through `ResilientSessionOptions`:

- `RequireAuthentication` – When `true`, every inbound connection must present an `AUTH` frame that matches `AuthenticationToken`. Unauthenticated peers are rejected before event frames are processed.
- `AuthenticationToken` – Shared secret distributed to trusted peers. Leave this unset to allow anonymous connections on private networks.

You can set these options when building the transport (see [Wiring the Transport](#wiring-the-transport)).

## Heartbeat and Backoff Defaults

`ResilientSessionOptions` also governs liveness detection and reconnection pacing:

- `HeartbeatInterval` – Defaults to 30 seconds. Each active session emits a `PING` at this cadence.
- `HeartbeatTimeout` – Defaults to 90 seconds (3× the interval). When the timeout elapses without a `PONG` or any other frame, the peer is considered dead and the client reconnect loop resumes.
- `SessionBufferWindow` – Defaults to 5 minutes. The outbox retains unacknowledged events within this window so they can be replayed on reconnect.
- `BackoffInitialDelay` – Defaults to 1 second. First retry delay after a failed connection attempt.
- `BackoffMaxDelay` – Defaults to 30 seconds. Caps the exponential backoff so that long outages do not stall recovery indefinitely.

All values can be overridden per transport instance. Use shorter intervals for aggressive failover or longer ones to reduce background traffic on constrained links.

## Persistent Outbox Artifacts

Every `ResilientCompositSessionConnection` writes its queued events to an on-disk outbox located at:

```
<AppContext.BaseDirectory>/outbox.json
```

The file contains the durable queue of event envelopes awaiting acknowledgement from remote peers. Entries are marked as `IsQueued` once written and are cleared as soon as the transport receives the matching `ACK` frame. Because ACKs now echo the canonical `Guid` assigned to the original frame, replay after reconnect is deterministic. The file is guarded by a cross-process `SemaphoreSlim` so multiple transports running in the same process cannot corrupt the artifact.

- **Backups** – Include the outbox in host backups if you need to guarantee at-least-once delivery across restarts.
- **Rotation** – The outbox automatically prunes acknowledged entries. If you need custom retention, customize the persistence hooks inside `ResilientCompositSessionConnection`.

## Wiring the Transport

`NetworkedEventAggregator` consumes an `IEventTransport`. To opt into the resilient TCP behavior:

```csharp
using Yaref92.Events;
using Yaref92.Events.Serialization;
using Yaref92.Events.Transports;

var localAggregator = new EventAggregator();

var transport = new TCPEventTransport(
    listenPort: 9000,
    serializer: new JsonEventSerializer(),
    heartbeatInterval: TimeSpan.FromSeconds(15),
    authenticationToken: "shared-secret"
);

var networkedAggregator = new NetworkedEventAggregator(localAggregator, transport);

// Register event types and start listening.
networkedAggregator.RegisterEventType<MyEvent>();
await transport.StartListeningAsync();

// Connect to a peer and benefit from the persistent session.
await transport.ConnectToPeerAsync("peer-host", 9000);
```

Key integration notes:

1. **Share the Aggregator** – Pass the same `EventAggregator` instance to both the local and networked components. This allows the transport to rehydrate events from the outbox back into the in-process aggregator on reconnect.
2. **Start Listening Early** – Call `StartListeningAsync` during application bootstrap so that inbound peers can authenticate and join the mesh.
3. **Keep the Transport Alive** – Hold onto the `TCPEventTransport` reference for as long as the application should stay connected. Disposing it tears down the server, persistent clients, and heartbeat loops.
4. **Configure Options** – Adjust heartbeat and authentication inputs to meet your deployment’s reliability and security requirements.

For advanced scenarios (multi-process failover, custom serialization, or alternative storage for the outbox), consult the source in `src/Yaref92.Events/Transports/` for extension points.
