#if NOT_ANDROID
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using SIPSorcery.Net;

namespace Yaref92.Events.Transport.Grpc;

public sealed partial class GrpcEventTransport
{
    private const int WebRtcSignalingPortOffset = 1;
    private const int WebRtcAnswerTimeoutSeconds = 5;
    private readonly ConcurrentDictionary<Guid, WebRtcSession> _webRtcSessions = new();
    private TcpListener? _signalingListener;
    private CancellationTokenSource? _signalingCts;
    private Task? _signalingLoop;

    private async Task StartWebRtcListenerAsync(CancellationToken cancellationToken)
    {
        if (_signalingListener is not null)
        {
            return;
        }

        _signalingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _signalingListener = new TcpListener(IPAddress.Any, _listenPort + WebRtcSignalingPortOffset);
        _signalingListener.Start();
        _signalingLoop = Task.Run(() => AcceptSignalingConnectionsAsync(_signalingCts.Token), _signalingCts.Token);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task StopWebRtcListenerAsync()
    {
        if (_signalingCts is not null)
        {
            _signalingCts.Cancel();
            _signalingCts.Dispose();
            _signalingCts = null;
        }

        if (_signalingListener is not null)
        {
            _signalingListener.Stop();
            _signalingListener = null;
        }

        if (_signalingLoop is not null)
        {
            try
            {
                await _signalingLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _signalingLoop = null;
        }

        foreach (var session in _webRtcSessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task AcceptSignalingConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _signalingListener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleSignalingConnectionAsync(client, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                client?.Dispose();
                break;
            }
        }
    }

    private async Task HandleSignalingConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        await using NetworkStream stream = client.GetStream();
        WebRtcSession? session = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            SignalMessage? message;
            try
            {
                message = await ReadSignalMessageAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                break;
            }

            if (message is null)
            {
                break;
            }

            switch (message.Type)
            {
                case SignalMessage.OfferType:
                    session = new WebRtcSession(this, stream, ownsStream: false);
                    _webRtcSessions.TryAdd(session.Id, session);
                    await session.HandleOfferAsync(message, cancellationToken).ConfigureAwait(false);
                    break;
                case SignalMessage.AnswerType when session is not null:
                    await session.HandleAnswerAsync(message, cancellationToken).ConfigureAwait(false);
                    break;
                case SignalMessage.CandidateType when session is not null:
                    session.HandleCandidate(message);
                    break;
            }
        }

        if (session is not null)
        {
            _webRtcSessions.TryRemove(session.Id, out var _);
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> TryConnectToPeerViaWebRtcAsync(string host, int port, CancellationToken cancellationToken)
    {
        TcpClient? client = null;
        WebRtcSession? session = null;
        var connected = false;

        try
        {
            client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            session = new WebRtcSession(this, stream, ownsStream: true, client);
            _webRtcSessions.TryAdd(session.Id, session);
            _ = Task.Run(() => session.ReceiveSignalingMessagesAsync(cancellationToken), cancellationToken);

            var timeout = TimeSpan.FromSeconds(WebRtcAnswerTimeoutSeconds);
            connected = await session.InitializeOfferAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (!connected)
            {
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (session is null)
            {
                client?.Dispose();
            }
            else if (!connected || !session.IsActive)
            {
                _webRtcSessions.TryRemove(session.Id, out var _);
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class WebRtcSession : IAsyncDisposable
    {
        private readonly GrpcEventTransport _transport;
        private readonly NetworkStream _stream;
        private readonly RTCPeerConnection _peerConnection;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly bool _ownsStream;
        private readonly TcpClient? _client;
        private readonly TaskCompletionSource<bool> _answerReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private RTCDataChannel? _dataChannel;
        private StreamRegistration? _registration;

        public WebRtcSession(GrpcEventTransport transport, NetworkStream stream, bool ownsStream, TcpClient? client = null)
        {
            _transport = transport;
            _stream = stream;
            _ownsStream = ownsStream;
            _client = client;
            _peerConnection = new RTCPeerConnection(new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    new() { urls = "stun:stun.l.google.com:19302" },
                },
            });

            _peerConnection.onicecandidate += candidate =>
            {
                if (candidate is null)
                {
                    return;
                }

                _ = SendAsync(new SignalMessage
                {
                    Type = SignalMessage.CandidateType,
                    Candidate = candidate.candidate,
                    SdpMid = candidate.sdpMid,
                    SdpMLineIndex = candidate.sdpMLineIndex,
                });
            };

            _peerConnection.ondatachannel += channel =>
            {
                _dataChannel = channel;
                HookDataChannel(channel);
            };
        }

        public Guid Id { get; } = Guid.NewGuid();

        public bool IsActive { get; private set; } = true;

        public async Task<bool> InitializeOfferAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            _dataChannel = _peerConnection.createDataChannel("events", new RTCDataChannelInit());
            HookDataChannel(_dataChannel);

            var offer = _peerConnection.createOffer(null);
            await _peerConnection.setLocalDescription(offer).ConfigureAwait(false);

            await SendAsync(new SignalMessage
            {
                Type = SignalMessage.OfferType,
                Sdp = offer.sdp,
            }, cancellationToken).ConfigureAwait(false);

            return await WaitForAnswerAsync(timeout, cancellationToken).ConfigureAwait(false);
        }

        public async Task ReceiveSignalingMessagesAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SignalMessage? message;
                try
                {
                    message = await ReadSignalMessageAsync(_stream, cancellationToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    break;
                }

                if (message is null)
                {
                    break;
                }

                switch (message.Type)
                {
                    case SignalMessage.AnswerType:
                        await HandleAnswerAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case SignalMessage.CandidateType:
                        HandleCandidate(message);
                        break;
                }
            }
        }

        public async Task HandleOfferAsync(SignalMessage offer, CancellationToken cancellationToken)
        {
            var offerDescription = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = offer.Sdp ?? string.Empty,
            };

            _peerConnection.setRemoteDescription(offerDescription);
            var answer = _peerConnection.createAnswer(null);
            await _peerConnection.setLocalDescription(answer).ConfigureAwait(false);

            await SendAsync(new SignalMessage
            {
                Type = SignalMessage.AnswerType,
                Sdp = answer.sdp,
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task HandleAnswerAsync(SignalMessage answer, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(answer.Sdp))
            {
                return;
            }

            var answerDescription = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = answer.Sdp,
            };

            _peerConnection.setRemoteDescription(answerDescription);
            _answerReceived.TrySetResult(true);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public void HandleCandidate(SignalMessage candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.Candidate))
            {
                return;
            }

            var iceCandidate = new RTCIceCandidateInit
            {
                candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid,
                sdpMLineIndex = (ushort)(candidate.SdpMLineIndex ?? 0),
            };

            _peerConnection.addIceCandidate(iceCandidate);
        }

        public async ValueTask DisposeAsync()
        {
            IsActive = false;
            if (_registration is not null)
            {
                _transport.UnregisterStream(_registration);
                _registration = null;
            }

            _dataChannel?.close();
            _peerConnection.close();
            _sendLock.Dispose();

            if (_ownsStream)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _client?.Dispose();
            }
        }

        private void HookDataChannel(RTCDataChannel channel)
        {
            channel.onopen += () =>
            {
                var writer = new WebRtcStreamWriter(channel);
                _registration = _transport.RegisterStream(writer);
            };

            channel.onmessage += async (_, protocol, data) =>
            {
                if (_registration is null || protocol != DataChannelPayloadProtocols.WebRTC_Binary)
                {
                    return;
                }

                if (!DataChannelProtocol.TryDecode(data, out var envelope))
                {
                    return;
                }

                TransportFrame frame = EnvelopeToFrame(envelope);
                await _transport.HandleIncomingFrameAsync(frame, _registration).ConfigureAwait(false);
            };

            channel.onclose += () =>
            {
                if (_registration is null)
                {
                    return;
                }

                _transport.UnregisterStream(_registration);
                _registration = null;
            };
        }

        private Task SendAsync(SignalMessage message, CancellationToken cancellationToken = default)
        {
            return SendMessageAsync(message, cancellationToken);
        }

        private async Task SendMessageAsync(SignalMessage message, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteSignalMessageAsync(_stream, message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task<bool> WaitForAnswerAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await _answerReceived.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _answerReceived.TrySetResult(false);
                return false;
            }
        }
    }
}
#endif
