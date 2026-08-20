using System.ComponentModel.DataAnnotations;

namespace Monitor.Core.Options;

/// <summary>App-issued JWT signing options (Monitor.Identity). Mirrors config.jwt.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required(AllowEmptyStrings = false)]
    public string Secret { get; set; } = string.Empty;

    /// <summary>Same shorthand duration syntax as the source ("8h").</summary>
    public string ExpiresIn { get; set; } = "8h";
}
