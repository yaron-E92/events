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
        var evt = new DummyEvent(DateTime.UtcNow, "test");

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
} 
