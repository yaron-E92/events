# Yaref92.Events

A lightweight, extensible, and type-safe event aggregator for .NET, supporting both synchronous and asynchronous event publishing and subscription.  
Designed for decoupled communication in modern applications.

---

[![NuGet](https://img.shields.io/nuget/v/Yaref92.Events.svg)](https://github.com/yaron-E92/events/pkgs/nuget/Yaref92.Events)
[![Build Status](https://img.shields.io/github/actions/workflow/status/yaref92/Yaref92.Events/ci.yml?branch=main)](https://github.com/yaref92/Yaref92.Events/actions)
[![License](https://img.shields.io/github/license/yaref92/Yaref92.Events)](LICENSE)

---

## Table of Contents

- [Features](#features)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [API Overview](#api-overview)
- [Usage Guide](#usage-guide)
- [Extensibility](#extensibility)
- [Versioning & Breaking Changes](#versioning--breaking-changes)
- [Changelog](#changelog)
- [Contributing](#contributing)
- [License](#license)

---

## Features

- **Type-safe event publishing and subscription**
- **Supports both synchronous and asynchronous event handlers**
- **Thread-safe, memory-leak resistant subscription management**
- **No external dependencies in the core package**
- **Extensible: add Rx, MediatR, or other integrations via separate packages**

---

## Installation

Install via NuGet:

```sh
dotnet add package Yaref92.Events
```

---

## Quick Start

### 1. Define an Event

```csharp
public class UserRegisteredEvent : IDomainEvent
{
    public string UserId { get; }
    public DateTime DateTimeOccurredUtc { get; } = DateTime.UtcNow;

    public UserRegisteredEvent(string userId) => UserId = userId;
}
```

### 2. Implement a Subscriber

#### Synchronous

```csharp
public class WelcomeEmailSender : IEventSubscriber<UserRegisteredEvent>
{
    public void OnNext(UserRegisteredEvent @event)
    {
        // Send welcome email
    }
}
```

#### Asynchronous

```csharp
public class AuditLogger : IAsyncEventSubscriber<UserRegisteredEvent>
{
    public async ValueTask OnNextAsync(UserRegisteredEvent @event, CancellationToken cancellationToken = default)
    {
        // Log audit asynchronously
        await Task.Delay(100, cancellationToken);
    }
}
```

### 3. Register Event Type and Subscribe

```csharp
var aggregator = new EventAggregator();
aggregator.RegisterEventType<UserRegisteredEvent>();

aggregator.SubscribeToEventType(new WelcomeEmailSender());
aggregator.SubscribeToEventTypeAsync(new AuditLogger());
```

### 4. Publish Events

#### Synchronous

```csharp
aggregator.PublishEvent(new UserRegisteredEvent("user-123"));
```

#### Asynchronous

```csharp
await aggregator.PublishEventAsync(new UserRegisteredEvent("user-123"));
```

---

## API Overview

### Core Interfaces

- `IDomainEvent`  
  Marker interface for events. Requires `DateTime DateTimeOccurredUtc`.

- `IEventSubscriber<T>`  
  Synchronous event subscriber. Implements `void OnNext(T @event)`.

- `IAsyncEventSubscriber<T>`  
  Asynchronous event subscriber. Implements `ValueTask OnNextAsync(T @event, CancellationToken cancellationToken = default)`.

- `IEventAggregator`  
  Main interface for registering event types, subscribing, unsubscribing, and publishing events.

### Main Methods

| Method                                      | Description                                 |
|----------------------------------------------|---------------------------------------------|
| `RegisterEventType<T>()`                     | Register an event type                      |
| `SubscribeToEventType<T>(IEventSubscriber<T>)` | Subscribe a sync handler                    |
| `UnsubscribeFromEventType<T>(IEventSubscriber<T>)` | Unsubscribe a sync handler                  |
| `SubscribeToEventTypeAsync<T>(IAsyncEventSubscriber<T>)` | Subscribe an async handler                  |
| `UnsubscribeFromEventTypeAsync<T>(IAsyncEventSubscriber<T>)` | Unsubscribe an async handler                |
| `PublishEvent<T>(T domainEvent)`             | Publish event synchronously                 |
| `PublishEventAsync<T>(T domainEvent)`        | Publish event asynchronously                |

---

## Usage Guide

### Registering and Publishing Events

- Always register your event types before subscribing or publishing.
- You can have multiple subscribers (sync and async) for the same event type.

### Subscribing

- Use `SubscribeToEventType` for synchronous subscribers.
- Use `SubscribeToEventTypeAsync` for asynchronous subscribers.

### Unsubscribing

- Always unsubscribe when your subscriber is no longer needed to prevent memory leaks.

### Error Handling

- Exceptions thrown by subscribers are not caught by the aggregator. Handle exceptions within your subscriber logic.

### Thread Safety

- The aggregator is thread-safe for registration, subscription, and publishing.

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
