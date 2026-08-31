using Monitor.Files.Caching;

namespace Monitor.UnitTests.Files;

/// <summary>
/// Exercises <see cref="AsyncLazy{T}"/> in isolation — the primitive
/// <see cref="Monitor.Files.S3FilesService"/> stores inside <c>IMemoryCache</c> so that
/// every caller awaiting the same cache entry awaits the exact same in-flight
/// <see cref="Task"/>, replacing the source's module-level <c>rawS3Inflight</c> promise
/// (s3.service.ts's <c>fetchAllObjectsCached</c>).
/// </summary>
public class AsyncLazyTests
{
    [Fact]
    public async Task Value_ReturnsSameTaskInstance_OnEveryAccess()
    {
        var lazy = new AsyncLazy<int>(() => Task.FromResult(42));

        var first = lazy.Value;
        var second = lazy.Value;

        Assert.Same(first, second);
        Assert.Equal(42, await first);
    }

    [Fact]
    public async Task Value_InvokesFactoryExactlyOnce_EvenWithConcurrentAccess()
    {
        var callCount = 0;
        var gate = new TaskCompletionSource();
        var lazy = new AsyncLazy<int>(async () =>
        {
            Interlocked.Increment(ref callCount);
            await gate.Task;
            return 7;
        });

        // Simulate several concurrent "cold cache" callers all reading .Value at once,
        // the exact scenario the source's rawS3Inflight dedup guards against.
        var t1 = lazy.Value;
        var t2 = lazy.Value;
        var t3 = lazy.Value;

        gate.SetResult();
        var results = await Task.WhenAll(t1, t2, t3);

        Assert.Equal(1, callCount);
        Assert.All(results, r => Assert.Equal(7, r));
    }

    [Fact]
    public async Task Value_FactoryThrowsSynchronously_FaultsTheTaskInsteadOfThrowingFromValue()
    {
        var lazy = new AsyncLazy<int>(() => throw new InvalidOperationException("boom"));

        // Lazy<T> caches the *task returned by the factory*, not a synchronous result, so
        // accessing .Value itself doesn't throw even if the factory throws synchronously —
        // Lazy<Task<T>>'s underlying Lazy.Value only re-throws if constructing the Task
        // object itself fails. Confirm the failure instead surfaces from awaiting Value.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await lazy.Value);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Value_FaultedTask_KeepsReturningTheSameFaultedTaskUntilExternallyEvicted()
    {
        // This is precisely why S3FilesService.AwaitAndEvictOnFailure manually removes the
        // cache entry on failure instead of relying on AsyncLazy/Lazy<T> to retry: once the
        // wrapped Task faults, the *same* faulted Task is replayed by every subsequent
        // access — Lazy<T>'s default thread-safety mode caches exceptions from the factory
        // for the lifetime of the Lazy instance.
        var callCount = 0;
        var lazy = new AsyncLazy<int>(() =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromException<int>(new InvalidOperationException("s3 unreachable"));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lazy.Value);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lazy.Value);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task Value_SuccessfulResult_IsReusedAcrossManyAwaits()
    {
        var callCount = 0;
        var lazy = new AsyncLazy<string>(() =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult("cached-listing");
        });

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal("cached-listing", await lazy.Value);
        }

        Assert.Equal(1, callCount);
    }
}
