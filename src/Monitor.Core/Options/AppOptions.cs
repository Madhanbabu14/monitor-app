using System.ComponentModel.DataAnnotations;

namespace Monitor.Core.Options;

/// <summary>
/// Root process-level options. Mirrors the bare `env` / `port` / `isDev`
/// fields that used to sit at the top of the Node `config` object
/// (backend/src/config/index.ts).
/// </summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    [Required(AllowEmptyStrings = false)]
    public string Environment { get; set; } = "Development";

    [Range(1, 65535)]
    public int Port { get; set; } = 4000;

    /// <summary>Equivalent of the source's `config.isDev` derived flag.</summary>
    public bool IsDevelopment => string.Equals(Environment, "Development", StringComparison.OrdinalIgnoreCase);
}
