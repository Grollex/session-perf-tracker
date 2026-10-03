using System.Runtime.InteropServices;

namespace SessionPerfTracker.Infrastructure.Gpu;

internal sealed record GpuAdapterStaticInfo(
    string AdapterLuid,
    string Name,
    double? DedicatedVideoMemoryMb);

/// <summary>
/// Vendor-neutral adapter details through the WDDM kernel thunks (the same source Task Manager uses).
/// Every call is defensive: older drivers simply report nothing and the metric stays unavailable.
/// </summary>
internal static class D3dkmtAdapterInfo
{
    private const int QueryTypeSegmentSize = 3;
    private const int QueryTypeAdapterRegistryInfo = 8;
    private const int QueryTypeAdapterPerfData = 62;
    private const int RegistryInfoCharCount = 260;

    public static IReadOnlyList<GpuAdapterStaticInfo> EnumerateAdapters()
    {
        try
        {
            var enumerate = new D3dkmtEnumAdapters2();
            if (D3DKMTEnumAdapters2(ref enumerate) != 0 || enumerate.NumAdapters == 0)
            {
                return [];
            }

            var itemSize = Marshal.SizeOf<D3dkmtAdapterInfoNative>();
            var buffer = Marshal.AllocHGlobal(itemSize * (int)enumerate.NumAdapters);
            try
            {
                enumerate.Adapters = buffer;
                if (D3DKMTEnumAdapters2(ref enumerate) != 0)
                {
                    return [];
                }

                var adapters = new List<GpuAdapterStaticInfo>();
                for (var index = 0; index < enumerate.NumAdapters; index++)
                {
                    var adapter = Marshal.PtrToStructure<D3dkmtAdapterInfoNative>(buffer + index * itemSize);
                    try
                    {
                        var luid = FormatLuid(adapter.LuidLowPart, adapter.LuidHighPart);
                        adapters.Add(new GpuAdapterStaticInfo(
                            luid,
                            QueryAdapterName(adapter.Adapter) ?? $"GPU {luid}",
                            QueryDedicatedMemoryMb(adapter.Adapter)));
                    }
                    finally
                    {
                        var close = new D3dkmtCloseAdapter { Adapter = adapter.Adapter };
                        _ = D3DKMTCloseAdapter(ref close);
                    }
                }

                return adapters;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Current adapter temperature in Celsius, or null when the driver does not report it.</summary>
    public static double? TryQueryTemperatureC(string adapterLuid)
    {
        if (!GpuCounterInstanceName.TryParseLuid(adapterLuid, out var lowPart, out var highPart))
        {
            return null;
        }

        try
        {
            var open = new D3dkmtOpenAdapterFromLuid { LuidLowPart = lowPart, LuidHighPart = highPart };
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0 || open.Adapter == 0)
            {
                return null;
            }

            try
            {
                var perfData = new D3dkmtAdapterPerfData { PhysicalAdapterIndex = 0 };
                var size = Marshal.SizeOf<D3dkmtAdapterPerfData>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(perfData, buffer, false);
                    var query = new D3dkmtQueryAdapterInfo
                    {
                        Adapter = open.Adapter,
                        Type = QueryTypeAdapterPerfData,
                        PrivateDriverData = buffer,
                        PrivateDriverDataSize = (uint)size
                    };

                    if (D3DKMTQueryAdapterInfo(ref query) != 0)
                    {
                        return null;
                    }

                    perfData = Marshal.PtrToStructure<D3dkmtAdapterPerfData>(buffer);
                    // Reported in deci-Celsius. Zero means "not reported" on most integrated GPUs.
                    var celsius = perfData.Temperature / 10d;
                    return celsius is > 1 and < 150 ? celsius : null;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                var close = new D3dkmtCloseAdapter { Adapter = open.Adapter };
                _ = D3DKMTCloseAdapter(ref close);
            }
        }
        catch
        {
            return null;
        }
    }

    public static string FormatLuid(uint lowPart, int highPart) =>
        $"0x{unchecked((uint)highPart):x8}_0x{lowPart:x8}";

    private static string? QueryAdapterName(uint adapter)
    {
        var size = RegistryInfoCharCount * 4 * sizeof(char);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var query = new D3dkmtQueryAdapterInfo
            {
                Adapter = adapter,
                Type = QueryTypeAdapterRegistryInfo,
                PrivateDriverData = buffer,
                PrivateDriverDataSize = (uint)size
            };

            if (D3DKMTQueryAdapterInfo(ref query) != 0)
            {
                return null;
            }

            var name = Marshal.PtrToStringUni(buffer, RegistryInfoCharCount).TrimEnd('\0').Trim();
            var terminator = name.IndexOf('\0');
            if (terminator >= 0)
            {
                name = name[..terminator];
            }

            return string.IsNullOrWhiteSpace(name) ? null : name;
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

    private static double? QueryDedicatedMemoryMb(uint adapter)
    {
        var size = sizeof(ulong) * 3;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var query = new D3dkmtQueryAdapterInfo
            {
                Adapter = adapter,
                Type = QueryTypeSegmentSize,
                PrivateDriverData = buffer,
                PrivateDriverDataSize = (uint)size
            };

            if (D3DKMTQueryAdapterInfo(ref query) != 0)
            {
                return null;
            }

            var dedicated = (ulong)Marshal.ReadInt64(buffer);
            return dedicated == 0 ? null : dedicated / 1024d / 1024d;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtAdapterInfoNative
    {
        public uint Adapter;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtEnumAdapters2
    {
        public uint NumAdapters;
        public IntPtr Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtOpenAdapterFromLuid
    {
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtCloseAdapter
    {
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtQueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public IntPtr PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtAdapterPerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOc;
        public ulong MemoryBandwidth;
        public ulong PcieBandwidth;
        public uint FanRpm;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref D3dkmtEnumAdapters2 enumerate);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref D3dkmtOpenAdapterFromLuid open);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3dkmtCloseAdapter close);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3dkmtQueryAdapterInfo query);
}
