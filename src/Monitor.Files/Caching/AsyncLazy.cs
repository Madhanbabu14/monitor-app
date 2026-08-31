namespace Monitor.Files.Caching;

/// <summary>
/// Per the Architect's brief: "the 60s cache + inflight de-duplication becomes
/// IMemoryCache holding AsyncLazy&lt;T&gt; so concurrent misses share one S3
/// round-trip." Stored (not the raw Task) in <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>
/// so every caller awaiting the same cache entry awaits the exact same
/// in-flight Task, replacing the source's module-level <c>rawS3Inflight</c> promise.
/// </summary>
public sealed class AsyncLazy<T> : Lazy<Task<T>>
{
    public AsyncLazy(Func<Task<T>> taskFactory)
        : base(() => taskFactory())
    {
    }
}
