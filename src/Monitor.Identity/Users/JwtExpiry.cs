using System.Globalization;

namespace Monitor.Identity.Users;

/// <summary>
/// Parses the `ms`-style shorthand duration string `jsonwebtoken`'s
/// <c>expiresIn</c> option accepts (config.jwt.expiresIn, e.g. "8h") into a
/// <see cref="TimeSpan"/>. Supports the suffixes the source's default
/// ("8h") and JwtOptions' documented shape use: s/m/h/d/w, plus a bare
/// integer meaning seconds (jsonwebtoken's other accepted form).
/// </summary>
public static class JwtExpiry
{
    public static readonly TimeSpan Default = TimeSpan.FromHours(8);

    public static TimeSpan Parse(string? expiresIn)
    {
        if (string.IsNullOrWhiteSpace(expiresIn))
        {
            return Default;
        }

        var trimmed = expiresIn.Trim();

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        var unit = trimmed[^1];
        var numberPart = trimmed[..^1];
        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return Default;
        }

        return char.ToLowerInvariant(unit) switch
        {
            's' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            'w' => TimeSpan.FromDays(value * 7),
            _ => Default,
        };
    }
}
