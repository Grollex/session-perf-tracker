namespace SessionPerfTracker.Infrastructure.Native;

/// <summary>
/// Tracks the root process and (optionally) every descendant across snapshots.
/// Identity is (pid, create time), so PID reuse never pulls an unrelated process into a session,
/// and orphaned children keep being tracked after a launcher/root exits.
/// </summary>
internal sealed class ProcessTreeTracker : IDisposable
{
    private static readonly TimeSpan DeadAncestorRetention = TimeSpan.FromMinutes(30);
    private readonly bool _includeChildren;
    private readonly Dictionary<int, TrackedProcess> _alive = [];
    private readonly Dictionary<int, DeadProcess> _dead = [];
    private readonly bool _ownsHandles;

    public ProcessTreeTracker(int rootProcessId, long rootCreateTimeTicks, bool includeChildren, bool ownHandles = true)
    {
        RootProcessId = rootProcessId;
        RootCreateTimeTicks = rootCreateTimeTicks;
        _includeChildren = includeChildren;
        _ownsHandles = ownHandles;
        _alive[rootProcessId] = new TrackedProcess(rootProcessId, rootCreateTimeTicks, "root", OpenHandle(rootProcessId));
    }

    public int RootProcessId { get; }
    public long RootCreateTimeTicks { get; private set; }
    public bool RootAlive { get; private set; } = true;
    public int? RootExitCode { get; private set; }
    public DateTimeOffset? RootExitedAt { get; private set; }
    public int? LastExitCode { get; private set; }
    public string? LastExitedProcessName { get; private set; }
    public IReadOnlyCollection<int> AliveProcessIds => _alive.Keys;
    public int AliveCount => _alive.Count;

    public void Update(IReadOnlyDictionary<int, ProcessRawInfo> processes, DateTimeOffset capturedAt)
    {
        // Late-bind the root create time when the session started before the first snapshot.
        if (RootCreateTimeTicks == 0
            && _alive.ContainsKey(RootProcessId)
            && processes.TryGetValue(RootProcessId, out var rootInfo))
        {
            RootCreateTimeTicks = rootInfo.CreateTimeTicks;
            _alive[RootProcessId] = _alive[RootProcessId] with { CreateTimeTicks = rootInfo.CreateTimeTicks, Name = rootInfo.Name };
        }

        foreach (var tracked in _alive.Values.ToArray())
        {
            if (processes.TryGetValue(tracked.ProcessId, out var current)
                && (tracked.CreateTimeTicks == 0 || current.CreateTimeTicks == tracked.CreateTimeTicks))
            {
                if (tracked.Name != current.Name)
                {
                    _alive[tracked.ProcessId] = tracked with { Name = current.Name };
                }

                continue;
            }

            MarkExited(tracked, capturedAt);
        }

        if (_includeChildren)
        {
            AddDescendants(processes, capturedAt);
        }

        foreach (var stale in _dead.Where(item => capturedAt - item.Value.ExitedAt > DeadAncestorRetention).Select(item => item.Key).ToArray())
        {
            _dead.Remove(stale);
        }
    }

    private void AddDescendants(IReadOnlyDictionary<int, ProcessRawInfo> processes, DateTimeOffset capturedAt)
    {
        var added = true;
        var guard = 0;
        while (added && guard++ < 64)
        {
            added = false;
            foreach (var process in processes.Values)
            {
                if (process.ProcessId <= 4 || _alive.ContainsKey(process.ProcessId) || process.ParentProcessId == process.ProcessId)
                {
                    continue;
                }

                if (!IsTrackedParent(process, processes, capturedAt))
                {
                    continue;
                }

                _alive[process.ProcessId] = new TrackedProcess(
                    process.ProcessId,
                    process.CreateTimeTicks,
                    process.Name,
                    OpenHandle(process.ProcessId));
                added = true;
            }
        }
    }

    private bool IsTrackedParent(ProcessRawInfo child, IReadOnlyDictionary<int, ProcessRawInfo> processes, DateTimeOffset capturedAt)
    {
        var parentId = child.ParentProcessId;
        if (_alive.TryGetValue(parentId, out var parent))
        {
            // The live process with this pid must be the one we track, and the child must be younger.
            return processes.TryGetValue(parentId, out var parentInfo)
                && (parent.CreateTimeTicks == 0 || parentInfo.CreateTimeTicks == parent.CreateTimeTicks)
                && (child.CreateTimeTicks == 0 || child.CreateTimeTicks >= parentInfo.CreateTimeTicks);
        }

        if (_dead.TryGetValue(parentId, out var deadParent))
        {
            // Orphan of a tracked process: it must have been created while that parent was alive.
            var childCreated = child.StartedAtUtc;
            return childCreated is not null
                && child.CreateTimeTicks >= deadParent.CreateTimeTicks
                && childCreated.Value <= deadParent.ExitedAt.AddSeconds(5);
        }

        return false;
    }

    private void MarkExited(TrackedProcess tracked, DateTimeOffset capturedAt)
    {
        _alive.Remove(tracked.ProcessId);
        var exitCode = ReadExitCodeAndClose(tracked.Handle);
        _dead[tracked.ProcessId] = new DeadProcess(tracked.CreateTimeTicks, capturedAt);
        LastExitCode = exitCode;
        LastExitedProcessName = tracked.Name;

        if (tracked.ProcessId == RootProcessId && RootAlive)
        {
            RootAlive = false;
            RootExitCode = exitCode;
            RootExitedAt = capturedAt;
        }
    }

    private IntPtr OpenHandle(int processId)
    {
        if (!_ownsHandles || processId <= 4)
        {
            return IntPtr.Zero;
        }

        try
        {
            return NativeMethods.OpenProcess(
                NativeMethods.ProcessQueryLimitedInformation | NativeMethods.Synchronize,
                false,
                processId);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static int? ReadExitCodeAndClose(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return NativeMethods.GetExitCodeProcess(handle, out var code) && code != NativeMethods.StillActive
                ? code
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        foreach (var tracked in _alive.Values)
        {
            if (tracked.Handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(tracked.Handle);
            }
        }

        _alive.Clear();
    }

    private sealed record TrackedProcess(int ProcessId, long CreateTimeTicks, string Name, IntPtr Handle);
    private sealed record DeadProcess(long CreateTimeTicks, DateTimeOffset ExitedAt);
}
