using System.ComponentModel.DataAnnotations;

namespace Monitor.Core.Options;

/// <summary>
/// Root process-level options. Mirrors the bare `env` / `port` fields that
/// used to sit at the top of the Node `config` object
/// (backend/src/config/index.ts).
/// </summary>
/// <remarks>
/// The source derived a single `config.isDev` flag from `NODE_ENV`. In the
/// target, the ASP.NET Core host already owns that concept end-to-end
/// (<c>ASPNETCORE_ENVIRONMENT</c> / <c>IWebHostEnvironment.IsDevelopment()</c>),
/// which is what Program.cs actually uses (e.g. for the Serilog bootstrap).
/// This class intentionally does NOT duplicate an `IsDevelopment` flag here —
/// doing so would create two independently-settable env switches
/// (<c>App:Environment</c> vs. <c>ASPNETCORE_ENVIRONMENT</c>) that could
/// drift out of sync. <see cref="Environment"/> is kept only as
/// configuration data mirroring the source's `config.env` value.
/// </remarks>
public sealed class AppOptions
{
    public const string SectionName = "App";

    [Required(AllowEmptyStrings = false)]
    public string Environment { get; set; } = "Development";

    [Range(1, 65535)]
    public int Port { get; set; } = 4000;
}
