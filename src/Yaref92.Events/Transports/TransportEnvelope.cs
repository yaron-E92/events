using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaref92.Events.Transports;

internal enum TransportMessageType
{
    Event,
    Ack,
    Ping,
    Pong,
    Auth,
}

internal sealed class TransportEnvelope
{
    [JsonPropertyName("type")]
    public TransportMessageType MessageType { get; set; }

    [JsonPropertyName("id")]
    public long? MessageId { get; set; }

    [JsonPropertyName("payload")]
    public string? Payload { get; set; }

    public static TransportEnvelope CreateEvent(long messageId, string payload) => new()
    {
        MessageType = TransportMessageType.Event,
        MessageId = messageId,
        Payload = payload,
    };

    public static TransportEnvelope CreateAck(long messageId) => new()
    {
        MessageType = TransportMessageType.Ack,
        MessageId = messageId,
    };

    public static TransportEnvelope CreatePing() => new()
    {
        MessageType = TransportMessageType.Ping,
    };

    public static TransportEnvelope CreatePong() => new()
    {
        MessageType = TransportMessageType.Pong,
    };

    public static TransportEnvelope CreateAuth(string token) => new()
    {
        MessageType = TransportMessageType.Auth,
        Payload = $"AUTH:{token}",
    };
}

internal static class TransportEnvelopeSerializer
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
