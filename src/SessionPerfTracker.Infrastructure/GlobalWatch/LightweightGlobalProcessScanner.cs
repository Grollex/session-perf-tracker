using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Infrastructure.Gpu;
using SessionPerfTracker.Infrastructure.Identity;
using SessionPerfTracker.Infrastructure.Native;
using SessionPerfTracker.Infrastructure.SelfMonitoring;

namespace SessionPerfTracker.Infrastructure.GlobalWatch;

/// <summary>
/// Whole-system process scan built on one NtQuerySystemInformation call per scan.
/// Paths and file metadata are resolved once per process instance and cached, so steady-state
/// scans cost a few milliseconds even with hundreds of processes.
/// </summary>
public sealed class LightweightGlobalProcessScanner : IGlobalProcessScanner
{
    private static readonly TimeSpan RecentProcessWindow = TimeSpan.FromSeconds(60);
    private readonly object _sync = new();
    private readonly SystemProcessSnapshotReader _reader;
    private readonly ExecutableIdentityProvider _identity;
    private readonly SystemLoadProvider _systemLoad;
    private readonly GpuMonitor _gpu;
    private readonly GlobalProcessPathCache _pathCache = new();
    private readonly Func<bool> _captureGpu;
    private SystemProcessSnapshot? _previous;

    public LightweightGlobalProcessScanner(Func<bool>? captureGpu = null)
        : this(SystemProcessSnapshotReader.Shared, ExecutableIdentityProvider.Shared, GpuMonitor.Shared, captureGpu)
    {
    }

    internal LightweightGlobalProcessScanner(
        SystemProcessSnapshotReader reader,
        ExecutableIdentityProvider identity,
        GpuMonitor gpu,
        Func<bool>? captureGpu)
    {
        _reader = reader;
        _identity = identity;
        _gpu = gpu;
        _captureGpu = captureGpu ?? (() => true);
        _systemLoad = new SystemLoadProvider(gpu, captureGpu: true);
    }

    public Task<GlobalProcessScan> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private GlobalProcessScan Scan(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = _reader.Capture();
            var previous = _previous;
            _previous = current;
            var windows = WindowSnapshot.Capture();
            var gpu = _captureGpu() ? _gpu.GetSample(TimeSpan.FromMilliseconds(1500)) : GpuSample.Unavailable;
            var elapsed = previous is null ? 0 : SystemProcessSnapshot.ElapsedSeconds(previous, current);
            var children = current.ChildrenByParent;
            var snapshots = new List<GlobalProcessSnapshot>(current.Processes.Count);

            foreach (var process in current.Processes.Values)
            {
                if (process.ProcessId == 0)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                ProcessRawInfo previousInfo = default;
                var hasPrevious = previous is not null
                    && previous.TryGet(process.ProcessId, out previousInfo)
                    && previousInfo.CreateTimeTicks == process.CreateTimeTicks;
                var startedAt = process.StartedAtUtc;
                var fullPath = ResolvePath(process, startedAt, current.CapturedAt);
                var identity = fullPath is null ? null : _identity.DescribeCached(fullPath);
                gpu.Processes.TryGetValue(process.ProcessId, out var gpuUsage);
                var parentName = current.TryGet(process.ParentProcessId, out var parent)
                    && parent.CreateTimeTicks <= process.CreateTimeTicks
                    ? parent.Name
                    : null;

                snapshots.Add(new GlobalProcessSnapshot
                {
                    ProcessId = process.ProcessId,
                    ParentProcessId = parentName is null ? null : process.ParentProcessId,
                    ParentProcessName = parentName,
                    DescendantProcessCount = CountDescendants(process.ProcessId, children),
                    Name = StripExe(process.Name),
                    WindowTitle = windows.GetTitle(process.ProcessId),
                    FullPath = fullPath,
                    FileDescription = identity?.FileDescription,
                    ProductName = identity?.ProductName,
                    CompanyName = identity?.CompanyName,
                    SignerStatus = identity?.SignerStatus,
                    Version = identity?.FileVersion,
                    OriginalFileName = identity?.OriginalFileName,
                    StartedAt = startedAt,
                    IsNewSincePreviousScan = previous is not null && !hasPrevious,
                    StartedRecently = startedAt is not null && current.CapturedAt - startedAt.Value <= RecentProcessWindow,
                    CpuPercent = hasPrevious
                        ? SystemProcessSnapshot.CpuPercent(previousInfo.CpuTime100Ns, process.CpuTime100Ns, elapsed)
                        : null,
                    MemoryMb = process.PrivateWorkingSetMb,
                    MemoryDeltaMb = hasPrevious
                        ? process.PrivateWorkingSetMb - previousInfo.PrivateWorkingSetMb
                        : null,
                    DiskReadMbPerSec = hasPrevious
                        ? SystemProcessSnapshot.MegabytesPerSecond(previousInfo.ReadTransferBytes, process.ReadTransferBytes, elapsed)
                        : null,
                    DiskWriteMbPerSec = hasPrevious
                        ? SystemProcessSnapshot.MegabytesPerSecond(previousInfo.WriteTransferBytes, process.WriteTransferBytes, elapsed)
                        : null,
                    GpuPercent = gpu.HasUtilization ? gpuUsage?.UtilizationPercent ?? 0 : null,
                    GpuDedicatedMb = gpu.IsAvailable ? gpuUsage?.DedicatedMb ?? 0 : null
                });
            }

            var ordered = snapshots
                .OrderByDescending(process => process.CpuPercent ?? 0)
                .ThenByDescending(process => process.MemoryMb ?? 0)
                .ToArray();

            return new GlobalProcessScan(
                current.CapturedAt,
                previous is null ? null : current.CapturedAt - previous.CapturedAt,
                ordered)
            {
                System = _systemLoad.Sample(ordered.Length)
            };
        }
    }

    private string? ResolvePath(ProcessRawInfo process, DateTimeOffset? startedAt, DateTimeOffset capturedAt)
    {
        if (_pathCache.TryGet(process.ProcessId, startedAt, capturedAt, out var cached))
        {
            return cached;
        }

        return _pathCache.Resolve(process.ProcessId, startedAt, ProcessImagePath.TryQuery(process.ProcessId), capturedAt);
    }

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static int CountDescendants(int processId, IReadOnlyDictionary<int, List<int>> childrenByParentId)
    {
        if (!childrenByParentId.TryGetValue(processId, out var children) || children.Count == 0)
        {
            return 0;
        }

        var count = 0;
        var visited = new HashSet<int> { processId };
        var stack = new Stack<int>(children);
        while (stack.Count > 0)
        {
            var childId = stack.Pop();
            if (!visited.Add(childId))
            {
                continue;
            }

            count++;
            if (childrenByParentId.TryGetValue(childId, out var grandchildren))
            {
                foreach (var grandchildId in grandchildren)
                {
                    stack.Push(grandchildId);
                }
            }
        }

        return count;
    }
}
