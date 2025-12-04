# MauiEventMessenger sample

A lightweight .NET MAUI sample that demonstrates how to use `NetworkedEventAggregator` with `TCPEventTransport` to exchange events between two devices running the app.

## Running

1. Open the project on two devices or emulators and ensure both are on the same network.
2. Each device should display its detected host address and the configured port (default `5050`). Tap **Start Listener** so both instances begin listening.
3. On device A, type device B's host and port into **Peer Host** and **Peer Port**, then tap **Connect**. Repeat on device B with device A's host and port.
4. Type a message in the **Message** box and tap **Send**. Messages propagate through `NetworkedEventAggregator` and should appear in the message list on both devices along with a toast notification.

You can change the default listening port in `MauiProgram` if your environment requires a different port.
