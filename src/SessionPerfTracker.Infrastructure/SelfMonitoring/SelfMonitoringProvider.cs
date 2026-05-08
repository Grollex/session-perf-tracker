using System.Diagnostics;
using System.Runtime.InteropServices;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Infrastructure.Collectors;

namespace SessionPerfTracker.Infrastructure.SelfMonitoring;

public sealed class SelfMonitoringProvider : ISelfMonitoringProvider
{
    private readonly object _sync = new();
    private DateTimeOffset? _previousTimestamp;
    private TimeSpan? _previousProcessorTime;
    private ulong? _previousWriteBytes;

    public Task<SelfMonitoringSample> SampleSelfAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var process = Process.GetCurrentProcess();
        process.Refresh();

        var now = DateTimeOffset.UtcNow;
        var totalProcessorTime = process.TotalProcessorTime;
        var memoryMb = WindowsProcessMemoryCounters.GetRamForAggregation(process).Megabytes;
        var ioCounters = WindowsProcessIoCounters.TryGet(process);

        double? cpuPercent = null;
        double? diskWriteMbPerSec = null;

        lock (_sync)
        {
            if (_previousTimestamp is not null && _previousProcessorTime is not null)
            {
                var wallMs = Math.Max(1, (now - _previousTimestamp.Value).TotalMilliseconds);
                var cpuDeltaMs = Math.Max(0, (totalProcessorTime - _previousProcessorTime.Value).TotalMilliseconds);
                cpuPercent = Math.Clamp(cpuDeltaMs / wallMs / Environment.ProcessorCount * 100, 0, 100);
            }

            if (ioCounters is not null && _previousTimestamp is not null && _previousWriteBytes is not null)
            {
                var seconds = Math.Max(0.001, (now - _previousTimestamp.Value).TotalSeconds);
                diskWriteMbPerSec = BytesToMegabytesPerSecond(ioCounters.WriteTransferCount, _previousWriteBytes.Value, seconds);
            }

            _previousTimestamp = now;
            _previousProcessorTime = totalProcessorTime;
            _previousWriteBytes = ioCounters?.WriteTransferCount;
        }

        return Task.FromResult(new SelfMonitoringSample(
            now,
            CpuPercent: cpuPercent,
            MemoryMb: memoryMb,
            DiskWriteMbPerSec: diskWriteMbPerSec));
    }

    public Task<SelfOverheadSummary> SummarizeAsync(
        IReadOnlyList<SelfMonitoringSample> samples,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new SelfOverheadSummary
        {
            AvgCpuPercent = Average(samples.Select(sample => sample.CpuPercent)),
            MaxCpuPercent = Max(samples.Select(sample => sample.CpuPercent)),
            AvgMemoryMb = Average(samples.Select(sample => sample.MemoryMb)),
            MaxMemoryMb = Max(samples.Select(sample => sample.MemoryMb)),
            AvgDiskWriteMbPerSec = Average(samples.Select(sample => sample.DiskWriteMbPerSec)),
            SampleCount = samples.Count
        });
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var concrete = values
            .Where(value => value.HasValue && double.IsFinite(value.Value))
            .Select(value => value!.Value)
            .ToArray();

        return concrete.Length == 0 ? null : concrete.Average();
    }

    private static double? Max(IEnumerable<double?> values)
    {
        var concrete = values
            .Where(value => value.HasValue && double.IsFinite(value.Value))
            .Select(value => value!.Value)
            .ToArray();

        return concrete.Length == 0 ? null : concrete.Max();
    }

    private static double BytesToMegabytesPerSecond(ulong current, ulong previous, double seconds)
    {
        if (current < previous || seconds <= 0)
        {
            return 0;
        }

        return (current - previous) / 1024d / 1024d / seconds;
    }

    private sealed record IoCounterSnapshot(ulong WriteTransferCount);

    private static class WindowsProcessIoCounters
    {
        public static IoCounterSnapshot? TryGet(Process process)
        {
            try
            {
                return GetProcessIoCounters(process.Handle, out var counters)
                    ? new IoCounterSnapshot(counters.WriteTransferCount)
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
