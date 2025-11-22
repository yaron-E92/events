using System;
using System.Text.Json;

using FluentAssertions;

using NUnit.Framework;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.UnitTests.Serialization;

[TestFixture]
public class SessionFrameSerializationTests
{
    public static IEnumerable<TestCaseData> FrameRoundTripCases()
    {
        var authFrame = SessionFrame.CreateAuth("session-token", "auth-secret");
        yield return new TestCaseData(authFrame).SetName("Auth_frame_round_trips");

        var pingFrame = SessionFrame.CreatePing();
        yield return new TestCaseData(pingFrame).SetName("Ping_frame_round_trips");

        var pongFrame = SessionFrame.CreatePong();
        yield return new TestCaseData(pongFrame).SetName("Pong_frame_round_trips");

        var eventId = Guid.NewGuid();
        var payload = "{\"hello\":\"world\"}";
        var eventFrame = SessionFrame.CreateEventFrame(eventId, payload);
        yield return new TestCaseData(eventFrame).SetName("Event_frame_round_trips");

        var ackFrame = SessionFrame.CreateAck(Guid.NewGuid());
        yield return new TestCaseData(ackFrame).SetName("Ack_frame_round_trips");
    }

    [Test]
    [TestCaseSource(nameof(FrameRoundTripCases))]
    public void SessionFrameKindConverter_Preserves_Frame_Data(SessionFrame original)
    {
        var json = JsonSerializer.Serialize(original, SessionFrameSerializer.Options);
        var deserialized = JsonSerializer.Deserialize<SessionFrame>(json, SessionFrameSerializer.Options);

        deserialized.Should().NotBeNull();
        deserialized!.Kind.Should().Be(original.Kind);
        deserialized.Id.Should().Be(original.Id);
        deserialized.Payload.Should().Be(original.Payload);
        deserialized.Token.Should().Be(original.Token);
    }

    [Test]
    public void SessionFrameKindConverter_Deserializes_Long_Event_Alias()
    {
        var json = "{\"kind\":\"message\",\"id\":\"" + Guid.NewGuid() + "\"}";
        var frame = JsonSerializer.Deserialize<SessionFrame>(json, SessionFrameSerializer.Options);
        frame.Should().NotBeNull();
        frame!.Kind.Should().Be(SessionFrameKind.Event);
    }

    [Test]
    public void SessionFrameKindConverter_Throws_For_Unknown_Kinds()
    {
        const string json = "{\"kind\":\"unsupported\"}";
        Action action = () => JsonSerializer.Deserialize<SessionFrame>(json, SessionFrameSerializer.Options);
        action.Should().Throw<JsonException>();
    }
}
