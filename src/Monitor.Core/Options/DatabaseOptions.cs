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
/// <remarks>
/// .NET 6's <c>OptionsBuilder.ValidateDataAnnotations()</c> only evaluates
/// attributes declared directly on this class's own properties (e.g. the
/// <see cref="Required"/> on <see cref="Primary"/> itself, which just checks
/// for null) — it does NOT recurse into <see cref="PrimaryDatabaseOptions"/>'s
/// own attributes. <see cref="IValidatableObject"/> is implemented here to
/// explicitly re-run attribute validation against the nested objects so a
/// missing/invalid <see cref="PrimaryDatabaseOptions.ConnectionString"/> still
/// fails fast at boot, matching the source's <c>required('DATABASE_URL')</c>.
/// </remarks>
public sealed class DatabaseOptions : IValidatableObject
{
    public const string SectionName = "Database";

    [Required]
    public PrimaryDatabaseOptions Primary { get; set; } = new();

    /// <summary>Null/unset => operational (production replica) source is not registered.</summary>
    public OperationalDatabaseOptions? Operational { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in ValidateNested(Primary, nameof(Primary)))
        {
            yield return result;
        }

        if (Operational is not null)
        {
            foreach (var result in ValidateNested(Operational, nameof(Operational)))
            {
                yield return result;
            }
        }
    }

    private static IEnumerable<ValidationResult> ValidateNested(object instance, string memberPrefix)
    {
        var context = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, context, results, validateAllProperties: true);

        foreach (var result in results)
        {
            var memberNames = result.MemberNames.Any()
                ? result.MemberNames.Select(m => $"{memberPrefix}.{m}")
                : new[] { memberPrefix };
            yield return new ValidationResult(result.ErrorMessage, memberNames);
        }
    }
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
