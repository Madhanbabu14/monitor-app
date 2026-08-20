namespace Monitor.Data.Repositories;

/// <summary>
/// Wraps a query against the optional, read-only operational (production
/// replica) database. The source had no analogue for this — it never had a
/// documented operational connection, let alone a typed "is it up" signal —
/// so callers (Monitor.Operations' S3&lt;-&gt;log reconciliation) must branch on
/// <see cref="Available"/> and degrade gracefully (e.g. the "Not Processed"
/// band) instead of assuming the query always succeeds.
/// </summary>
public sealed class OperationalResult<T>
{
    public bool Available { get; }

    public T? Value { get; }

    private OperationalResult(bool available, T? value)
    {
        Available = available;
        Value = value;
    }

    public static OperationalResult<T> Unavailable() => new(false, default);

    public static OperationalResult<T> Ok(T value) => new(true, value);
}
