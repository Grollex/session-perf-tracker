namespace SessionPerfTracker.Infrastructure.GlobalWatch;

/// <summary>
/// Executable paths keyed by process identity (pid + start time). A path never changes for a
/// running process, so it is resolved once; failed lookups are retried only occasionally.
/// </summary>
internal sealed class GlobalProcessPathCache
{
    private const int MaxEntries = 4096;
    private static readonly TimeSpan MaxEntryAge = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FailedLookupRetry = TimeSpan.FromSeconds(60);
    private readonly object _sync = new();
    private readonly Dictionary<ProcessIdentity, CacheEntry> _paths = [];

    public bool TryGet(int processId, DateTimeOffset? startedAt, DateTimeOffset capturedAt, out string? path)
    {
        path = null;
        if (!TryCreateIdentity(processId, startedAt, out var identity))
        {
            return false;
        }

        lock (_sync)
        {
            if (!_paths.TryGetValue(identity, out var cached))
            {
                return false;
            }

            if (cached.Path is null && capturedAt - cached.FirstSeenAt >= FailedLookupRetry)
            {
                return false;
            }

            _paths[identity] = cached with { LastSeenAt = capturedAt };
            path = cached.Path;
            return true;
        }
    }

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
                _paths[identity] = new CacheEntry(normalizedObservedPath, capturedAt, capturedAt);
                Prune(capturedAt);
                return normalizedObservedPath;
            }

            if (_paths.TryGetValue(identity, out var cached))
            {
                _paths[identity] = cached.Path is null
                    ? cached with { LastSeenAt = capturedAt, FirstSeenAt = capturedAt }
                    : cached with { LastSeenAt = capturedAt };
                return cached.Path;
            }

            // Remember the failure so protected processes are not re-opened on every scan.
            _paths[identity] = new CacheEntry(null, capturedAt, capturedAt);
            Prune(capturedAt);
            return null;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        if (_paths.Count < MaxEntries / 2)
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
    private sealed record CacheEntry(string? Path, DateTimeOffset LastSeenAt, DateTimeOffset FirstSeenAt);
}
