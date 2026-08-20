namespace Monitor.Core.Options;

/// <summary>Mirrors config.cors. No [Required] — the source has a hard default.</summary>
public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    public string Origin { get; set; } = "http://localhost:3000";
}
