using SessionPerfTracker.Infrastructure.GlobalWatch;

namespace SessionPerfTracker.UnitTests;

public sealed class GlobalProcessPathCacheTests
{
    [Fact]
    public void Resolve_returns_cached_path_for_same_process_identity()
    {
        var cache = new GlobalProcessPathCache();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var capturedAt = DateTimeOffset.UtcNow;

        var observed = cache.Resolve(1234, startedAt, @" C:\Apps\tracked.exe ", capturedAt);
        var recovered = cache.Resolve(1234, startedAt, null, capturedAt.AddSeconds(3));

        Assert.Equal(@"C:\Apps\tracked.exe", observed);
        Assert.Equal(@"C:\Apps\tracked.exe", recovered);
    }

    [Fact]
    public void Resolve_does_not_reuse_path_after_pid_reuse()
    {
        var cache = new GlobalProcessPathCache();
        var capturedAt = DateTimeOffset.UtcNow;

        _ = cache.Resolve(1234, capturedAt.AddMinutes(-5), @"C:\Apps\old.exe", capturedAt);
        var recovered = cache.Resolve(1234, capturedAt.AddMinutes(-1), null, capturedAt.AddSeconds(3));

        Assert.Null(recovered);
    }

    [Fact]
    public void Resolve_does_not_cache_when_start_time_is_unavailable()
    {
        var cache = new GlobalProcessPathCache();
        var capturedAt = DateTimeOffset.UtcNow;

        _ = cache.Resolve(1234, null, @"C:\Apps\unknown-start.exe", capturedAt);
        var recovered = cache.Resolve(1234, null, null, capturedAt.AddSeconds(3));

        Assert.Null(recovered);
    }
}
