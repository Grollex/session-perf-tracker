using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.App.ViewModels;

/// <summary>
/// One KPI tile on the Live screen. Instances live for the whole session and are updated in
/// place, so a refresh changes a few bound properties instead of rebuilding the tile list.
/// </summary>
public sealed class LiveMetricTileViewModel : ObservableObject
{
    private string _valueText = "—";
    private string _detailText = string.Empty;
    private string _tone = "normal";
    private bool _isAvailable = true;
    private IReadOnlyList<double> _history = [];

    public LiveMetricTileViewModel(string key, string label, double scaleMaximum, string accent)
    {
        Key = key;
        Label = label;
        ScaleMaximum = scaleMaximum;
        Accent = accent;
    }

    public string Key { get; }
    public string Label { get; }
    public double ScaleMaximum { get; }

    /// <summary>Accent name used by the view to pick the line color (cpu, ram, gpu, disk).</summary>
    public string Accent { get; }

    public string ValueText
    {
        get => _valueText;
        set => SetProperty(ref _valueText, value);
    }

    public string DetailText
    {
        get => _detailText;
        set => SetProperty(ref _detailText, value);
    }

    /// <summary>normal / warning / critical relative to the active session limits.</summary>
    public string Tone
    {
        get => _tone;
        set => SetProperty(ref _tone, value);
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        set => SetProperty(ref _isAvailable, value);
    }

    public IReadOnlyList<double> History
    {
        get => _history;
        set => SetProperty(ref _history, value);
    }

    public static string ResolveTone(double? value, double? limit)
    {
        if (value is null || limit is null || limit <= 0)
        {
            return "normal";
        }

        var ratio = value.Value / limit.Value;
        return ratio >= 1 ? "critical" : ratio >= 0.85 ? "warning" : "normal";
    }
}

/// <summary>Thread-safe rolling history of recent samples for the live sparklines.</summary>
internal sealed class LiveMetricHistory
{
    public const int Capacity = 90;
    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<double>> _series = new(StringComparer.Ordinal);

    public void Clear()
    {
        lock (_sync)
        {
            _series.Clear();
        }
    }

    public void Add(MetricSample sample)
    {
        lock (_sync)
        {
            Push("cpu", sample, MetricKey.CpuPercent);
            Push("ram", sample, MetricKey.MemoryMb);
            Push("gpu", sample, MetricKey.GpuPercent);
            if (sample.Values.TryGetValue(MetricKey.DiskReadMbPerSec, out var read)
                | sample.Values.TryGetValue(MetricKey.DiskWriteMbPerSec, out var write))
            {
                PushValue("disk", read + write);
            }
        }
    }

    public IReadOnlyList<double> Snapshot(string key)
    {
        lock (_sync)
        {
            return _series.TryGetValue(key, out var queue) ? queue.ToArray() : [];
        }
    }

    private void Push(string key, MetricSample sample, MetricKey metric)
    {
        if (sample.Values.TryGetValue(metric, out var value))
        {
            PushValue(key, value);
        }
    }

    private void PushValue(string key, double value)
    {
        if (!_series.TryGetValue(key, out var queue))
        {
            queue = new Queue<double>(Capacity + 1);
            _series[key] = queue;
        }

        queue.Enqueue(double.IsFinite(value) ? value : 0);
        while (queue.Count > Capacity)
        {
            queue.Dequeue();
        }
    }
}
