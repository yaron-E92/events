using System.Globalization;
using Yaref92.Events.Transport.Grpc.Protos;
using Yaref92.Events.Transports.Events;

namespace Yaref92.Events.Transport.Grpc.Models;

internal enum GrpcSessionFrameKind
{
    Auth,
    Ping,
    Pong,
    Message,
    Ack,
}

internal sealed record GrpcReconnectToken(string SessionKey, string Token);

internal sealed record GrpcAuthPayload(string SessionToken, string? Secret, GrpcReconnectToken? ReconnectToken);

internal sealed record GrpcMessagePayload(Guid EventId, EventEnvelope Envelope, string? DeduplicationId)
{
    public string DeduplicationCorrelation => DeduplicationId ?? EventId.ToString("D", CultureInfo.InvariantCulture);
}

internal sealed record GrpcAckPayload(Guid EventId, string? DeduplicationId)
{
    public string DeduplicationCorrelation => DeduplicationId ?? EventId.ToString("D", CultureInfo.InvariantCulture);
}

internal sealed record GrpcSessionFrame(
    GrpcSessionFrameKind Kind,
    string? SessionId,
    GrpcAuthPayload? Auth,
    GrpcMessagePayload? Message,
    GrpcAckPayload? Ack)
{
    public static GrpcSessionFrame FromProto(SessionFrame frame)
    {
        return frame.FrameCase switch
        {
            SessionFrame.FrameOneofCase.Auth => CreateAuthFrame(frame),
            SessionFrame.FrameOneofCase.Ping => new GrpcSessionFrame(GrpcSessionFrameKind.Ping, frame.SessionId, null, null, null),
            SessionFrame.FrameOneofCase.Pong => new GrpcSessionFrame(GrpcSessionFrameKind.Pong, frame.SessionId, null, null, null),
            SessionFrame.FrameOneofCase.Message => CreateMessageFrame(frame),
            SessionFrame.FrameOneofCase.Ack => CreateAckFrame(frame),
            _ => throw new InvalidOperationException("Unknown session frame kind"),
        };
    }

    public static SessionFrame ToProto(GrpcSessionFrame frame)
    {
        SessionFrame protoFrame = new()
        {
            SessionId = frame.SessionId ?? string.Empty,
        };

        switch (frame.Kind)
        {
            case GrpcSessionFrameKind.Auth:
                if (frame.Auth is null)
                {
                    throw new InvalidOperationException("Auth payload is required for AUTH frames.");
                }

                protoFrame.Auth = new AuthFrame
                {
                    SessionToken = frame.Auth.SessionToken,
                    Secret = frame.Auth.Secret ?? string.Empty,
                };

                if (frame.Auth.ReconnectToken is not null)
                {
                    protoFrame.Auth.ReconnectToken = new ReconnectToken
                    {
                        SessionKey = frame.Auth.ReconnectToken.SessionKey,
                        Token = frame.Auth.ReconnectToken.Token,
                    };
                }

                break;
            case GrpcSessionFrameKind.Ping:
                protoFrame.Ping = new PingFrame();
                break;
            case GrpcSessionFrameKind.Pong:
                protoFrame.Pong = new PongFrame();
                break;
            case GrpcSessionFrameKind.Message:
                if (frame.Message is null)
                {
                    throw new InvalidOperationException("Message payload is required for MSG frames.");
                }

                protoFrame.Message = new MessageFrame
                {
                    EventId = frame.Message.EventId.ToString("D", CultureInfo.InvariantCulture),
                    DeduplicationId = frame.Message.DeduplicationCorrelation,
                    Envelope = new Protos.EventEnvelope
                    {
                        EventId = frame.Message.Envelope.EventId.ToString("D", CultureInfo.InvariantCulture),
                        TypeName = frame.Message.Envelope.TypeName ?? string.Empty,
                        EventJson = frame.Message.Envelope.EventJson ?? string.Empty,
                    },
                };

                break;
            case GrpcSessionFrameKind.Ack:
                if (frame.Ack is null)
                {
                    throw new InvalidOperationException("Ack payload is required for ACK frames.");
                }

                protoFrame.Ack = new AckFrame
                {
                    EventId = frame.Ack.EventId.ToString("D", CultureInfo.InvariantCulture),
                    DeduplicationId = frame.Ack.DeduplicationCorrelation,
                };

                break;
            default:
                throw new InvalidOperationException("Unknown session frame kind");
        }

        return protoFrame;
    }

    private static GrpcSessionFrame CreateAuthFrame(SessionFrame frame)
    {
        AuthFrame auth = frame.Auth;
        GrpcReconnectToken? reconnect = null;

        if (auth.ReconnectToken is not null && !string.IsNullOrWhiteSpace(auth.ReconnectToken.SessionKey))
        {
            reconnect = new GrpcReconnectToken(auth.ReconnectToken.SessionKey, auth.ReconnectToken.Token);
        }

        return new GrpcSessionFrame(
            GrpcSessionFrameKind.Auth,
            frame.SessionId,
            new GrpcAuthPayload(auth.SessionToken, string.IsNullOrWhiteSpace(auth.Secret) ? null : auth.Secret, reconnect),
            null,
            null);
    }

    private static GrpcSessionFrame CreateMessageFrame(SessionFrame frame)
    {
        MessageFrame message = frame.Message;
        Guid eventId = ParseGuid(message.EventId, nameof(message.EventId));
        Protos.EventEnvelope envelope = message.Envelope ?? throw new InvalidOperationException("Message frames require an envelope");

        EventEnvelope domainEnvelope = new(
            ParseGuid(envelope.EventId, nameof(envelope.EventId)),
            string.IsNullOrWhiteSpace(envelope.TypeName) ? null : envelope.TypeName,
            string.IsNullOrWhiteSpace(envelope.EventJson) ? null : envelope.EventJson);

        return new GrpcSessionFrame(
            GrpcSessionFrameKind.Message,
            frame.SessionId,
            null,
            new GrpcMessagePayload(eventId, domainEnvelope, string.IsNullOrWhiteSpace(message.DeduplicationId) ? null : message.DeduplicationId),
            null);
    }

    private static GrpcSessionFrame CreateAckFrame(SessionFrame frame)
    {
        AckFrame ack = frame.Ack;

        return new GrpcSessionFrame(
            GrpcSessionFrameKind.Ack,
            frame.SessionId,
            null,
            null,
            new GrpcAckPayload(ParseGuid(ack.EventId, nameof(ack.EventId)), string.IsNullOrWhiteSpace(ack.DeduplicationId) ? null : ack.DeduplicationId));
    }

    private static Guid ParseGuid(string value, string fieldName)
    {
        if (Guid.TryParse(value, out Guid parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Field '{fieldName}' must be a valid GUID string.");
    }
}
