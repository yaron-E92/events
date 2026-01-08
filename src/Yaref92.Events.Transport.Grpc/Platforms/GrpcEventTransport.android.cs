#if ANDROID
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using SIPSorcery.Net;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.Transport.Grpc;

public sealed partial class GrpcEventTransport
{
    private const int WebRtcAnswerTimeoutSeconds = 5;
    private readonly ConcurrentDictionary<Guid, WebRtcSession> _webRtcSessions = new();
    private TcpListener? _signalingListener;
    private CancellationTokenSource? _signalingCts;
    private Task? _signalingLoop;

    public Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        if (_signalingListener is not null)
        {
            return Task.CompletedTask;
        }

        _signalingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _signalingListener = new TcpListener(IPAddress.Any, _listenPort);
        _signalingListener.Start();
        _signalingLoop = Task.Run(() => AcceptSignalingConnectionsAsync(_signalingCts.Token), _signalingCts.Token);
        return Task.CompletedTask;
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
                message = await WebRtcSignaling.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
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
                    await session.ConnectAsync(message, timeout: null, cancellationToken).ConfigureAwait(false);
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

    private async Task DisposeAsyncCore()
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

        foreach (var channel in _channels)
        {
            channel.Dispose();
        }
    }

    private async Task<bool> TryConnectToPeerViaWebRtcAsync(SessionKey sessionKey, string host, int port, CancellationToken cancellationToken)
    {
        TcpClient? client = null;
        WebRtcSession? session = null;
        var connected = false;

        try
        {
            client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            session = new WebRtcSession(this, stream, ownsStream: true, client, sessionKey);
            _webRtcSessions.TryAdd(session.Id, session);
            _ = Task.Run(() => session.ReceiveSignalingMessagesAsync(cancellationToken), cancellationToken);

            var timeout = TimeSpan.FromSeconds(WebRtcAnswerTimeoutSeconds);
            connected = await session.ConnectAsync(offer: null, timeout, cancellationToken).ConfigureAwait(false);
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

    private sealed class WebRtcSession : IDataChannelSession
    {
        private readonly GrpcEventTransport _transport;
        private readonly NetworkStream _stream;
        private readonly RTCPeerConnection _peerConnection;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly bool _ownsStream;
        private readonly TcpClient? _client;
        private readonly TaskCompletionSource<bool> _answerReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SessionKey? _sessionKey;
        private RTCDataChannel? _dataChannel;
        private StreamRegistration? _registration;

        public WebRtcSession(GrpcEventTransport transport, NetworkStream stream, bool ownsStream, TcpClient? client = null, SessionKey? sessionKey = null)
        {
            _transport = transport;
            _stream = stream;
            _ownsStream = ownsStream;
            _client = client;
            _sessionKey = sessionKey;
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

        public Task<bool> ConnectAsync(SignalMessage? offer, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            if (offer is null)
            {
                var connectTimeout = timeout ?? TimeSpan.FromSeconds(WebRtcAnswerTimeoutSeconds);
                return InitializeOfferAsync(connectTimeout, cancellationToken);
            }

            return ConnectAsAnswererAsync(offer, cancellationToken);
        }

        public async Task<bool> InitializeOfferAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            _dataChannel = await _peerConnection.createDataChannel("events", new RTCDataChannelInit());
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
                    message = await WebRtcSignaling.ReadMessageAsync(_stream, cancellationToken).ConfigureAwait(false);
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
                sdpMLineIndex = (ushort) (candidate.SdpMLineIndex ?? 0),
            };

            _peerConnection.addIceCandidate(iceCandidate);
        }

        public async ValueTask DisposeAsync()
        {
            IsActive = false;
            _transport.UnregisterDataChannelSession(_registration, "session disposed");
            _registration = null;

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
                if (_registration is not null)
                {
                    _transport.UnregisterDataChannelSession(_registration, "replaced by new data channel open");
                }

                _registration = _transport.RegisterDataChannelSession(channel);
                if (_sessionKey is not null)
                {
                    _ = _transport.SendAuthFrameAsync(_registration, _sessionKey);
                }
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
                _transport.UnregisterDataChannelSession(_registration, "data channel closed");
                _registration = null;
            };
        }

        private Task SendAsync(SignalMessage message, CancellationToken cancellationToken = default)
        {
            return SendMessageAsync(message, cancellationToken);
        }

        Task IDataChannelSession.SendAsync(SignalMessage message, CancellationToken cancellationToken)
        {
            return SendAsync(message, cancellationToken);
        }

        void IDataChannelSession.HookInboundFrames(RTCDataChannel channel)
        {
            HookDataChannel(channel);
        }

        private async Task SendMessageAsync(SignalMessage message, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WebRtcSignaling.WriteMessageAsync(_stream, message, cancellationToken).ConfigureAwait(false);
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

        private async Task<bool> ConnectAsAnswererAsync(SignalMessage offer, CancellationToken cancellationToken)
        {
            await HandleOfferAsync(offer, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

}
#endif
