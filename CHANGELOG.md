# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Documentation for the resilient TCP transport, covering session frames, authentication, heartbeats/backoff, persistent outbox handling, and integrating with `NetworkedEventAggregator`.
- README guidance on configuring heartbeat intervals, authentication tokens, and durable outbox behavior for `TCPEventTransport`.

## [1.2.1] - 2025-10-23

### Fixed

- Hardened the TCP transport to surface publish failures and ensure background tasks recover cleanly.
- Ensured TCP client connections are cleaned up after failures and documented how to retain transport references for peer scenarios.
- Expanded test coverage for TCP publish failure paths, including socket error handling through the event aggregator.

---

## [1.2.0] - 2024-06-29

### Added

- **Networked event aggregation**
  - New `IEventTransport` abstraction for pluggable network transports
  - `TCPEventTransport` implementation for peer-to-peer and client/server event propagation
  - `NetworkedEventAggregator` for bridging local and networked event delivery
  - Envelope-based JSON serialization with type safety
- **Event deduplication and memory management**
  - Deduplication of events using unique `EventId` on each event
  - Configurable deduplication window (default: 15 minutes)
  - Periodic cleanup of old event IDs to prevent memory bloat
- **Event identity enforcement**
  - All events must now inherit from `DomainEventBase`, which guarantees a unique `EventId` and UTC timestamp
- **Documentation**
  - Comprehensive README updates for networked usage, event requirements, deduplication, and security

### Changed

- Improved naming and clarity of deduplication methods
- Removed redundant deduplication logic
- Added proper resource disposal in tests to address analyzer warnings

### Migration

- All event types must now inherit from `DomainEventBase`
- Existing in-memory usage is unaffected unless you opt-in to networked features
- See the README for updated usage and migration notes

---

## [1.1.0] - 2025-06-29

### Added

- **Async event publishing and subscription support**
  - New `IAsyncEventSubscriber<T>` interface for asynchronous event handling
  - `PublishEventAsync<T>()` method for asynchronous event publishing
  - Parallel execution of async subscribers using `Task.WhenAll`
  - Mixed sync and async subscriber support for the same event type
- **Cancellation support for async operations**
  - Optional `CancellationToken` parameter in async methods
  - Full cancellation propagation to async subscribers
  - Proper `OperationCanceledException` handling
- **Enhanced Rx integration with async support**
  - `AsyncRxSubscriber<T>` base class for async Rx subscribers
  - Async support in `RxEventAggregator` with cancellation
  - Fire-and-forget async handling for Rx subscribers
- **Comprehensive async testing**
  - Tests for async subscriber subscription and unsubscription
  - Cancellation scenario testing
  - Mixed sync/async subscriber testing
  - Exception propagation testing

### Changed

- **Enhanced API surface** - Added overloads for async subscriber management
- **Improved performance** - Async subscribers execute in parallel
- **Better error handling** - Cancellation exceptions are properly propagated

### Migration

- No breaking changes - all new features are additive
- Existing sync subscribers continue to work unchanged
- To use async features, implement `IAsyncEventSubscriber<T>` and use `PublishEventAsync<T>()`
- See the README for comprehensive async usage examples

---

## [1.0.0] - 2025-06-26

### Added

- Initial stable release of Yaref92.Events
- Core event aggregator with type-safe event registration and publishing
- Thread-safe subscription management using concurrent collections
- Comprehensive logging support with Microsoft.Extensions.Logging
- Memory leak prevention through proper subscription lifecycle management
- Null argument validation with descriptive exceptions
- Extensible architecture for future integrations

### Changed

- **Breaking:** Rx (Reactive Extensions) support has been moved to a separate extension package (`Yaref92.Events.Rx`)
- **Breaking:** All Rx-based APIs and dependencies have been removed from the core package
- **Breaking:** Simplified subscription management - removed ISubscription interface
- **Breaking:** Renamed internal field `_subscribersByType` to `_subscriptionGroups` for clarity

### Fixed

- Thread safety issues in concurrent registration and subscription operations
- Memory leak potential in subscription management
- Improved exception handling and error messages
- Enhanced logging with proper log levels and structured logging

### Removed

- `ISubscription` interface and `Subscription` class
- Rx dependencies from core package
- Async event subscriber interfaces (to be added in future releases)

### Migration

- If you use Rx features, add a reference to the new `Yaref92.Events.Rx` package and update your code to use its APIs
- Update any code that references the removed `ISubscription` interface
- See the README for updated usage examples and migration steps

---

## [0.2.0] - 2024-07-05

### Added

- Initial beta release
- Basic event aggregator functionality
- Rx integration in core package
- ISubscription interface for subscription management

### Known Issues

- Thread safety concerns in concurrent operations
- Memory leak potential in subscription management
- Limited logging and error handling

---
