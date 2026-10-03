using System.Collections.Concurrent;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Infrastructure.Gpu;
using SessionPerfTracker.Infrastructure.Native;

namespace SessionPerfTracker.Infrastructure.Collectors;

/// <summary>
/// Session collector: one system snapshot per sample gives CPU, private RAM and I/O for the whole
/// tracked tree (no per-process handles), plus vendor-neutral GPU usage from the shared GPU monitor.
/// </summary>
public sealed class ProcessCpuMemoryCollector : IMetricCollector, IRamAccountingDiagnosticProvider
{
    private const string MemoryMetricName = "Private Working Set";
    private readonly ConcurrentDictionary<string, SessionState> _states = new(StringComparer.Ordinal);
    private readonly SystemProcessSnapshotReader _reader;
    private readonly GpuMonitor _gpu;

    public ProcessCpuMemoryCollector()
        : this(SystemProcessSnapshotReader.Shared, GpuMonitor.Shared)
    {
    }

    internal ProcessCpuMemoryCollector(SystemProcessSnapshotReader reader, GpuMonitor gpu)
    {
        _reader = reader;
        _gpu = gpu;
    }

    public string Id => "process-cpu-memory-disk-gpu-collector";
    public string Label => "Process CPU/RAM/Disk/GPU collector";

    public Task<MetricCapabilities> GetCapabilitiesAsync(TargetDescriptor target, CancellationToken cancellationToken = default)
    {
        var gpu = _gpu.IsAvailable ? MetricReliability.BestEffort : MetricReliability.Unavailable;
        return Task.FromResult(new MetricCapabilities
        {
            CpuPercent = MetricReliability.Stable,
            MemoryMb = MetricReliability.Stable,
            GpuPercent = gpu,
            GpuMemoryMb = gpu,
            DiskReadMbPerSec = MetricReliability.BestEffort,
            DiskWriteMbPerSec = MetricReliability.BestEffort,
            TemperatureC = gpu
        });
    }

    public void RegisterSession(string sessionId, TargetDescriptor target, SamplingSettings sampling)
    {
        if (target.ProcessId is not int rootProcessId)
        {
            return;
        }

        var snapshot = _reader.Capture();
        var createTime = snapshot.TryGet(rootProcessId, out var root) ? root.CreateTimeTicks : 0;
        var state = new SessionState(
            new ProcessTreeTracker(rootProcessId, createTime, target.IncludeChildProcesses),
            sampling.CaptureGpu,
            Math.Max(500, sampling.IntervalMs));
        if (_states.TryRemove(sessionId, out var previous))
        {
            previous.Dispose();
        }

        _states[sessionId] = state;
    }

    public Task<MetricSample> CollectAsync(
        TargetDescriptor target,
        string sessionId,
        long elapsedMs,
        CancellationToken cancellationToken = default)
    {
        if (target.ProcessId is null)
        {
            throw new InvalidOperationException("Process collection requires a process id.");
        }

        if (!_states.ContainsKey(sessionId))
        {
            RegisterSession(sessionId, target, new SamplingSettings());
        }

        var state = _states[sessionId];
        var snapshot = _reader.Capture();
        lock (state)
        {
            state.Tracker.Update(snapshot.Processes, snapshot.CapturedAt);
            var alive = state.Tracker.AliveProcessIds.ToArray();
            var previous = state.Previous;
            var elapsed = previous is null ? 0 : SystemProcessSnapshot.ElapsedSeconds(previous, snapshot);
            var cpuPercent = 0d;
            var memoryMb = 0d;
            var diskRead = 0d;
            var diskWrite = 0d;

            foreach (var processId in alive)
            {
                if (!snapshot.TryGet(processId, out var current))
                {
                    continue;
                }

                memoryMb += current.PrivateWorkingSetMb;
                if (previous is not null
                    && previous.TryGet(processId, out var before)
                    && before.CreateTimeTicks == current.CreateTimeTicks)
                {
                    cpuPercent += SystemProcessSnapshot.CpuPercent(before.CpuTime100Ns, current.CpuTime100Ns, elapsed);
                    diskRead += SystemProcessSnapshot.MegabytesPerSecond(before.ReadTransferBytes, current.ReadTransferBytes, elapsed);
                    diskWrite += SystemProcessSnapshot.MegabytesPerSecond(before.WriteTransferBytes, current.WriteTransferBytes, elapsed);
                }
            }

            state.Previous = snapshot;
            var values = new Dictionary<MetricKey, double>
            {
                [MetricKey.CpuPercent] = Math.Clamp(cpuPercent, 0, 100),
                [MetricKey.MemoryMb] = memoryMb,
                [MetricKey.DiskReadMbPerSec] = diskRead,
                [MetricKey.DiskWriteMbPerSec] = diskWrite
            };
            var reliability = new Dictionary<MetricKey, MetricReliability>
            {
                [MetricKey.CpuPercent] = MetricReliability.Stable,
                [MetricKey.MemoryMb] = MetricReliability.Stable,
                [MetricKey.DiskReadMbPerSec] = MetricReliability.BestEffort,
                [MetricKey.DiskWriteMbPerSec] = MetricReliability.BestEffort
            };

            if (state.CaptureGpu && alive.Length > 0)
            {
                AddGpuValues(state, alive, values, reliability);
            }

            UpdateTreeStatus(state, alive, snapshot);

            return Task.FromResult(new MetricSample
            {
                Id = $"{sessionId}_sample_{elapsedMs}",
                SessionId = sessionId,
                Timestamp = snapshot.CapturedAt,
                ElapsedMs = elapsedMs,
                RootProcessId = state.Tracker.RootProcessId,
                ProcessCount = alive.Length,
                Values = values,
                SourceReliability = reliability
            });
        }
    }

    public ProcessTreeStatus? GetTreeStatus(string sessionId) =>
        _states.TryGetValue(sessionId, out var state) ? state.Status : null;

    public void ReleaseSession(string sessionId)
    {
        if (_states.TryRemove(sessionId, out var state))
        {
            state.Dispose();
        }
    }

    public Task<RamAccountingDiagnosticSnapshot> CaptureAsync(
        TargetDescriptor target,
        CancellationToken cancellationToken = default)
    {
        if (target.ProcessId is null)
        {
            throw new InvalidOperationException("RAM diagnostic requires a process id.");
        }

        var rootProcessId = target.ProcessId.Value;
        var snapshot = _reader.Capture();
        var createTime = snapshot.TryGet(rootProcessId, out var root) ? root.CreateTimeTicks : 0;
        using var tracker = new ProcessTreeTracker(rootProcessId, createTime, target.IncludeChildProcesses, ownHandles: false);
        tracker.Update(snapshot.Processes, snapshot.CapturedAt);

        var rows = tracker.AliveProcessIds
            .Where(processId => snapshot.TryGet(processId, out _))
            .Select(processId =>
            {
                snapshot.TryGet(processId, out var process);
                return new RamAccountingProcessSnapshot
                {
                    ProcessId = processId,
                    ProcessName = process.Name,
                    ParentProcessId = process.ParentProcessId,
                    IsRoot = processId == rootProcessId,
                    MemoryMb = process.PrivateWorkingSetMb,
                    MemoryMetricName = MemoryMetricName
                };
            })
            .OrderByDescending(row => row.IsRoot)
            .ThenBy(row => row.ParentProcessId)
            .ThenBy(row => row.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ProcessId)
            .ToArray();

        return Task.FromResult(new RamAccountingDiagnosticSnapshot
        {
            RootProcessId = rootProcessId,
            RootProcessName = rows.FirstOrDefault(row => row.IsRoot)?.ProcessName ?? $"Process {rootProcessId}",
            RootParentProcessId = root.ProcessId == rootProcessId ? root.ParentProcessId : null,
            IncludeChildProcesses = target.IncludeChildProcesses,
            MemoryMetricName = MemoryMetricName,
            AggregatedMemoryMb = rows.Sum(row => row.MemoryMb),
            Processes = rows
        });
    }

    private void AddGpuValues(
        SessionState state,
        IReadOnlyCollection<int> alive,
        Dictionary<MetricKey, double> values,
        Dictionary<MetricKey, MetricReliability> reliability)
    {
        var maxAge = TimeSpan.FromMilliseconds(Math.Clamp(state.IntervalMs * 0.8, 400, 1500));
        var gpu = _gpu.GetSample(maxAge);
        if (!gpu.IsAvailable || gpu.Aggregate(alive) is not { } usage)
        {
            return;
        }

        if (gpu.HasUtilization)
        {
            values[MetricKey.GpuPercent] = usage.UtilizationPercent;
            reliability[MetricKey.GpuPercent] = MetricReliability.BestEffort;
        }

        values[MetricKey.GpuMemoryMb] = usage.DedicatedMb;
        reliability[MetricKey.GpuMemoryMb] = MetricReliability.BestEffort;

        if (gpu.PrimaryAdapter?.TemperatureC is double temperature)
        {
            values[MetricKey.TemperatureC] = temperature;
            reliability[MetricKey.TemperatureC] = MetricReliability.BestEffort;
        }
    }

    private static void UpdateTreeStatus(SessionState state, IReadOnlyList<int> alive, SystemProcessSnapshot snapshot)
    {
        var tracker = state.Tracker;
        var hasWindow = false;
        var responding = true;
        string? windowProcessName = null;

        if (alive.Count > 0)
        {
            var windows = WindowSnapshot.Capture();
            var candidates = alive.OrderByDescending(processId => processId == tracker.RootProcessId);
            foreach (var processId in candidates)
            {
                if (windows.IsResponding(processId) is not bool isResponding)
                {
                    continue;
                }

                hasWindow = true;
                responding = isResponding;
                windowProcessName = snapshot.TryGet(processId, out var info) ? info.Name : null;
                break;
            }
        }

        state.Status = new ProcessTreeStatus
        {
            RootAlive = tracker.RootAlive,
            AliveCount = alive.Count,
            RootExitCode = tracker.RootExitCode,
            RootExitedAt = tracker.RootExitedAt,
            LastExitCode = tracker.LastExitCode,
            LastExitedProcessName = tracker.LastExitedProcessName,
            HasWindow = hasWindow,
            IsResponding = responding,
            WindowProcessName = windowProcessName
        };
    }

    private sealed class SessionState(ProcessTreeTracker tracker, bool captureGpu, int intervalMs) : IDisposable
    {
        public ProcessTreeTracker Tracker { get; } = tracker;
        public bool CaptureGpu { get; } = captureGpu;
        public int IntervalMs { get; } = intervalMs;
        public SystemProcessSnapshot? Previous { get; set; }
        public ProcessTreeStatus? Status { get; set; }

        public void Dispose() => Tracker.Dispose();
    }
}
