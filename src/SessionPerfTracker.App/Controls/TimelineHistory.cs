namespace SessionPerfTracker.App.Controls;

public readonly record struct TimelinePoint(
    double Seconds,
    double Cpu,
    double Gpu,
    double RamPercent,
    double RamMb,
    double Disk);

/// <summary>0 = info, 1 = warning, 2 = critical.</summary>
public readonly record struct TimelineMarker(double Seconds, int Severity, string Title);

public sealed record TimelineWindow(
    DateTimeOffset Origin,
    double StartSeconds,
    double EndSeconds,
    IReadOnlyList<TimelinePoint> Points,
    IReadOnlyList<TimelineMarker> Markers);

/// <summary>
/// Thread-safe history for the Live timeline. Writers append from the sampling or scan thread;
/// the chart copies only the visible window, already reduced to about one point per pixel.
/// </summary>
public sealed class TimelineHistory
{
    private const int MaxPoints = 20_000;
    private const int MaxMarkers = 500;
    private readonly object _sync = new();
    private readonly List<TimelinePoint> _points = [];
    private readonly List<TimelineMarker> _markers = [];
    private DateTimeOffset _origin = DateTimeOffset.Now;

    public DateTimeOffset Origin
    {
        get
        {
            lock (_sync)
            {
                return _origin;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _points.Count;
            }
        }
    }

    public void Reset(DateTimeOffset origin)
    {
        lock (_sync)
        {
            _origin = origin;
            _points.Clear();
            _markers.Clear();
        }
    }

    public void Add(TimelinePoint point)
    {
        lock (_sync)
        {
            _points.Add(point);
            if (_points.Count > MaxPoints)
            {
                CompactOldestHalf();
            }
        }
    }

    public void AddMarker(TimelineMarker marker)
    {
        lock (_sync)
        {
            _markers.Add(marker);
            if (_markers.Count > MaxMarkers)
            {
                _markers.RemoveRange(0, _markers.Count - MaxMarkers);
            }
        }
    }

    /// <summary>Copies the last <paramref name="windowSeconds"/> (0 = everything), reduced to at most <paramref name="maxPoints"/>.</summary>
    public TimelineWindow GetWindow(double windowSeconds, int maxPoints)
    {
        lock (_sync)
        {
            if (_points.Count == 0)
            {
                return new TimelineWindow(_origin, 0, Math.Max(windowSeconds, 60), [], []);
            }

            // A young history grows from the left edge instead of hugging the right one.
            var dataStart = _points[0].Seconds;
            var dataEnd = _points[^1].Seconds;
            double start, end;
            if (windowSeconds <= 0)
            {
                start = dataStart;
                end = Math.Max(dataEnd, dataStart + 60);
            }
            else if (dataEnd - dataStart < windowSeconds)
            {
                // Until the history fills the range, stretch what exists (at least one minute).
                start = dataStart;
                end = dataStart + Math.Max(60, dataEnd - dataStart);
            }
            else
            {
                start = dataEnd - windowSeconds;
                end = dataEnd;
            }

            var first = LowerBound(start);
            var count = _points.Count - first;
            var points = count <= maxPoints
                ? _points.GetRange(first, count)
                : Reduce(first, count, maxPoints);
            var markers = _markers.Where(marker => marker.Seconds >= start && marker.Seconds <= end).ToArray();
            return new TimelineWindow(_origin, start, end, points, markers);
        }
    }

    private int LowerBound(double seconds)
    {
        int low = 0, high = _points.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_points[middle].Seconds < seconds)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return Math.Max(0, low - 1);
    }

    /// <summary>Bucket maximum keeps short spikes visible when many samples share a pixel.</summary>
    private List<TimelinePoint> Reduce(int first, int count, int maxPoints)
    {
        var result = new List<TimelinePoint>(maxPoints + 1);
        var bucketSize = (double)count / maxPoints;
        for (var bucket = 0; bucket < maxPoints; bucket++)
        {
            var from = first + (int)(bucket * bucketSize);
            var to = Math.Min(first + count, first + (int)((bucket + 1) * bucketSize));
            if (to <= from)
            {
                continue;
            }

            var merged = _points[from];
            for (var index = from + 1; index < to; index++)
            {
                merged = MaxOf(merged, _points[index]);
            }

            result.Add(merged with { Seconds = _points[to - 1].Seconds });
        }

        return result;
    }

    private void CompactOldestHalf()
    {
        var half = _points.Count / 2;
        var compacted = new List<TimelinePoint>(_points.Count);
        for (var index = 0; index + 1 < half; index += 2)
        {
            // Keep the earlier timestamp so repeated compaction never moves the start of the history.
            compacted.Add(MaxOf(_points[index], _points[index + 1]) with { Seconds = _points[index].Seconds });
        }

        compacted.AddRange(_points.GetRange(half, _points.Count - half));
        _points.Clear();
        _points.AddRange(compacted);
    }

    private static TimelinePoint MaxOf(TimelinePoint left, TimelinePoint right) => new(
        right.Seconds,
        MaxNumber(left.Cpu, right.Cpu),
        MaxNumber(left.Gpu, right.Gpu),
        MaxNumber(left.RamPercent, right.RamPercent),
        MaxNumber(left.RamMb, right.RamMb),
        MaxNumber(left.Disk, right.Disk));

    private static double MaxNumber(double left, double right) =>
        double.IsNaN(left) ? right : double.IsNaN(right) ? left : Math.Max(left, right);
}
