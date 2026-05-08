using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Metrics;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Events;

public sealed class AntiNoiseEventFilter : IEventNoiseFilter
{
    private readonly IThresholdSettingsProvider _settingsProvider;

    public AntiNoiseEventFilter(IThresholdSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public IReadOnlyList<PerformanceEvent> Filter(EventNoiseFilterInput input)
    {
        if (input.RawEvents.Count == 0)
        {
            return [];
        }

        var settings = _settingsProvider.Current.AntiNoise;
        var startupGraceMs = settings.StartupGraceSeconds * 1000L;
        var candidates = input.RawEvents
            .Where(performanceEvent => performanceEvent.ElapsedMs >= startupGraceMs)
            .Where(performanceEvent => !IsSuppressedBySnapshot(input, performanceEvent))
            .ToArray();

        if (candidates.Length == 0)
        {
            return [];
        }

        var groupedCandidates = GroupRelatedEvents(candidates, settings.GroupingWindowSeconds * 1000L);
        var accepted = new List<PerformanceEvent>();
        foreach (var performanceEvent in groupedCandidates.OrderBy(item => item.ElapsedMs))
        {
            if (IsSuppressedByCooldown(
                performanceEvent,
                input.PreviousAcceptedEvents.Concat(accepted),
                settings.EventCooldownSeconds * 1000L))
            {
                continue;
            }

            accepted.Add(performanceEvent);
        }

        return accepted;
    }

    private static bool IsSuppressedBySnapshot(EventNoiseFilterInput input, PerformanceEvent performanceEvent)
    {
        if (input.SuppressDiskEventsUntilUtc is null
            || performanceEvent.Timestamp > input.SuppressDiskEventsUntilUtc.Value
            || !IsDiskMetric(performanceEvent.MetricKey))
        {
            return false;
        }

        return performanceEvent.Kind is EventKind.Spike or EventKind.ThresholdBreach;
    }

    private static bool IsSuppressedByCooldown(
        PerformanceEvent performanceEvent,
        IEnumerable<PerformanceEvent> previousAcceptedEvents,
        long cooldownMs)
    {
        if (cooldownMs <= 0
            || performanceEvent.MetricKey is null
            || HasEventKind(performanceEvent, EventKind.ThresholdBreach))
        {
            return false;
        }

        var lastMatchingEvent = previousAcceptedEvents
            .Where(previous => previous.MetricKey == performanceEvent.MetricKey
                && SharesAnyKind(previous, performanceEvent))
            .OrderByDescending(previous => previous.ElapsedMs)
            .FirstOrDefault();

        return lastMatchingEvent is not null
            && performanceEvent.ElapsedMs - lastMatchingEvent.ElapsedMs < cooldownMs;
    }

    private static IReadOnlyList<PerformanceEvent> GroupRelatedEvents(
        IReadOnlyList<PerformanceEvent> events,
        long groupingWindowMs)
    {
        if (groupingWindowMs <= 0)
        {
            return events;
        }

        var grouped = new List<PerformanceEvent>();
        var groups = events
            .GroupBy(performanceEvent => new
            {
                performanceEvent.MetricKey,
                Bucket = performanceEvent.MetricKey is null ? performanceEvent.ElapsedMs : performanceEvent.ElapsedMs / groupingWindowMs
            });

        foreach (var group in groups)
        {
            var groupEvents = group.OrderBy(item => item.ElapsedMs).ToArray();
            var kinds = groupEvents
                .SelectMany(GetEventKinds)
                .Distinct()
                .ToArray();

            if (group.Key.MetricKey is null || kinds.Length <= 1)
            {
                grouped.AddRange(groupEvents);
                continue;
            }

            grouped.Add(CreateGroupedEvent(groupEvents, kinds));
        }

        return grouped;
    }

    private static PerformanceEvent CreateGroupedEvent(
        IReadOnlyList<PerformanceEvent> events,
        IReadOnlyList<EventKind> kinds)
    {
        var primary = events.FirstOrDefault(item => HasEventKind(item, EventKind.ThresholdBreach))
            ?? events[0];
        var metric = primary.MetricKey is null ? null : MetricCatalog.Get(primary.MetricKey.Value);
        var kindText = string.Join(" + ", kinds.Select(FormatKind));
        var providerText = string.Join(", ", events.Select(item => item.DetectionProvider).Distinct());

        return primary with
        {
            Id = $"{primary.SessionId}_event_grouped_{primary.MetricKey}_{events.Min(item => item.ElapsedMs)}",
            Kind = HasEventKind(primary, EventKind.ThresholdBreach) || kinds.Contains(EventKind.ThresholdBreach)
                ? EventKind.ThresholdBreach
                : primary.Kind,
            Title = $"{metric?.Label ?? "Metric"} grouped event",
            Details = $"{kindText} happened in the same short window. {string.Join(" ", events.Select(item => item.Details).Distinct())}",
            ObservedValue = events.LastOrDefault(item => item.ObservedValue is not null)?.ObservedValue,
            ThresholdValue = events.FirstOrDefault(item => item.Kind == EventKind.ThresholdBreach)?.ThresholdValue ?? primary.ThresholdValue,
            Severity = events.Max(item => item.Severity),
            GroupedKinds = kinds,
            NoisePolicy = "Grouped by anti-noise filter",
            DetectionProvider = string.IsNullOrWhiteSpace(providerText)
                ? "anti-noise-event-filter"
                : $"{providerText}; anti-noise-event-filter"
        };
    }

    private static bool SharesAnyKind(PerformanceEvent left, PerformanceEvent right) =>
        GetEventKinds(left).Intersect(GetEventKinds(right)).Any();

    private static bool HasEventKind(PerformanceEvent performanceEvent, EventKind kind) =>
        performanceEvent.Kind == kind || performanceEvent.GroupedKinds.Contains(kind);

    private static IEnumerable<EventKind> GetEventKinds(PerformanceEvent performanceEvent) =>
        performanceEvent.GroupedKinds.Count > 0
            ? performanceEvent.GroupedKinds
            : [performanceEvent.Kind];

    private static bool IsDiskMetric(MetricKey? key) =>
        key is MetricKey.DiskReadMbPerSec or MetricKey.DiskWriteMbPerSec;

    private static string FormatKind(EventKind kind) => kind switch
    {
        EventKind.ThresholdBreach => "threshold breach",
        EventKind.Spike => "spike",
        EventKind.HangSuspected => "hang suspected",
        EventKind.LongStartup => "long startup",
        EventKind.ExternalExit => "external close",
        EventKind.UnexpectedExit => "unexpected exit",
        EventKind.CrashLikeExit => "crash-like exit",
        _ => kind.ToString()
    };
}
