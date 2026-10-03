using System.Runtime.InteropServices;
using System.Text;

namespace SessionPerfTracker.Infrastructure.Native;

internal static class ProcessImagePath
{
    private const int SystemProcessIdInformationClass = 0x58;
    private const int NameBufferBytes = 4096;
    private static readonly object DeviceMapSync = new();
    private static readonly TimeSpan DeviceMapRefresh = TimeSpan.FromMinutes(5);
    private static IReadOnlyList<(string Device, string Drive)> _deviceMap = [];
    private static DateTimeOffset _deviceMapLoadedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Full executable path. Tries PROCESS_QUERY_LIMITED_INFORMATION first, then the kernel's
    /// image name for the pid (works for services and protected processes without opening them).
    /// </summary>
    public static string? TryQuery(int processId)
    {
        if (processId <= 4)
        {
            return null;
        }

        return TryQueryWithHandle(processId) ?? TryQueryKernelImageName(processId);
    }

    private static string? TryQueryWithHandle(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var builder = new StringBuilder(1024);
            var size = builder.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, builder, ref size))
            {
                return null;
            }

            var path = builder.ToString();
            return string.IsNullOrWhiteSpace(path) ? null : path;
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

    private static string? TryQueryKernelImageName(int processId)
    {
        var buffer = Marshal.AllocHGlobal(NameBufferBytes);
        try
        {
            var info = new SystemProcessIdInformation
            {
                ProcessId = processId,
                Length = 0,
                MaximumLength = NameBufferBytes,
                Buffer = buffer
            };

            var status = NtQuerySystemInformation(
                SystemProcessIdInformationClass,
                ref info,
                Marshal.SizeOf<SystemProcessIdInformation>(),
                out _);
            if (status != 0 || info.Length == 0)
            {
                return null;
            }

            var ntPath = Marshal.PtrToStringUni(buffer, info.Length / 2);
            return ToDosPath(ntPath);
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string? ToDosPath(string ntPath)
    {
        if (string.IsNullOrWhiteSpace(ntPath))
        {
            return null;
        }

        if (ntPath.Length > 2 && ntPath[1] == ':')
        {
            return ntPath;
        }

        foreach (var (device, drive) in GetDeviceMap())
        {
            if (ntPath.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return drive + ntPath[device.Length..];
            }
        }

        return null;
    }

    private static IReadOnlyList<(string Device, string Drive)> GetDeviceMap()
    {
        lock (DeviceMapSync)
        {
            if (DateTimeOffset.UtcNow - _deviceMapLoadedAt < DeviceMapRefresh && _deviceMap.Count > 0)
            {
                return _deviceMap;
            }

            var map = new List<(string Device, string Drive)>();
            var builder = new StringBuilder(1024);
            foreach (var drive in Environment.GetLogicalDrives())
            {
                var letter = drive.TrimEnd('\\');
                builder.Clear();
                if (QueryDosDevice(letter, builder, builder.Capacity) > 0)
                {
                    // The first entry of the multi-string is the current target.
                    var target = builder.ToString();
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        map.Add((target, letter));
                    }
                }
            }

            _deviceMap = map;
            _deviceMapLoadedAt = DateTimeOffset.UtcNow;
            return map;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessIdInformation
    {
        public IntPtr ProcessId;
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(
        int systemInformationClass,
        ref SystemProcessIdInformation systemInformation,
        int systemInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int maxLength);
}
