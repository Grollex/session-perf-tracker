using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Infrastructure.Gpu;
using SessionPerfTracker.Infrastructure.Native;

namespace SessionPerfTracker.Infrastructure.SelfMonitoring;

/// <summary>Whole-machine CPU/RAM/GPU load for the Live header and the Global Watch summary.</summary>
public sealed class SystemLoadProvider : ISystemLoadProvider
{
    private readonly SystemMetricsReader _metrics = new();
    private readonly GpuMonitor _gpu;
    private readonly bool _captureGpu;

    public SystemLoadProvider(bool captureGpu = true)
        : this(GpuMonitor.Shared, captureGpu)
    {
    }

    internal SystemLoadProvider(GpuMonitor gpu, bool captureGpu)
    {
        _gpu = gpu;
        _captureGpu = captureGpu;
    }

    public SystemLoadSnapshot Sample() => Sample(processCount: 0);

    internal SystemLoadSnapshot Sample(int processCount)
    {
        var load = _metrics.Sample();
        var gpu = _captureGpu ? _gpu.GetSample(TimeSpan.FromMilliseconds(1500)) : GpuSample.Unavailable;
        var adapter = gpu.PrimaryAdapter;

        return new SystemLoadSnapshot
        {
            CapturedAt = DateTimeOffset.UtcNow,
            CpuPercent = load.CpuPercent,
            MemoryUsedMb = load.MemoryUsedMb,
            MemoryTotalMb = load.MemoryTotalMb,
            CommitUsedMb = load.CommitUsedMb,
            CommitLimitMb = load.CommitLimitMb,
            ProcessCount = processCount,
            GpuAvailable = gpu.IsAvailable && adapter is not null,
            GpuName = adapter?.Name,
            GpuPercent = gpu.HasUtilization ? adapter?.UtilizationPercent : null,
            GpuDedicatedUsedMb = adapter?.DedicatedUsedMb,
            GpuDedicatedTotalMb = adapter?.DedicatedTotalMb,
            GpuTemperatureC = adapter?.TemperatureC
        };
    }
}
