using System.Text;

namespace SessionPerfTracker.Infrastructure.Native;

internal static class ProcessImagePath
{
    /// <summary>
    /// Reads the full executable path with PROCESS_QUERY_LIMITED_INFORMATION, which works for
    /// elevated processes too. Only fails for a handful of protected system processes.
    /// </summary>
    public static string? TryQuery(int processId)
    {
        if (processId <= 4)
        {
            return null;
        }

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
}
