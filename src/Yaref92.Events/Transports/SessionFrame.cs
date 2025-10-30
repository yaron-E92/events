using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaref92.Events.Transports;

internal enum SessionFrameKind
{
    Auth,
    Ping,
    Pong,
    Message,
    Ack,
}

internal sealed class SessionFrame
{
    [JsonPropertyName("kind")]
    public SessionFrameKind Kind { get; init; }

    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("payload")]
    public string? Payload { get; init; }

    public static SessionFrame CreateAuth(string token, string? secret = null)
    {
        if (token is null)
        {
            throw new ArgumentNullException(nameof(token));
        }

        return new SessionFrame
        {
            Kind = SessionFrameKind.Auth,
            Token = token,
            Payload = secret,
        };
    }

    public static SessionFrame CreatePing() => new() { Kind = SessionFrameKind.Ping };

    public static SessionFrame CreatePong() => new() { Kind = SessionFrameKind.Pong };

    public static SessionFrame CreateAck(long messageId) => new()
    {
        Kind = SessionFrameKind.Ack,
        Id = messageId,
    };

    public static SessionFrame CreateMessage(long messageId, string payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        return new SessionFrame
        {
            Kind = SessionFrameKind.Message,
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
        public override SessionFrameKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value?.ToUpperInvariant() switch
            {
                "AUTH" => SessionFrameKind.Auth,
                "PING" => SessionFrameKind.Ping,
                "PONG" => SessionFrameKind.Pong,
                "MSG" or "MESSAGE" => SessionFrameKind.Message,
                "ACK" => SessionFrameKind.Ack,
                _ => throw new JsonException($"Unsupported session frame kind '{value}'."),
            };
        }

        public override void Write(Utf8JsonWriter writer, SessionFrameKind value, JsonSerializerOptions options)
        {
            var stringValue = value switch
            {
                SessionFrameKind.Auth => "AUTH",
                SessionFrameKind.Ping => "PING",
                SessionFrameKind.Pong => "PONG",
                SessionFrameKind.Message => "MSG",
                SessionFrameKind.Ack => "ACK",
                _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
            };
            writer.WriteStringValue(stringValue);
        }
    }
}
