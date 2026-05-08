using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Metrics;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Events;

public sealed class CpuMemoryThresholdSpikeDetector : IEventDetector
{
    private static readonly MetricKey[] HandledMetricKeys =
    [
        MetricKey.CpuPercent,
        MetricKey.MemoryMb,
        MetricKey.DiskReadMbPerSec,
        MetricKey.DiskWriteMbPerSec
    ];
    private readonly IThresholdSettingsProvider _thresholdSettingsProvider;

    public CpuMemoryThresholdSpikeDetector(IThresholdSettingsProvider thresholdSettingsProvider)
    {
        _thresholdSettingsProvider = thresholdSettingsProvider;
    }

    public string Id => "cpu-memory-threshold-spike-detector";
    public IReadOnlySet<EventKind> HandledKinds { get; } = new HashSet<EventKind>
    {
        EventKind.ThresholdBreach,
        EventKind.Spike
    };

    public Task<IReadOnlyList<PerformanceEvent>> DetectAsync(
        EventDetectionInput input,
        CancellationToken cancellationToken = default)
    {
        var latest = input.Samples.LastOrDefault();
        if (latest is null)
        {
            return Task.FromResult<IReadOnlyList<PerformanceEvent>>([]);
        }

        var previous = input.Samples.Count > 1 ? input.Samples[^2] : null;
        var events = new List<PerformanceEvent>();
        var thresholds = input.Sampling.SessionThresholds ?? _thresholdSettingsProvider.ResolveThresholds(input.Target);

        foreach (var key in HandledMetricKeys)
        {
            if (!latest.Values.TryGetValue(key, out var latestValue))
            {
                continue;
            }

            var definition = MetricCatalog.Get(key);
            AddThresholdEvent(input, latest, previous, definition, latestValue, GetConfiguredThreshold(key, thresholds), events);
            AddSpikeEvent(input, latest, previous, definition, latestValue, events);
        }

        return Task.FromResult<IReadOnlyList<PerformanceEvent>>(events);
    }

    private static double? GetConfiguredThreshold(MetricKey key, ThresholdLimitValues thresholds) => key switch
    {
        MetricKey.CpuPercent => thresholds.CpuThresholdPercent,
        MetricKey.MemoryMb => thresholds.RamThresholdMb,
        MetricKey.DiskReadMbPerSec => thresholds.DiskReadThresholdMbPerSec,
        MetricKey.DiskWriteMbPerSec => thresholds.DiskWriteThresholdMbPerSec,
        _ => MetricCatalog.Get(key).WarningThreshold
    };

    private static void AddThresholdEvent(
        EventDetectionInput input,
        MetricSample latest,
        MetricSample? previous,
        MetricDefinition definition,
        double latestValue,
        double? threshold,
        List<PerformanceEvent> events)
    {
        if (threshold is null || latestValue <= threshold.Value)
        {
            return;
        }

        var previousAboveThreshold = previous is not null
            && previous.Values.TryGetValue(definition.Key, out var previousValue)
            && previousValue > threshold.Value;

        var previousAcceptedBreach = input.PreviousEvents.Any(performanceEvent =>
            performanceEvent.MetricKey == definition.Key
            && HasEventKind(performanceEvent, EventKind.ThresholdBreach));

        if (previousAboveThreshold && previousAcceptedBreach)
        {
            return;
        }

        events.Add(new PerformanceEvent
        {
            Id = $"{input.SessionId}_event_threshold_{definition.Key}_{latest.ElapsedMs}",
            SessionId = input.SessionId,
            Kind = EventKind.ThresholdBreach,
            MetricKey = definition.Key,
            Timestamp = latest.Timestamp,
            ElapsedMs = latest.ElapsedMs,
            Severity = EventSeverity.Warning,
            Title = $"{definition.Label} threshold breach",
            Details = $"{definition.Label} crossed configured threshold {threshold.Value:N1} {definition.Unit}.",
            ObservedValue = latestValue,
            ThresholdValue = threshold.Value,
            DetectionProvider = "cpu-memory-threshold-spike-detector"
        });
    }

    private static bool HasEventKind(PerformanceEvent performanceEvent, EventKind kind) =>
        performanceEvent.Kind == kind || performanceEvent.GroupedKinds.Contains(kind);

    private static void AddSpikeEvent(
        EventDetectionInput input,
        MetricSample latest,
        MetricSample? previous,
        MetricDefinition definition,
        double latestValue,
        List<PerformanceEvent> events)
    {
        if (definition.SpikeDelta is null
            || previous is null
            || !previous.Values.TryGetValue(definition.Key, out var previousValue))
        {
            return;
        }

        var delta = latestValue - previousValue;
        if (delta < definition.SpikeDelta.Value)
        {
            return;
        }

        events.Add(new PerformanceEvent
        {
            Id = $"{input.SessionId}_event_spike_{definition.Key}_{latest.ElapsedMs}",
            SessionId = input.SessionId,
            Kind = EventKind.Spike,
            MetricKey = definition.Key,
            Timestamp = latest.Timestamp,
            ElapsedMs = latest.ElapsedMs,
            Severity = EventSeverity.Warning,
            Title = $"{definition.Label} spike",
            Details = $"{definition.Label} rose by {delta:N1} {definition.Unit} since the previous sample.",
            ObservedValue = latestValue,
            ThresholdValue = definition.SpikeDelta.Value,
            DetectionProvider = "cpu-memory-threshold-spike-detector"
        });
    }
}
