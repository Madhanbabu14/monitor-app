using System.Text.Json;
using Monitor.Core.Errors;

namespace Monitor.UnitTests.Errors;

public class ErrorEnvelopeTests
{
    [Fact]
    public void Constructor_SetsFixedStatusAndGivenMessage()
    {
        var envelope = new ErrorEnvelope("Invalid email or password");

        Assert.Equal("error", envelope.Status);
        Assert.Equal("Invalid email or password", envelope.Message);
    }

    [Fact]
    public void Serialize_ProducesExactLegacyEnvelopeShape()
    {
        var envelope = new ErrorEnvelope("An unexpected error occurred");

        var json = JsonSerializer.Serialize(envelope);

        Assert.Equal("{\"status\":\"error\",\"message\":\"An unexpected error occurred\"}", json);
    }

    [Fact]
    public void Serialize_PropertyNamesAreLowercase_MatchingSourceJsonShape()
    {
        var envelope = new ErrorEnvelope("Route GET /nope not found");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("status", out var statusProp));
        Assert.Equal("error", statusProp.GetString());

        Assert.True(root.TryGetProperty("message", out var messageProp));
        Assert.Equal("Route GET /nope not found", messageProp.GetString());

        // Exactly two properties - no stack trace / extra fields leak into the wire shape.
        var propertyCount = 0;
        foreach (var _ in root.EnumerateObject())
        {
            propertyCount++;
        }
        Assert.Equal(2, propertyCount);
    }

    [Fact]
    public void Serialize_MessageWithSpecialCharacters_IsEscapedCorrectly()
    {
        var envelope = new ErrorEnvelope("quote \" and backslash \\ and newline \n");

        var json = JsonSerializer.Serialize(envelope);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("quote \" and backslash \\ and newline \n", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void Constructor_EmptyMessage_RoundTripsAsEmptyString()
    {
        var envelope = new ErrorEnvelope(string.Empty);

        Assert.Equal(string.Empty, envelope.Message);
        Assert.Equal("{\"status\":\"error\",\"message\":\"\"}", JsonSerializer.Serialize(envelope));
    }
}
