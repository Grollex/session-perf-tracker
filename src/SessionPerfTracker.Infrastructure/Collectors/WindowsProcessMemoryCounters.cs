using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SessionPerfTracker.Infrastructure.Collectors;

internal static class WindowsProcessMemoryCounters
{
    public const string PreferredMetricName = "Private Working Set";
    private const string FallbackMetricName = "Private Bytes";

    public static ProcessMemoryValue GetRamForAggregation(Process process)
    {
        if (TryGetPrivateWorkingSet(process, out var privateWorkingSetBytes))
        {
            return new ProcessMemoryValue(PreferredMetricName, privateWorkingSetBytes);
        }

        return new ProcessMemoryValue(FallbackMetricName, (ulong)Math.Max(0, process.PrivateMemorySize64));
    }

    private static bool TryGetPrivateWorkingSet(Process process, out ulong bytes)
    {
        bytes = 0;

        try
        {
            var counters = new ProcessMemoryCountersEx2
            {
                Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>()
            };

            if (!GetProcessMemoryInfo(process.Handle, ref counters, counters.Size))
            {
                return false;
            }

            bytes = counters.PrivateWorkingSetSize.ToUInt64();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr process,
        ref ProcessMemoryCountersEx2 counters,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint Size;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
        public UIntPtr PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }
}

internal sealed record ProcessMemoryValue(string MetricName, ulong Bytes)
{
    public double Megabytes => Bytes / 1024d / 1024d;
}
