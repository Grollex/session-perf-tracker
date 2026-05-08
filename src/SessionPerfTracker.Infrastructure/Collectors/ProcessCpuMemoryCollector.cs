using System.Diagnostics;
using System.Runtime.InteropServices;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Infrastructure.Targeting;

namespace SessionPerfTracker.Infrastructure.Collectors;

public sealed class ProcessCpuMemoryCollector : IMetricCollector, IRamAccountingDiagnosticProvider
{
    private readonly Dictionary<string, Dictionary<int, CollectorState>> _states = new(StringComparer.Ordinal);

    public string Id => "process-cpu-memory-disk-collector";
    public string Label => "Process CPU/RAM/Disk collector";

    public Task<MetricCapabilities> GetCapabilitiesAsync(TargetDescriptor target, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MetricCapabilities
        {
            CpuPercent = MetricReliability.Stable,
            MemoryMb = MetricReliability.Stable,
            GpuPercent = MetricReliability.Unavailable,
            DiskReadMbPerSec = MetricReliability.BestEffort,
            DiskWriteMbPerSec = MetricReliability.BestEffort,
            TemperatureC = MetricReliability.Unavailable
        });

    public Task<MetricSample> CollectAsync(
        TargetDescriptor target,
        string sessionId,
        long elapsedMs,
        CancellationToken cancellationToken = default)
    {
        if (target.ProcessId is null)
        {
            throw new InvalidOperationException("CPU/RAM collection requires a process id.");
        }

        var rootProcessId = target.ProcessId.Value;
        using var rootProcess = Process.GetProcessById(rootProcessId);
        if (rootProcess.HasExited)
        {
            throw new InvalidOperationException("Target process has exited.");
        }

        var now = DateTimeOffset.UtcNow;
        var processIds = GetTrackedProcessIds(target, rootProcessId);
        var state = GetState(sessionId);
        var observedProcessIds = new HashSet<int>();
        var processorRatio = 0d;
        var memoryMb = 0d;
        var diskReadMbPerSec = 0d;
        var diskWriteMbPerSec = 0d;
        var processCount = 0;

        foreach (var processId in processIds)
        {
            using var process = TryGetProcess(processId);
            if (process is null || process.HasExited)
            {
                continue;
            }

            var totalProcessorTime = process.TotalProcessorTime;
            var ioCounters = WindowsProcessIoCounters.TryGet(process);
            memoryMb += WindowsProcessMemoryCounters.GetRamForAggregation(process).Megabytes;
            processCount++;
            observedProcessIds.Add(processId);

            if (state.TryGetValue(processId, out var previous))
            {
                var wallMs = Math.Max(1, (now - previous.Timestamp).TotalMilliseconds);
                var delta = Math.Max(0, (totalProcessorTime - previous.TotalProcessorTime).TotalMilliseconds);
                processorRatio += delta / wallMs;

                if (ioCounters is not null && previous.IoCounters is not null)
                {
                    var seconds = wallMs / 1000d;
                    diskReadMbPerSec += BytesToMegabytesPerSecond(
                        ioCounters.ReadTransferCount,
                        previous.IoCounters.ReadTransferCount,
                        seconds);
                    diskWriteMbPerSec += BytesToMegabytesPerSecond(
                        ioCounters.WriteTransferCount,
                        previous.IoCounters.WriteTransferCount,
                        seconds);
                }
            }

            state[processId] = new CollectorState(now, totalProcessorTime, ioCounters);
        }

        foreach (var staleProcessId in state.Keys.Where(processId => !observedProcessIds.Contains(processId)).ToArray())
        {
            state.Remove(staleProcessId);
        }

        var cpuPercent = Math.Clamp(processorRatio / Environment.ProcessorCount * 100, 0, 100);

        return Task.FromResult(new MetricSample
        {
            Id = $"{sessionId}_sample_{elapsedMs}",
            SessionId = sessionId,
            Timestamp = now,
            ElapsedMs = elapsedMs,
            RootProcessId = rootProcessId,
            ProcessCount = processCount,
            Values = new Dictionary<MetricKey, double>
            {
                [MetricKey.CpuPercent] = cpuPercent,
                [MetricKey.MemoryMb] = memoryMb,
                [MetricKey.DiskReadMbPerSec] = diskReadMbPerSec,
                [MetricKey.DiskWriteMbPerSec] = diskWriteMbPerSec
            },
            SourceReliability = new Dictionary<MetricKey, MetricReliability>
            {
                [MetricKey.CpuPercent] = MetricReliability.Stable,
                [MetricKey.MemoryMb] = MetricReliability.Stable,
                [MetricKey.GpuPercent] = MetricReliability.Unavailable,
                [MetricKey.DiskReadMbPerSec] = MetricReliability.BestEffort,
                [MetricKey.DiskWriteMbPerSec] = MetricReliability.BestEffort,
                [MetricKey.TemperatureC] = MetricReliability.Unavailable
            }
        });
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
        var parentByProcess = WindowsProcessTreeSnapshot.GetParentProcessMap();
        var processIds = GetTrackedProcessIds(target, rootProcessId);
        var rows = new List<RamAccountingProcessSnapshot>();

        foreach (var processId in processIds)
        {
            using var process = TryGetProcess(processId);
            if (process is null || process.HasExited)
            {
                continue;
            }

            var memory = WindowsProcessMemoryCounters.GetRamForAggregation(process);
            rows.Add(new RamAccountingProcessSnapshot
            {
                ProcessId = processId,
                ProcessName = SafeGetProcessName(process),
                ParentProcessId = parentByProcess.TryGetValue(processId, out var parentProcessId) ? parentProcessId : null,
                IsRoot = processId == rootProcessId,
                MemoryMb = memory.Megabytes,
                MemoryMetricName = memory.MetricName
            });
        }

        var root = rows.FirstOrDefault(row => row.IsRoot);

        return Task.FromResult(new RamAccountingDiagnosticSnapshot
        {
            RootProcessId = rootProcessId,
            RootProcessName = root?.ProcessName ?? $"Process {rootProcessId}",
            RootParentProcessId = parentByProcess.TryGetValue(rootProcessId, out var rootParentProcessId) ? rootParentProcessId : null,
            IncludeChildProcesses = target.IncludeChildProcesses,
            MemoryMetricName = rows.Select(row => row.MemoryMetricName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                ? rows.FirstOrDefault()?.MemoryMetricName ?? WindowsProcessMemoryCounters.PreferredMetricName
                : "Mixed",
            AggregatedMemoryMb = rows.Sum(row => row.MemoryMb),
            Processes = rows
                .OrderByDescending(row => row.IsRoot)
                .ThenBy(row => row.ParentProcessId)
                .ThenBy(row => row.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.ProcessId)
                .ToArray()
        });
    }

    private Dictionary<int, CollectorState> GetState(string sessionId)
    {
        if (!_states.TryGetValue(sessionId, out var state))
        {
            state = [];
            _states[sessionId] = state;
        }

        return state;
    }

    private static IReadOnlyList<int> GetTrackedProcessIds(TargetDescriptor target, int rootProcessId)
    {
        if (!target.IncludeChildProcesses)
        {
            return [rootProcessId];
        }

        return WindowsProcessTreeSnapshot.GetIncludedProcessIds(rootProcessId, includeChildProcesses: true);
    }

    private static Process? TryGetProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string SafeGetProcessName(Process process)
    {
        try
        {
            return string.IsNullOrWhiteSpace(process.ProcessName) ? $"Process {process.Id}" : process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            return $"Process {process.Id}";
        }
    }

    private static double BytesToMegabytesPerSecond(ulong current, ulong previous, double seconds)
    {
        if (current < previous || seconds <= 0)
        {
            return 0;
        }

        return (current - previous) / 1024d / 1024d / seconds;
    }

    private sealed record CollectorState(
        DateTimeOffset Timestamp,
        TimeSpan TotalProcessorTime,
        WindowsProcessIoCounters.Snapshot? IoCounters);

    private static class WindowsProcessIoCounters
    {
        public static Snapshot? TryGet(Process process)
        {
            try
            {
                return GetProcessIoCounters(process.Handle, out var counters)
                    ? new Snapshot(counters.ReadTransferCount, counters.WriteTransferCount)
                    : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters ioCounters);

        public sealed record Snapshot(ulong ReadTransferCount, ulong WriteTransferCount);

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }
    }
}
