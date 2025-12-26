#if ANDROID
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

using Google.Protobuf;
using Grpc.Core;
using SIPSorcery.Net;

namespace Yaref92.Events.Transport.Grpc;

public sealed partial class GrpcEventTransport
{
    private const int SignalMessageBufferSize = 4;
    private const int MaxSignalMessageSize = 64 * 1024;
    private static readonly JsonSerializerOptions SignalMessageOptions = new(JsonSerializerDefaults.Web);
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
                    session = new WebRtcSession(this, stream);
                    _webRtcSessions.TryAdd(session.Id, session);
                    await session.HandleOfferAsync(message, cancellationToken).ConfigureAwait(false);
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

    private static async Task<SignalMessage?> ReadSignalMessageAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBuffer = new byte[SignalMessageBufferSize];
        await stream.ReadExactlyAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
        if (length <= 0 || length > MaxSignalMessageSize)
        {
            return null;
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<SignalMessage>(payload, SignalMessageOptions);
    }

    private static async Task WriteSignalMessageAsync(NetworkStream stream, SignalMessage message, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, SignalMessageOptions);
        byte[] lengthBuffer = new byte[SignalMessageBufferSize];
        BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, payload.Length);
        await stream.WriteAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
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

    private sealed class WebRtcSession : IAsyncDisposable
    {
        private readonly GrpcEventTransport _transport;
        private readonly NetworkStream _stream;
        private readonly RTCPeerConnection _peerConnection;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private RTCDataChannel? _dataChannel;
        private StreamRegistration? _registration;

        public WebRtcSession(GrpcEventTransport transport, NetworkStream stream)
        {
            _transport = transport;
            _stream = stream;
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
            if (_registration is not null)
            {
                _transport.UnregisterStream(_registration);
                _registration = null;
            }

            _dataChannel?.close();
            _peerConnection.close();
            _sendLock.Dispose();
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

                TransportFrame frame;
                try
                {
                    frame = TransportFrame.Parser.ParseFrom(data);
                }
                catch (InvalidProtocolBufferException)
                {
                    return;
                }

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
    }

    private sealed class WebRtcStreamWriter : IAsyncStreamWriter<TransportFrame>
    {
        private readonly RTCDataChannel _channel;

        public WebRtcStreamWriter(RTCDataChannel channel)
        {
            _channel = channel;
        }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(TransportFrame message)
        {
            byte[] payload = message.ToByteArray();
            _channel.send(payload);
            return Task.CompletedTask;
        }
    }

    private sealed class SignalMessage
    {
        public const string OfferType = "offer";
        public const string AnswerType = "answer";
        public const string CandidateType = "candidate";

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("sdp")]
        public string? Sdp { get; set; }

        [JsonPropertyName("candidate")]
        public string? Candidate { get; set; }

        [JsonPropertyName("sdpMid")]
        public string? SdpMid { get; set; }

        [JsonPropertyName("sdpMLineIndex")]
        public int? SdpMLineIndex { get; set; }
    }
}
#endif
