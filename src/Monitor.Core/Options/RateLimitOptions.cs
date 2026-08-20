using System.ComponentModel.DataAnnotations;

namespace Monitor.Core.Options;

/// <summary>Mirrors config.rateLimit (windowMs / maxRequests).</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    [Range(1, int.MaxValue)]
    public int WindowMs { get; set; } = 900_000;

    [Range(1, int.MaxValue)]
    public int MaxRequests { get; set; } = 100;
}
