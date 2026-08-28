namespace Monitor.Files;

/// <summary>
/// Backs the raw-S3-listing cache entry (see <see cref="FilesService"/>). Wrapping the
/// factory in <see cref="Lazy{T}"/> guarantees the underlying <see cref="Task{T}"/> is
/// created exactly once even when several requests race to populate the same
/// <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/> entry concurrently —
/// the .NET analogue of the source's <c>rawS3Inflight</c> promise de-duplication
/// (only one <c>ListObjectsV2</c> round-trip per cold cache, no matter how many
/// concurrent callers miss at once).
/// </summary>
internal sealed class AsyncLazy<T>
{
    private readonly Lazy<Task<T>> _lazy;

    public AsyncLazy(Func<Task<T>> factory)
    {
        _lazy = new Lazy<Task<T>>(factory);
    }

    public Task<T> Value => _lazy.Value;
}
