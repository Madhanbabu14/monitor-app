using System.ComponentModel.DataAnnotations;

namespace Monitor.Core.Options;

/// <summary>
/// Two named Postgres connections, replacing the single
/// `DATABASE_URL` (+ undocumented `PRODUCTION_DATABASE_URL`) pair from the
/// Node config. Primary is required and fails fast at boot (ValidateOnStart)
/// if missing/invalid. Operational is a first-class, OPTIONAL, read-only
/// section: it is only registered as an NpgsqlDataSource (in Monitor.Data)
/// when a connection string is actually present.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public PrimaryDatabaseOptions Primary { get; set; } = new();

    /// <summary>Null/unset => operational (production replica) source is not registered.</summary>
    public OperationalDatabaseOptions? Operational { get; set; }
}

public sealed class PrimaryDatabaseOptions
{
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int MaxConnections { get; set; } = 20;

    [Range(0, int.MaxValue)]
    public int IdleTimeoutMs { get; set; } = 30000;

    [Range(0, int.MaxValue)]
    public int ConnectionTimeoutMs { get; set; } = 5000;

    /// <summary>Locked to VerifyFull post-migration; rejectUnauthorized:false does not survive.</summary>
    public string SslMode { get; set; } = "VerifyFull";

    public string? SslCaCertificatePath { get; set; }
}

public sealed class OperationalDatabaseOptions
{
    public string? ConnectionString { get; set; }

    public string SslMode { get; set; } = "VerifyFull";

    public string? SslCaCertificatePath { get; set; }
}
