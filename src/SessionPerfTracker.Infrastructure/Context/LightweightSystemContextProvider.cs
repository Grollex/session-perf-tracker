using System.Diagnostics;
using System.Runtime.InteropServices;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Infrastructure.Collectors;

namespace SessionPerfTracker.Infrastructure.Context;

public sealed class LightweightSystemContextProvider : ISpikeContextProvider
{
    private const int ProbeDelayMs = 250;
    private const int TopProcessCount = 6;

    public async Task<SpikeContextSnapshot?> CaptureAsync(
        SpikeContextInput input,
        CancellationToken cancellationToken = default)
    {
        var first = CaptureProcessProbe();
        await Task.Delay(ProbeDelayMs, cancellationToken);
        var second = CaptureProcessProbe();

        if (second.Count == 0)
        {
            return null;
        }

        var firstByProcessId = first.ToDictionary(process => process.ProcessId);
        var processes = second
            .Select(process => BuildContextProcess(process, firstByProcessId.GetValueOrDefault(process.ProcessId)))
            .ToArray();

        var capturedAt = DateTimeOffset.UtcNow;
        var newProcesses = second
            .Where(process => IsNewNearEvent(process, input.Event.Timestamp, input.LookbackMs, capturedAt))
            .OrderByDescending(process => process.StartedAt)
            .Take(12)
            .Select(process => $"{process.Name} ({process.ProcessId})")
            .ToArray();

        return new SpikeContextSnapshot
        {
            TriggerEventId = input.Event.Id,
            TriggerEventKind = input.Event.Kind,
            TriggerMetricKey = input.Event.MetricKey,
            TriggeredAt = input.Event.Timestamp,
            CapturedAt = capturedAt,
            RootTargetName = input.Target.DisplayName,
            RootProcessId = input.Target.ProcessId,
            WindowMsBefore = input.LookbackMs,
            WindowMsAfter = 0,
            TopProcessesByCpu = processes
                .Where(process => process.CpuPercent is > 0)
                .OrderByDescending(process => process.CpuPercent)
                .ThenByDescending(process => process.MemoryMb)
                .Take(TopProcessCount)
                .ToArray(),
            TopProcessesByMemory = processes
                .Where(process => process.MemoryMb is > 0)
                .OrderByDescending(process => process.MemoryMb)
                .Take(TopProcessCount)
                .ToArray(),
            TopProcessesByDisk = processes
                .Where(process => process.DiskMbPerSec is > 0)
                .OrderByDescending(process => process.DiskMbPerSec)
                .ThenByDescending(process => process.MemoryMb)
                .Take(TopProcessCount)
                .ToArray(),
            NewProcessNames = newProcesses,
            CaptureProvider = "lightweight-system-context-provider",
            Note = "Best-effort process snapshot captured after a detected event; not a continuous system trace."
        };
    }

    private static IReadOnlyList<ProcessProbe> CaptureProcessProbe()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var probes = new List<ProcessProbe>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    var processId = process.Id;
                    var processName = SafeGetProcessName(process);
                    var totalProcessorTime = SafeGetTotalProcessorTime(process);
                    var memoryMb = SafeGetMemoryMb(process);
                    var ioCounters = WindowsProcessIoCounters.TryGet(process);
                    var startedAt = SafeGetStartTime(process);

                    probes.Add(new ProcessProbe(
                        processId,
                        processName,
                        capturedAt,
                        totalProcessorTime,
                        memoryMb,
                        ioCounters,
                        startedAt));
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    continue;
                }
            }
        }

        return probes;
    }

    private static ContextProcessSnapshot BuildContextProcess(ProcessProbe current, ProcessProbe? previous)
    {
        var cpuPercent = 0d;
        var diskReadMbPerSec = 0d;
        var diskWriteMbPerSec = 0d;

        if (previous is not null)
        {
            var wallMs = Math.Max(1, (current.CapturedAt - previous.CapturedAt).TotalMilliseconds);
            if (current.TotalProcessorTime is not null && previous.TotalProcessorTime is not null)
            {
                var cpuDeltaMs = Math.Max(0, (current.TotalProcessorTime.Value - previous.TotalProcessorTime.Value).TotalMilliseconds);
                cpuPercent = Math.Clamp(cpuDeltaMs / wallMs / Environment.ProcessorCount * 100, 0, 100);
            }

            if (current.IoCounters is not null && previous.IoCounters is not null)
            {
                var seconds = wallMs / 1000d;
                diskReadMbPerSec = BytesToMegabytesPerSecond(
                    current.IoCounters.ReadTransferCount,
                    previous.IoCounters.ReadTransferCount,
                    seconds);
                diskWriteMbPerSec = BytesToMegabytesPerSecond(
                    current.IoCounters.WriteTransferCount,
                    previous.IoCounters.WriteTransferCount,
                    seconds);
            }
        }

        return new ContextProcessSnapshot
        {
            ProcessId = current.ProcessId,
            Name = current.Name,
            CpuPercent = cpuPercent,
            MemoryMb = current.MemoryMb,
            DiskReadMbPerSec = diskReadMbPerSec,
            DiskWriteMbPerSec = diskWriteMbPerSec,
            DiskMbPerSec = diskReadMbPerSec + diskWriteMbPerSec
        };
    }

    private static bool IsNewNearEvent(
        ProcessProbe process,
        DateTimeOffset eventTimestamp,
        int lookbackMs,
        DateTimeOffset capturedAt)
    {
        if (process.StartedAt is null)
        {
            return false;
        }

        var start = process.StartedAt.Value.ToUniversalTime();
        var windowStart = eventTimestamp.ToUniversalTime().AddMilliseconds(-Math.Max(0, lookbackMs));
        return start >= windowStart && start <= capturedAt.ToUniversalTime();
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

    private static TimeSpan? SafeGetTotalProcessorTime(Process process)
    {
        try
        {
            return process.TotalProcessorTime;
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

    private static double? SafeGetMemoryMb(Process process)
    {
        try
        {
            return WindowsProcessMemoryCounters.GetRamForAggregation(process).Megabytes;
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

    private static DateTimeOffset? SafeGetStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime);
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

    private static double BytesToMegabytesPerSecond(ulong current, ulong previous, double seconds)
    {
        if (current < previous || seconds <= 0)
        {
            return 0;
        }

        return (current - previous) / 1024d / 1024d / seconds;
    }

    private sealed record ProcessProbe(
        int ProcessId,
        string Name,
        DateTimeOffset CapturedAt,
        TimeSpan? TotalProcessorTime,
        double? MemoryMb,
        IoCounterSnapshot? IoCounters,
        DateTimeOffset? StartedAt);

    private sealed record IoCounterSnapshot(ulong ReadTransferCount, ulong WriteTransferCount);

    private static class WindowsProcessIoCounters
    {
        public static IoCounterSnapshot? TryGet(Process process)
        {
            try
            {
                return GetProcessIoCounters(process.Handle, out var counters)
                    ? new IoCounterSnapshot(counters.ReadTransferCount, counters.WriteTransferCount)
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
