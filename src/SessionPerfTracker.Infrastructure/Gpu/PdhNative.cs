using System.Runtime.InteropServices;

namespace SessionPerfTracker.Infrastructure.Gpu;

internal static class PdhNative
{
    public const uint ErrorSuccess = 0;
    public const uint PdhMoreData = 0x800007D2;
    public const uint PdhFmtDouble = 0x00000200;
    public const uint PdhFmtNoCap100 = 0x00008000;
    public const uint PdhCstatusValidData = 0x00000000;
    public const uint PdhCstatusNewData = 0x00000001;

    // PDH_FMT_COUNTERVALUE_ITEM_W on x64: LPWSTR szName (8) + { DWORD CStatus; pad; union (8) } (16).
    public const int ItemSize = 24;
    public const int ItemStatusOffset = 8;
    public const int ItemValueOffset = 16;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        out uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr query);
}

/// <summary>Reusable buffer reader for wildcard PDH counters.</summary>
internal sealed class PdhCounterArrayReader : IDisposable
{
    private IntPtr _buffer;
    private uint _bufferSize;

    public void Read(IntPtr counter, uint format, Action<string, double> onValue)
    {
        if (counter == IntPtr.Zero)
        {
            return;
        }

        var size = _bufferSize;
        var status = PdhNative.PdhGetFormattedCounterArrayW(counter, format, ref size, out var count, _buffer);
        if (status == PdhNative.PdhMoreData)
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
            }

            _bufferSize = size + 4096;
            _buffer = Marshal.AllocHGlobal((int)_bufferSize);
            size = _bufferSize;
            status = PdhNative.PdhGetFormattedCounterArrayW(counter, format, ref size, out count, _buffer);
        }

        if (status != PdhNative.ErrorSuccess || _buffer == IntPtr.Zero)
        {
            return;
        }

        for (var index = 0; index < count; index++)
        {
            var item = _buffer + index * PdhNative.ItemSize;
            var itemStatus = (uint)Marshal.ReadInt32(item, PdhNative.ItemStatusOffset);
            if (itemStatus != PdhNative.PdhCstatusValidData && itemStatus != PdhNative.PdhCstatusNewData)
            {
                continue;
            }

            var namePointer = Marshal.ReadIntPtr(item);
            if (namePointer == IntPtr.Zero)
            {
                continue;
            }

            var name = Marshal.PtrToStringUni(namePointer);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, PdhNative.ItemValueOffset));
            if (double.IsFinite(value))
            {
                onValue(name, value);
            }
        }
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferSize = 0;
        }
    }
}
