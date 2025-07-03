using System.Text.Json;

using FluentAssertions;

using Yaref92.Events.Serialization;

namespace Yaref92.Events.UnitTests.Serialization;

[TestFixture]
public class JsonEventSerializerTests
{
    [Test]
    public void Serialize_And_Deserialize_RoundTrip_Works()
    {
        // Arrange
        var serializer = new JsonEventSerializer();
        var evt = new DummyEvent("test");

        // Act
        var json = serializer.Serialize(evt);
        (Type? type, Abstractions.IDomainEvent? domainEvent) result = serializer.Deserialize(json);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<(Type? type, Abstractions.IDomainEvent? domainEvent)>();
        result.type.Should().Be(typeof(DummyEvent));
        result.domainEvent.Should().NotBeNull().And.BeEquivalentTo(evt);
    }

    [Test]
    public void Serialize_Null_Throws()
    {
        var serializer = new JsonEventSerializer();
        Action act = () => serializer.Serialize<DummyEvent>(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Deserialize_InvalidJson_Throws()
    {
        var serializer = new JsonEventSerializer();
        Action act = () => serializer.Deserialize("not a json");
        act.Should().Throw<JsonException>();
    }

    [Test]
    public void Serialize_And_Deserialize_Preserves_EventId()
    {
        var serializer = new JsonEventSerializer();
        DummyEvent evt = new("test");
        var json = serializer.Serialize(evt);
        (Type? type, Abstractions.IDomainEvent? domainEvent) = serializer.Deserialize(json);
        type.Should().Be(typeof(DummyEvent));
        domainEvent.Should().NotBeNull();
        domainEvent!.EventId.Should().Be(evt.EventId);
    }

    [Test]
    public void New_DummyEvent_Has_NonEmpty_EventId()
    {
        var evt = new DummyEvent();
        evt.EventId.Should().NotBe(Guid.Empty);
    }
} 
