using System.Text.Json.Serialization;

namespace Monitor.Core.Errors;

/// <summary>
/// The exact JSON shape both `errorHandler` and `notFoundHandler` emitted in
/// the source: <c>{"status":"error","message":"..."}</c>. Kept as a fixed
/// envelope for the strangler window (ProblemDetails is a post-cutover
/// follow-up, gated on the React client dropping this assumption).
/// </summary>
public sealed class ErrorEnvelope
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "error";

    [JsonPropertyName("message")]
    public string Message { get; init; }

    public ErrorEnvelope(string message)
    {
        Message = message;
    }
}
