# Yaref92.Events

A lightweight, extensible, and type-safe event aggregator for .NET, supporting both synchronous event publishing and subscription with optional Reactive Extensions (Rx) integration.  
Designed for decoupled communication in modern applications.

---

![Latest Release](https://img.shields.io/github/v/release/yaron-E92/events)
[![Build Status](https://github.com/yaron-E92/events/actions/workflows/ci-pipeline.yaml/badge.svg)](https://github.com/yaron-E92/events/actions/workflows/ci-pipeline.yaml)
[![License](https://img.shields.io/github/license/yaron-E92/events)](https://github.com/yaron-E92/events/blob/main/LICENSE)
[![Last Commit](https://img.shields.io/github/last-commit/yaron-E92/events)](https://github.com/yaron-E92/events/commits/main)

---

## Table of Contents

- [Yaref92.Events](#yaref92events)
  - [Table of Contents](#table-of-contents)
  - [Features](#features)
  - [Networked Event Aggregation](#networked-event-aggregation)
  - [Installation](#installation)
    - [Core Package](#core-package)
    - [Rx Integration (Optional)](#rx-integration-optional)
  - [Quick Start](#quick-start)
    - [1. Define an Event](#1-define-an-event)
    - [2. Implement a Subscriber](#2-implement-a-subscriber)
    - [3. Register Event Type and Subscribe](#3-register-event-type-and-subscribe)
    - [4. Publish Events](#4-publish-events)
  - [API Overview](#api-overview)
    - [Core Interfaces](#core-interfaces)
    - [Main Methods](#main-methods)
  - [Usage Guide](#usage-guide)
    - [Registering and Publishing Events](#registering-and-publishing-events)
    - [Subscribing](#subscribing)
    - [Unsubscribing](#unsubscribing)
    - [Error Handling](#error-handling)
  - [Rx Integration](#rx-integration)
    - [Basic Rx Usage](#basic-rx-usage)
    - [Rx Subscribers](#rx-subscribers)
  - [Async Support](#async-support)
    - [Async Subscribers](#async-subscribers)
    - [Async Event Publishing](#async-event-publishing)
    - [Cancellation Support](#cancellation-support)
    - [Mixed Sync and Async Subscribers](#mixed-sync-and-async-subscribers)
    - [Async Rx Subscribers](#async-rx-subscribers)
    - [Performance Benefits](#performance-benefits)
  - [Thread Safety](#thread-safety)
  - [Memory Management](#memory-management)
    - [Subscription Lifecycle](#subscription-lifecycle)
    - [Memory Leak Prevention](#memory-leak-prevention)
  - [Logging](#logging)
    - [Logged Events](#logged-events)
  - [Extensibility](#extensibility)
  - [Versioning \& Breaking Changes](#versioning--breaking-changes)
  - [Changelog](#changelog)
  - [License](#license)

---

## Features

- **Type-safe event publishing and subscription**
- **Thread-safe, memory-leak resistant subscription management**
- **Comprehensive logging support with Microsoft.Extensions.Logging**
- **No external dependencies in the core package**
- **Optional Rx integration via separate package**
- **Extensible architecture for future integrations**
- **Full async/await support with parallel execution**
- **Cancellation support for long-running async operations**
- **Mixed sync and async subscriber support**
- **Distributed/networked event propagation with pluggable transports**
- **Event deduplication and memory-safe cleanup**

---

## Networked Event Aggregation

Yaref92.Events supports distributed event-driven applications via pluggable network transports.

### Features
- Pluggable transport abstraction (`IEventTransport`)
- TCP transport implementation (`TCPEventTransport`)
- Networked event aggregator (`NetworkedEventAggregator`)
- Event deduplication and memory-safe cleanup
- Envelope-based serialization with type safety

### Basic Usage Example

```csharp
using Yaref92.Events;
using Yaref92.Events.Abstractions;
using Yaref92.Events.Transports;
using Yaref92.Events.Serialization;

// 1. Create a local in-memory aggregator
var localAggregator = new EventAggregator();

// 2. Create a TCP transport (listening on port 9000)
var transport = new TCPEventTransport(9000, new JsonEventSerializer());

// 3. Create a networked aggregator
var networkedAggregator = new NetworkedEventAggregator(localAggregator, transport);

// 4. Register your event types
networkedAggregator.RegisterEventType<MyEvent>();

// 5. Subscribe to events as usual
networkedAggregator.SubscribeToEventType(new MyEventHandler());

// 6. Publish events (locally and over the network)
networkedAggregator.PublishEvent(new MyEvent("Hello, world!"));

// 7. (Optional) Connect to peers
await transport.ConnectToPeerAsync("remotehost", 9000);
```

> **Note:** If you plan to call `transport.ConnectToPeerAsync` later, keep a reference to the original `TCPEventTransport` instance. `NetworkedEventAggregator` only depends on the `IEventTransport` abstraction, so it cannot initiate new peer connections once the transport reference goes out of scope.

### Event Type Requirements

All events must inherit from `DomainEventBase`:

```csharp
public class MyEvent : DomainEventBase
{
    public string Message { get; }
    public MyEvent(string message, DateTime? occurred = null, Guid? eventId = null)
        : base(occurred, eventId)
    {
        Message = message;
    }
}
```

### Deduplication & Memory Management

- The `NetworkedEventAggregator` deduplicates events using a unique `EventId` on each event.
- The deduplication window is configurable (default: 15 minutes).
- Old event IDs are periodically cleaned up to prevent memory bloat.

**Example:**
```csharp
// Set a custom deduplication window (e.g., 5 minutes)
var networkedAggregator = new NetworkedEventAggregator(localAggregator, transport, TimeSpan.FromMinutes(5));
```

### Security & Hardening

- The TCP transport is suitable for trusted networks.
- For production, consider:
  - Limiting simultaneous connections
  - Enabling idle timeouts
  - Adding authentication or encryption
  - Handling malformed messages robustly

### API Reference

- **`IEventTransport`**: Abstraction for network transports.
- **`TCPEventTransport`**: TCP-based implementation.
- **`NetworkedEventAggregator`**: Aggregator that bridges local and networked event delivery.
- **`DomainEventBase`**: Base class for all events, ensures unique `EventId`.

### Migration Notes

- All event types must now inherit from `DomainEventBase`.
- Existing in-memory usage is unaffected unless you opt-in to networked features.

---

## Installation

### Core Package

Install the main package via NuGet:

```sh
dotnet add package Yaref92.Events
```

### Rx Integration (Optional)

For Reactive Extensions support:

```sh
dotnet add package Yaref92.Events.Rx
```

---

## Quick Start

### 1. Define an Event

```csharp
public class UserRegisteredEvent : DomainEventBase
{
    public string UserId { get; }
    public UserRegisteredEvent(string userId) : base()
    {
        UserId = userId;
    }
}
```

### 2. Implement a Subscriber

```csharp
public class WelcomeEmailSender : IEventSubscriber<UserRegisteredEvent>
{
    public void OnNext(UserRegisteredEvent @event)
    {
        // Send welcome email
        Console.WriteLine($"Welcome email sent to user: {@event.UserId}");
    }
}
```

### 3. Register Event Type and Subscribe

```csharp
var aggregator = new EventAggregator();
aggregator.RegisterEventType<UserRegisteredEvent>();

aggregator.SubscribeToEventType(new WelcomeEmailSender());
```

### 4. Publish Events

```csharp
aggregator.PublishEvent(new UserRegisteredEvent("user-123"));
```

---

## API Overview

### Core Interfaces

- `IDomainEvent`  
  Marker interface for events. Requires `DateTime DateTimeOccurredUtc` and `Guid EventId` (via `DomainEventBase`).

- `IEventSubscriber<T>`  
  Synchronous event subscriber. Implements `void OnNext(T @event)`.

- `IAsyncEventSubscriber<T>`  
  Asynchronous event subscriber. Implements `Task OnNextAsync(T @event, CancellationToken cancellationToken = default)`.

- `IEventAggregator`  
  Main interface for registering event types, subscribing, unsubscribing, and publishing events.

### Main Methods

| Method                                      | Description                                 |
|----------------------------------------------|---------------------------------------------|
| `RegisterEventType<T>()`                     | Register an event type                      |
| `SubscribeToEventType<T>(IEventSubscriber<T>)` | Subscribe a synchronous handler        |
| `SubscribeToEventType<T>(IAsyncEventSubscriber<T>)` | Subscribe an asynchronous handler   |
| `UnsubscribeFromEventType<T>(IEventSubscriber<T>)` | Unsubscribe a synchronous handler    |
| `UnsubscribeFromEventType<T>(IAsyncEventSubscriber<T>)` | Unsubscribe an asynchronous handler |
| `PublishEvent<T>(T domainEvent)`             | Publish event synchronously                 |
| `PublishEventAsync<T>(T domainEvent, CancellationToken cancellationToken = default)` | Publish event asynchronously |

---

## Usage Guide

### Registering and Publishing Events

- Always register your event types before subscribing or publishing.
- You can have multiple subscribers for the same event type.
- Event registration is idempotent - calling multiple times has no effect.

### Subscribing

- Use `SubscribeToEventType` for synchronous subscribers.
- Subscription is idempotent - subscribing the same subscriber multiple times has no effect.

### Unsubscribing

- Always unsubscribe when your subscriber is no longer needed to prevent memory leaks.
- Unsubscription is idempotent - unsubscribing a non-subscribed subscriber has no effect.

### Error Handling

- Exceptions thrown by subscribers are not caught by the aggregator. Handle exceptions within your subscriber logic.
- The aggregator validates events and throws descriptive exceptions for invalid operations.

---

## Rx Integration

The optional `Yaref92.Events.Rx` package provides Reactive Extensions integration:

### Basic Rx Usage

```csharp
using var aggregator = new RxEventAggregator();
aggregator.RegisterEventType<UserRegisteredEvent>();

// Subscribe using Rx
var subscription = aggregator.EventStream
    .OfType<UserRegisteredEvent>()
    .Where(e => e.UserId.StartsWith("admin"))
    .Subscribe(e => Console.WriteLine($"Admin user: {e.UserId}"));

aggregator.PublishEvent(new UserRegisteredEvent("admin-123"));
```

### Rx Subscribers

```csharp
public class AuditLogger : IRxSubscriber<UserRegisteredEvent>
{
    public void OnNext(UserRegisteredEvent @event) => Console.WriteLine($"Audit: {@event.UserId}");
    public void OnError(Exception error) => Console.WriteLine($"Error: {error.Message}");
    public void OnCompleted() => Console.WriteLine("Audit logging completed");
}

var aggregator = new RxEventAggregator();
aggregator.RegisterEventType<UserRegisteredEvent>();
aggregator.SubscribeToEventType(new AuditLogger());
```

---

## Async Support

Yaref92.Events supports both synchronous and asynchronous event handling, with full cancellation support for async operations.

### Async Subscribers

Implement `IAsyncEventSubscriber<T>` for asynchronous event handling:

```csharp
public class EmailService : IAsyncEventSubscriber<UserRegisteredEvent>
{
    public async Task OnNextAsync(UserRegisteredEvent @event, CancellationToken cancellationToken = default)
    {
        // Simulate async email sending
        await Task.Delay(100, cancellationToken);
        Console.WriteLine($"Welcome email sent to: {@event.UserId}");
    }
}
```

### Async Event Publishing

Use `PublishEventAsync` for asynchronous event publishing:

```csharp
var aggregator = new EventAggregator();
aggregator.RegisterEventType<UserRegisteredEvent>();

var emailService = new EmailService();
aggregator.SubscribeToEventType(emailService);

// Publish asynchronously
await aggregator.PublishEventAsync(new UserRegisteredEvent("user-123"));
```

### Cancellation Support

Async operations support cancellation via `CancellationToken`:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // 5 second timeout

try
{
    await aggregator.PublishEventAsync(new UserRegisteredEvent("user-123"), cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Event publishing was cancelled");
}
```

### Mixed Sync and Async Subscribers

You can mix synchronous and asynchronous subscribers for the same event type:

```csharp
var aggregator = new EventAggregator();
aggregator.RegisterEventType<UserRegisteredEvent>();

// Sync subscriber
var logger = new ConsoleLogger();
aggregator.SubscribeToEventType(logger);

// Async subscriber
var emailService = new EmailService();
aggregator.SubscribeToEventType(emailService);

// Both will receive the event
await aggregator.PublishEventAsync(new UserRegisteredEvent("user-123"));
```

### Async Rx Subscribers

For Rx integration with async support, use `AsyncRxSubscriber<T>`:

```csharp
public class AsyncAuditLogger : AsyncRxSubscriber<UserRegisteredEvent>
{
    public override async Task OnNextAsync(UserRegisteredEvent @event, CancellationToken cancellationToken = default)
    {
        await Task.Delay(50, cancellationToken); // Simulate async work
        Console.WriteLine($"Async audit: {@event.UserId}");
    }
}
```

### Performance Benefits

- **Parallel execution**: Async subscribers are executed in parallel using `Task.WhenAll`
- **Non-blocking**: Sync subscribers are called directly, async subscribers are awaited
- **Cancellation**: Full support for cancelling long-running async operations
- **Memory efficient**: No additional allocations for cancellation tokens (default parameter)

---

## Thread Safety

The EventAggregator is designed to be thread-safe:

- **Concurrent Registration**: Multiple threads can register event types simultaneously
- **Concurrent Subscription**: Multiple threads can subscribe/unsubscribe simultaneously  
- **Concurrent Publishing**: Multiple threads can publish events simultaneously
- **Safe Iteration**: Subscriber collections are safely iterated during event publishing

All operations use thread-safe collections internally to ensure consistency.

---

## Memory Management

### Subscription Lifecycle

- **Subscribe**: Add subscribers when they're needed
- **Unsubscribe**: Remove subscribers when they're no longer needed
- **Dispose**: For RxEventAggregator, dispose when the aggregator is no longer needed

### Memory Leak Prevention

- Always unsubscribe subscribers when they're no longer needed
- Use `using` statements with RxEventAggregator for automatic disposal
- The aggregator uses weak references internally to help prevent memory leaks

---

## Logging

The EventAggregator supports comprehensive logging via Microsoft.Extensions.Logging:

```csharp
var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<EventAggregator>();
var aggregator = new EventAggregator(logger);
```

### Logged Events

- **Warning**: Duplicate event type registration
- **Warning**: Duplicate subscriber subscription  
- **Error**: Attempting to publish null events
- **Error**: Attempting to unsubscribe null subscribers

---

## Extensibility

- **Rx Support:**  
  Rx (Reactive Extensions) support is available via the optional `Yaref92.Events.Rx` package.
- **Other Integrations:**  
  You can build adapters for MediatR, ASP.NET, or other frameworks as needed.

---

## Versioning & Breaking Changes

- **1.0.0** is a major release with breaking changes, including the removal of Rx from the core package.
- See the [Changelog](#changelog) for migration steps and details.

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for a full list of changes and migration instructions.

---

## License

This project is licensed under the GPL-3 License. See [LICENSE](LICENSE) for details.

---
