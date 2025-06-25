# Changelog

All notable changes to this project will be documented in this file.

## [1.0.0] - YYYY-MM-DD

### Added

- Initial stable release of Yaref92.Events.
- Support for both synchronous and asynchronous event subscribers.
- Thread-safe, memory-leak resistant subscription management.
- Extensible architecture for future integrations (e.g., Rx, MediatR).

### Changed

- **Breaking:** Rx (Reactive Extensions) support has been removed from the core package and is now available as a separate extension package (`Yaref92.Events.Rx`).
- **Breaking:** All Rx-based APIs and dependencies have been removed from the core.

### Migration

- If you use Rx features, add a reference to the new `Yaref92.Events.Rx` package and update your code to use its APIs.
- See the README for updated usage examples and migration steps.

---
