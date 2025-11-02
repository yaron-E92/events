using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaref92.Events.Transports;

public enum SessionFrameKind
{
    Auth,
    Ping,
    Pong,
    Event,
    Ack,
}

public sealed class SessionFrame
{
    [JsonPropertyName("kind")]
    public SessionFrameKind Kind { get; init; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid Id { get; init; }

    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("payload")]
    public string? Payload { get; init; }

    public static SessionFrame CreateAuth(string token, string? secret = null)
    {
        ArgumentNullException.ThrowIfNull(token);

        return new SessionFrame
        {
            Kind = SessionFrameKind.Auth,
            Token = token,
            Payload = secret,
        };
    }

    public static SessionFrame CreatePing() => new() { Kind = SessionFrameKind.Ping };

    public static SessionFrame CreatePong() => new() { Kind = SessionFrameKind.Pong };

    public static SessionFrame CreateAck(Guid messageId) => new()
    {
        Kind = SessionFrameKind.Ack,
        Id = messageId,
    };

    public static SessionFrame CreateMessage(Guid messageId, string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new SessionFrame
        {
            Kind = SessionFrameKind.Event,
            Id = messageId,
            Payload = payload,
        };
    }
}

internal static class SessionFrameSerializer
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new SessionFrameKindConverter());
        return options;
    }

    private sealed class SessionFrameKindConverter : JsonConverter<SessionFrameKind>
    {
        private const string AuthKind = "AUTH";
        private const string PingKind = "PING";
        private const string PongKind = "PONG";
        private const string MessageKind = "MSG";
        private const string MessageKindLong = "MESSAGE";
        private const string AckKind = "ACK";

        public override SessionFrameKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value?.ToUpperInvariant() switch
            {
                AuthKind => SessionFrameKind.Auth,
                PingKind => SessionFrameKind.Ping,
                PongKind => SessionFrameKind.Pong,
                MessageKind or MessageKindLong => SessionFrameKind.Event,
                AckKind => SessionFrameKind.Ack,
                _ => throw new JsonException($"Unsupported session frame kind '{value}'."),
            };
        }

        public override void Write(Utf8JsonWriter writer, SessionFrameKind value, JsonSerializerOptions options)
        {
            var stringValue = value switch
            {
                SessionFrameKind.Auth => AuthKind,
                SessionFrameKind.Ping => PingKind,
                SessionFrameKind.Pong => PongKind,
                SessionFrameKind.Event => MessageKind,
                SessionFrameKind.Ack => AckKind,
                _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
            };
            writer.WriteStringValue(stringValue);
        }
    }
}
