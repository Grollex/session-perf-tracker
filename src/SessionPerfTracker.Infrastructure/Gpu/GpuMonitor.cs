using System.Diagnostics;

namespace SessionPerfTracker.Infrastructure.Gpu;

internal sealed record GpuProcessUsage(double UtilizationPercent, double DedicatedMb, double SharedMb);

internal sealed record GpuAdapterUsage(
    string AdapterLuid,
    string Name,
    double UtilizationPercent,
    double DedicatedUsedMb,
    double? DedicatedTotalMb,
    double? TemperatureC);

internal sealed record GpuSample(
    DateTimeOffset CapturedAt,
    bool IsAvailable,
    bool HasUtilization,
    IReadOnlyDictionary<int, GpuProcessUsage> Processes,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, double>> ProcessEngines,
    IReadOnlyList<GpuAdapterUsage> Adapters)
{
    public static GpuSample Unavailable { get; } = new(
        DateTimeOffset.MinValue,
        false,
        false,
        new Dictionary<int, GpuProcessUsage>(),
        new Dictionary<int, IReadOnlyDictionary<string, double>>(),
        []);

    /// <summary>The adapter that matters most for the user: the busiest one, then the one with most VRAM.</summary>
    public GpuAdapterUsage? PrimaryAdapter => Adapters
        .OrderByDescending(adapter => adapter.UtilizationPercent >= 1 ? adapter.UtilizationPercent : 0)
        .ThenByDescending(adapter => adapter.DedicatedTotalMb ?? 0)
        .ThenByDescending(adapter => adapter.DedicatedUsedMb)
        .FirstOrDefault();

    public GpuProcessUsage? Aggregate(IEnumerable<int> processIds)
    {
        if (!IsAvailable)
        {
            return null;
        }

        var dedicated = 0d;
        var shared = 0d;
        var engineTotals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var processId in processIds)
        {
            if (Processes.TryGetValue(processId, out var usage))
            {
                dedicated += usage.DedicatedMb;
                shared += usage.SharedMb;
            }

            if (ProcessEngines.TryGetValue(processId, out var engines))
            {
                foreach (var (engine, value) in engines)
                {
                    engineTotals[engine] = engineTotals.GetValueOrDefault(engine) + value;
                }
            }
        }

        // Same rule as for a single process: the group's load is its busiest engine.
        var utilization = engineTotals.Count == 0 ? 0 : Math.Min(100, engineTotals.Values.Max());
        return new GpuProcessUsage(utilization, dedicated, shared);
    }
}

/// <summary>
/// Vendor-neutral GPU sampler built on the WDDM "GPU Engine"/"GPU Process Memory" performance
/// counters (Windows 10 1709+). One shared PDH query serves Live sessions and Global Watch,
/// and results are cached so concurrent callers never trigger duplicate collections.
/// </summary>
internal sealed class GpuMonitor : IDisposable
{
    private static readonly TimeSpan AdapterInfoRefresh = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TemperatureRefresh = TimeSpan.FromSeconds(4);
    private readonly object _sync = new();
    private readonly PdhCounterArrayReader _reader = new();
    private IntPtr _query;
    private IntPtr _engineCounter;
    private IntPtr _processDedicatedCounter;
    private IntPtr _processSharedCounter;
    private IntPtr _adapterDedicatedCounter;
    private bool _initialized;
    private bool _available;
    private bool _primed;
    private bool _disposed;
    private GpuSample _last = GpuSample.Unavailable;
    private long _lastCollectTimestamp;
    private IReadOnlyDictionary<string, GpuAdapterStaticInfo> _adapterInfo = new Dictionary<string, GpuAdapterStaticInfo>();
    private DateTimeOffset _adapterInfoLoadedAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, double?> _temperatureByAdapter = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _temperatureLoadedAt = DateTimeOffset.MinValue;

    public static GpuMonitor Shared { get; } = new();

    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                EnsureInitialized();
                return _available;
            }
        }
    }

    /// <summary>
    /// Returns a sample no older than <paramref name="maxAge"/>. Rate counters need two collections,
    /// so the very first call primes the query and reports zero utilization.
    /// </summary>
    public GpuSample GetSample(TimeSpan maxAge)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return GpuSample.Unavailable;
            }

            EnsureInitialized();
            if (!_available)
            {
                return GpuSample.Unavailable;
            }

            if (_lastCollectTimestamp != 0
                && Stopwatch.GetElapsedTime(_lastCollectTimestamp) < maxAge)
            {
                return _last;
            }

            try
            {
                _last = Collect();
            }
            catch
            {
                _last = _last with { CapturedAt = DateTimeOffset.UtcNow };
            }

            _lastCollectTimestamp = Stopwatch.GetTimestamp();
            return _last;
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            if (PdhNative.PdhOpenQueryW(null, IntPtr.Zero, out _query) != PdhNative.ErrorSuccess)
            {
                _query = IntPtr.Zero;
                return;
            }

            if (PdhNative.PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engineCounter) != PdhNative.ErrorSuccess)
            {
                _engineCounter = IntPtr.Zero;
            }

            if (PdhNative.PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out _processDedicatedCounter) != PdhNative.ErrorSuccess)
            {
                _processDedicatedCounter = IntPtr.Zero;
            }

            if (PdhNative.PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Shared Usage", IntPtr.Zero, out _processSharedCounter) != PdhNative.ErrorSuccess)
            {
                _processSharedCounter = IntPtr.Zero;
            }

            if (PdhNative.PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _adapterDedicatedCounter) != PdhNative.ErrorSuccess)
            {
                _adapterDedicatedCounter = IntPtr.Zero;
            }

            _available = _engineCounter != IntPtr.Zero;
            if (!_available)
            {
                PdhNative.PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                return;
            }

            // Prime the rate counter so the next collection has a valid delta.
            _ = PdhNative.PdhCollectQueryData(_query);
            _lastCollectTimestamp = 0;
        }
        catch
        {
            _available = false;
        }
    }

    private GpuSample Collect()
    {
        if (PdhNative.PdhCollectQueryData(_query) != PdhNative.ErrorSuccess)
        {
            return _last;
        }

        var hasUtilization = _primed;
        _primed = true;
        var now = DateTimeOffset.UtcNow;
        RefreshAdapterInfoIfNeeded(now);

        // engine key -> pid -> utilization
        var engineTotals = new Dictionary<string, double>(StringComparer.Ordinal);
        var engineAdapter = new Dictionary<string, string>(StringComparer.Ordinal);
        var processEngine = new Dictionary<(int Pid, string Engine), double>();
        _reader.Read(_engineCounter, PdhNative.PdhFmtDouble | PdhNative.PdhFmtNoCap100, (name, value) =>
        {
            if (value <= 0 || !GpuCounterInstanceName.TryParse(name, out var instance) || instance.ProcessId is not int pid)
            {
                return;
            }

            var engineKey = instance.EngineKey;
            engineTotals[engineKey] = engineTotals.GetValueOrDefault(engineKey) + value;
            engineAdapter[engineKey] = instance.AdapterLuid;
            var key = (pid, engineKey);
            processEngine[key] = processEngine.GetValueOrDefault(key) + value;
        });

        var processUtilization = new Dictionary<int, double>();
        var processEngines = new Dictionary<int, IReadOnlyDictionary<string, double>>();
        foreach (var ((pid, engineKey), value) in processEngine)
        {
            // Task Manager semantics: a process' GPU usage is its busiest engine.
            processUtilization[pid] = Math.Max(processUtilization.GetValueOrDefault(pid), Math.Min(100, value));
            if (!processEngines.TryGetValue(pid, out var engines))
            {
                engines = new Dictionary<string, double>(StringComparer.Ordinal);
                processEngines[pid] = engines;
            }

            ((Dictionary<string, double>)engines)[engineKey] = value;
        }

        var dedicatedByProcess = ReadProcessMemory(_processDedicatedCounter, clampToAdapterVram: true);
        var sharedByProcess = ReadProcessMemory(_processSharedCounter, clampToAdapterVram: false);
        var processes = new Dictionary<int, GpuProcessUsage>();
        foreach (var pid in processUtilization.Keys.Concat(dedicatedByProcess.Keys).Concat(sharedByProcess.Keys).Distinct())
        {
            processes[pid] = new GpuProcessUsage(
                hasUtilization ? processUtilization.GetValueOrDefault(pid) : 0,
                dedicatedByProcess.GetValueOrDefault(pid) / 1024d / 1024d,
                sharedByProcess.GetValueOrDefault(pid) / 1024d / 1024d);
        }

        var adapterDedicatedUsed = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        _reader.Read(_adapterDedicatedCounter, PdhNative.PdhFmtDouble, (name, value) =>
        {
            if (GpuCounterInstanceName.TryParse(name, out var instance))
            {
                adapterDedicatedUsed[instance.AdapterLuid] = adapterDedicatedUsed.GetValueOrDefault(instance.AdapterLuid) + value;
            }
        });

        var adapterUtilization = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (engineKey, total) in engineTotals)
        {
            var luid = engineAdapter[engineKey];
            adapterUtilization[luid] = Math.Max(adapterUtilization.GetValueOrDefault(luid), Math.Min(100, total));
        }

        RefreshTemperaturesIfNeeded(now, adapterDedicatedUsed.Keys.Concat(_adapterInfo.Keys));
        var adapters = adapterDedicatedUsed.Keys
            .Concat(adapterUtilization.Keys)
            .Concat(_adapterInfo.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(luid =>
            {
                _adapterInfo.TryGetValue(luid, out var info);
                return new GpuAdapterUsage(
                    luid,
                    info?.Name ?? $"GPU {luid}",
                    hasUtilization ? adapterUtilization.GetValueOrDefault(luid) : 0,
                    adapterDedicatedUsed.GetValueOrDefault(luid) / 1024d / 1024d,
                    info?.DedicatedVideoMemoryMb,
                    _temperatureByAdapter.GetValueOrDefault(luid));
            })
            // Hide software renderers (no VRAM, never busy).
            .Where(adapter => (adapter.DedicatedTotalMb ?? 0) > 0 || adapter.DedicatedUsedMb > 0 || adapter.UtilizationPercent > 0)
            .ToArray();

        return new GpuSample(now, true, hasUtilization, processes, hasUtilization ? processEngines : new Dictionary<int, IReadOnlyDictionary<string, double>>(), adapters);
    }

    private Dictionary<int, double> ReadProcessMemory(IntPtr counter, bool clampToAdapterVram)
    {
        var result = new Dictionary<int, double>();
        _reader.Read(counter, PdhNative.PdhFmtDouble, (name, value) =>
        {
            if (value <= 0 || !GpuCounterInstanceName.TryParse(name, out var instance) || instance.ProcessId is not int pid)
            {
                return;
            }

            // Some drivers over-report the compositor's dedicated usage far beyond physical VRAM;
            // such values are meaningless, so drop them instead of polluting totals.
            if (clampToAdapterVram
                && _adapterInfo.TryGetValue(instance.AdapterLuid, out var adapter)
                && adapter.DedicatedVideoMemoryMb is double totalMb
                && value / 1024d / 1024d > totalMb * 1.05)
            {
                return;
            }

            result[pid] = result.GetValueOrDefault(pid) + value;
        });

        return result;
    }

    private void RefreshAdapterInfoIfNeeded(DateTimeOffset now)
    {
        if (now - _adapterInfoLoadedAt < AdapterInfoRefresh)
        {
            return;
        }

        _adapterInfoLoadedAt = now;
        _adapterInfo = D3dkmtAdapterInfo.EnumerateAdapters()
            .GroupBy(adapter => adapter.AdapterLuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private void RefreshTemperaturesIfNeeded(DateTimeOffset now, IEnumerable<string> adapterLuids)
    {
        if (now - _temperatureLoadedAt < TemperatureRefresh)
        {
            return;
        }

        _temperatureLoadedAt = now;
        foreach (var luid in adapterLuids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _temperatureByAdapter[luid] = D3dkmtAdapterInfo.TryQueryTemperatureC(luid);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_query != IntPtr.Zero)
            {
                PdhNative.PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }

            _reader.Dispose();
        }
    }
}
