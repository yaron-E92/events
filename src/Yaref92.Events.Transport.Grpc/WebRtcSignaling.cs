using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaref92.Events.Transport.Grpc;

internal static class WebRtcSignaling
{
    private const int SignalMessageBufferSize = 4;
    private const int MaxSignalMessageSize = 64 * 1024;
    private static readonly JsonSerializerOptions SignalMessageOptions = new(JsonSerializerDefaults.Web);

    public static async Task<SignalMessage?> ReadMessageAsync(NetworkStream stream, CancellationToken cancellationToken)
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

    public static async Task WriteMessageAsync(NetworkStream stream, SignalMessage message, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, SignalMessageOptions);
        byte[] lengthBuffer = new byte[SignalMessageBufferSize];
        BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, payload.Length);
        await stream.WriteAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class SignalMessage
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
