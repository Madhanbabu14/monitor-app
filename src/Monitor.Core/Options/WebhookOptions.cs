using System.ComponentModel.DataAnnotations;

namespace Monitor.Core.Options;

/// <summary>Mirrors config.webhook. Required — fails fast at boot if missing.</summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhook";

    [Required(AllowEmptyStrings = false)]
    public string Secret { get; set; } = string.Empty;
}
