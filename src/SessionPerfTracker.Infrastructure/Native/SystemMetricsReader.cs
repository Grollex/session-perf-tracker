using System.Runtime.InteropServices;

namespace SessionPerfTracker.Infrastructure.Native;

internal sealed record SystemLoadSample(
    double? CpuPercent,
    double? MemoryUsedMb,
    double? MemoryTotalMb,
    double? MemoryLoadPercent);

/// <summary>Whole-system CPU and RAM usage from GetSystemTimes/GlobalMemoryStatusEx.</summary>
internal sealed class SystemMetricsReader
{
    private readonly object _sync = new();
    private long _previousIdle;
    private long _previousTotal;
    private bool _hasPrevious;

    public SystemLoadSample Sample()
    {
        double? cpuPercent = null;
        lock (_sync)
        {
            if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                // Kernel time includes idle time.
                var total = kernel + user;
                if (_hasPrevious)
                {
                    var totalDelta = total - _previousTotal;
                    var idleDelta = idle - _previousIdle;
                    if (totalDelta > 0)
                    {
                        cpuPercent = Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
                    }
                }

                _previousIdle = idle;
                _previousTotal = total;
                _hasPrevious = true;
            }
        }

        var status = new NativeMethods.MemoryStatusEx { Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
        {
            return new SystemLoadSample(cpuPercent, null, null, null);
        }

        var totalMb = status.TotalPhys / 1024d / 1024d;
        var usedMb = (status.TotalPhys - status.AvailPhys) / 1024d / 1024d;
        return new SystemLoadSample(cpuPercent, usedMb, totalMb, status.MemoryLoad);
    }
}
