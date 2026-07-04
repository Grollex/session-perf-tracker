namespace SessionPerfTracker.Infrastructure.GlobalWatch;

internal sealed class GlobalProcessPathCache
{
    private const int MaxEntries = 4096;
    private static readonly TimeSpan MaxEntryAge = TimeSpan.FromMinutes(15);
    private readonly object _sync = new();
    private readonly Dictionary<ProcessIdentity, CacheEntry> _paths = [];

    public string? Resolve(
        int processId,
        DateTimeOffset? startedAt,
        string? observedPath,
        DateTimeOffset capturedAt)
    {
        if (!TryCreateIdentity(processId, startedAt, out var identity))
        {
            return NormalizePath(observedPath);
        }

        lock (_sync)
        {
            var normalizedObservedPath = NormalizePath(observedPath);
            if (!string.IsNullOrWhiteSpace(normalizedObservedPath))
            {
                _paths[identity] = new CacheEntry(normalizedObservedPath, capturedAt);
                Prune(capturedAt);
                return normalizedObservedPath;
            }

            if (_paths.TryGetValue(identity, out var cached))
            {
                _paths[identity] = cached with { LastSeenAt = capturedAt };
                return cached.Path;
            }

            Prune(capturedAt);
            return null;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        if (_paths.Count == 0)
        {
            return;
        }

        var oldestAllowed = now - MaxEntryAge;
        foreach (var stale in _paths
                     .Where(item => item.Value.LastSeenAt < oldestAllowed)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _paths.Remove(stale);
        }

        if (_paths.Count <= MaxEntries)
        {
            return;
        }

        foreach (var extra in _paths
                     .OrderByDescending(item => item.Value.LastSeenAt)
                     .Skip(MaxEntries)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _paths.Remove(extra);
        }
    }

    private static bool TryCreateIdentity(
        int processId,
        DateTimeOffset? startedAt,
        out ProcessIdentity identity)
    {
        if (processId <= 0 || startedAt is null)
        {
            identity = default;
            return false;
        }

        identity = new ProcessIdentity(processId, startedAt.Value.ToUniversalTime().Ticks);
        return true;
    }

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : path.Trim();

    private readonly record struct ProcessIdentity(int ProcessId, long StartedAtUtcTicks);
    private sealed record CacheEntry(string Path, DateTimeOffset LastSeenAt);
}
