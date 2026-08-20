using System.ComponentModel.DataAnnotations;

namespace Monitor.Core.Options;

/// <summary>Azure AD tenant/app registration used for SSO token validation (Monitor.Identity).</summary>
public sealed class AzureAdOptions
{
    public const string SectionName = "AzureAd";

    [Required(AllowEmptyStrings = false)]
    public string TenantId { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string ClientId { get; set; } = string.Empty;
}
