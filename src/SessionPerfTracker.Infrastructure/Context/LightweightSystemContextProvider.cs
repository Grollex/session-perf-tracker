using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Infrastructure.Gpu;
using SessionPerfTracker.Infrastructure.Native;

namespace SessionPerfTracker.Infrastructure.Context;

/// <summary>
/// Captures "who else was busy" around an event: two system snapshots 250 ms apart give per-process
/// CPU and disk rates without opening any process handles.
/// </summary>
public sealed class LightweightSystemContextProvider : ISpikeContextProvider
{
    private const int ProbeDelayMs = 250;
    private const int TopProcessCount = 6;
    private readonly SystemProcessSnapshotReader _reader;
    private readonly GpuMonitor _gpu;

    public LightweightSystemContextProvider()
        : this(SystemProcessSnapshotReader.Shared, GpuMonitor.Shared)
    {
    }

    internal LightweightSystemContextProvider(SystemProcessSnapshotReader reader, GpuMonitor gpu)
    {
        _reader = reader;
        _gpu = gpu;
    }

    public async Task<SpikeContextSnapshot?> CaptureAsync(
        SpikeContextInput input,
        CancellationToken cancellationToken = default)
    {
        var first = await Task.Run(_reader.Capture, cancellationToken).ConfigureAwait(false);
        await Task.Delay(ProbeDelayMs, cancellationToken).ConfigureAwait(false);
        var second = await Task.Run(_reader.Capture, cancellationToken).ConfigureAwait(false);
        var gpu = _gpu.GetSample(TimeSpan.FromSeconds(2));

        if (second.Processes.Count == 0)
        {
            return null;
        }

        var elapsed = SystemProcessSnapshot.ElapsedSeconds(first, second);
        var processes = second.Processes.Values
            .Where(process => process.ProcessId != 0)
            .Select(process => BuildContextProcess(process, first, elapsed, gpu))
            .ToArray();

        var capturedAt = second.CapturedAt;
        var windowStart = input.Event.Timestamp.ToUniversalTime().AddMilliseconds(-Math.Max(0, input.LookbackMs));
        var newProcesses = second.Processes.Values
            .Where(process => process.StartedAtUtc is { } started && started >= windowStart && started <= capturedAt)
            .OrderByDescending(process => process.CreateTimeTicks)
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
            TopProcessesByGpu = processes
                .Where(process => process.GpuPercent is >= 0.5)
                .OrderByDescending(process => process.GpuPercent)
                .Take(TopProcessCount)
                .ToArray(),
            NewProcessNames = newProcesses,
            CaptureProvider = "lightweight-system-context-provider",
            Note = "Best-effort process snapshot captured after a detected event; not a continuous system trace."
        };
    }

    private static ContextProcessSnapshot BuildContextProcess(
        ProcessRawInfo current,
        SystemProcessSnapshot first,
        double elapsedSeconds,
        GpuSample gpu)
    {
        var cpuPercent = 0d;
        var diskRead = 0d;
        var diskWrite = 0d;
        if (first.TryGet(current.ProcessId, out var previous) && previous.CreateTimeTicks == current.CreateTimeTicks)
        {
            cpuPercent = SystemProcessSnapshot.CpuPercent(previous.CpuTime100Ns, current.CpuTime100Ns, elapsedSeconds);
            diskRead = SystemProcessSnapshot.MegabytesPerSecond(previous.ReadTransferBytes, current.ReadTransferBytes, elapsedSeconds);
            diskWrite = SystemProcessSnapshot.MegabytesPerSecond(previous.WriteTransferBytes, current.WriteTransferBytes, elapsedSeconds);
        }

        gpu.Processes.TryGetValue(current.ProcessId, out var gpuUsage);
        return new ContextProcessSnapshot
        {
            ProcessId = current.ProcessId,
            Name = current.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? current.Name[..^4] : current.Name,
            CpuPercent = cpuPercent,
            MemoryMb = current.PrivateWorkingSetMb,
            DiskReadMbPerSec = diskRead,
            DiskWriteMbPerSec = diskWrite,
            DiskMbPerSec = diskRead + diskWrite,
            GpuPercent = gpu.HasUtilization ? gpuUsage?.UtilizationPercent : null
        };
    }
}
