# Yaref92.Events

A lightweight, extensible, and type-safe event aggregator for .NET, supporting both synchronous event publishing and subscription with optional Reactive Extensions (Rx) integration.  
Designed for decoupled communication in modern applications.

---

[![NuGet](https://img.shields.io/nuget/v/Yaref92.Events.svg)](https://github.com/yaron-E92/events/pkgs/nuget/Yaref92.Events)
[![Build Status](https://img.shields.io/github/actions/workflow/status/yaref92/Yaref92.Events/ci.yml?branch=main)](https://github.com/yaref92/Yaref92.Events/actions)
[![License](https://img.shields.io/github/license/yaref92/Yaref92.Events)](LICENSE)

---

## Table of Contents

- [Yaref92.Events](#yaref92events)
  - [Table of Contents](#table-of-contents)
  - [Features](#features)
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
public class UserRegisteredEvent : IDomainEvent
{
    public string UserId { get; }
    public DateTime DateTimeOccurredUtc { get; } = DateTime.UtcNow;

    public UserRegisteredEvent(string userId) => UserId = userId;
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
  Marker interface for events. Requires `DateTime DateTimeOccurredUtc`.

- `IEventSubscriber<T>`  
  Synchronous event subscriber. Implements `void OnNext(T @event)`.

- `IEventAggregator`  
  Main interface for registering event types, subscribing, unsubscribing, and publishing events.

### Main Methods

| Method                                      | Description                                 |
|----------------------------------------------|---------------------------------------------|
| `RegisterEventType<T>()`                     | Register an event type                      |
| `SubscribeToEventType<T>(IEventSubscriber<T>)` | Subscribe a handler                    |
| `UnsubscribeFromEventType<T>(IEventSubscriber<T>)` | Unsubscribe a handler                  |
| `PublishEvent<T>(T domainEvent)`             | Publish event synchronously                 |

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
    public void OnCompleted() => Console.WriteLine("Completed");
}

var aggregator = new RxEventAggregator();
aggregator.RegisterEventType<UserRegisteredEvent>();
aggregator.SubscribeToEventType(new AuditLogger());
```

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
