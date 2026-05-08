using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Metrics;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Domain.Services;

public sealed class SessionSummaryService : ISessionSummaryService
{
    public SessionSummary Summarize(SessionRecordWithoutSummary session)
    {
        var duration = GetDuration(session.StartedAt, session.EndedAt);
        var metrics = MetricCatalog.All
            .Select(metric => SummarizeMetric(metric, session))
            .Where(metric => metric is not null)
            .Select(metric => metric!)
            .ToArray();

        var spikeCount = CountEvents(session.Events, EventKind.Spike);
        var thresholdBreachCount = CountEvents(session.Events, EventKind.ThresholdBreach);
        var hangSuspectedCount = CountEvents(session.Events, EventKind.HangSuspected);
        var crashLikeExit = session.Status == SessionStatus.CrashLikeExit || CountEvents(session.Events, EventKind.CrashLikeExit) > 0;
        var unexpectedExit = session.Status == SessionStatus.UnexpectedExit || CountEvents(session.Events, EventKind.UnexpectedExit) > 0;
        var longStartupMs = session.Events.FirstOrDefault(item => item.Kind == EventKind.LongStartup)?.ObservedValue is { } value
            ? Convert.ToInt32(value)
            : (int?)null;
        var exit = ResolveExitKind(session.Status, session.Events);
        var stability = ResolveStabilityStatus(
            crashLikeExit,
            unexpectedExit,
            hangSuspectedCount,
            thresholdBreachCount,
            spikeCount,
            longStartupMs);

        return new SessionSummary
        {
            SessionId = session.Id,
            TargetName = session.Target.DisplayName,
            StartedAt = session.StartedAt,
            EndedAt = session.EndedAt,
            Duration = duration,
            SampleCount = session.Samples.Count,
            EventCount = session.Events.Count,
            SpikeCount = spikeCount,
            ThresholdBreachCount = thresholdBreachCount,
            HangSuspectedCount = hangSuspectedCount,
            CrashLikeExit = crashLikeExit,
            LongStartupMs = longStartupMs,
            ExitKind = exit.Kind,
            ExitReason = exit.Reason,
            StabilityStatus = stability.Status,
            StabilityReason = stability.Reason,
            Metrics = metrics
        };
    }

    private static (SessionExitKind Kind, string Reason) ResolveExitKind(
        SessionStatus status,
        IReadOnlyList<PerformanceEvent> events)
    {
        if (status == SessionStatus.Running)
        {
            return (SessionExitKind.Running, "Session is still recording.");
        }

        if (status == SessionStatus.Stopped)
        {
            return (SessionExitKind.NormalStop, "Monitoring was stopped from SessionPerfTracker.");
        }

        var crashEvent = events.FirstOrDefault(item => HasEventKind(item, EventKind.CrashLikeExit));
        if (status == SessionStatus.CrashLikeExit || crashEvent is not null)
        {
            return (SessionExitKind.CrashLikeExit, crashEvent?.Details ?? "Best-effort crash-like exit signal was recorded.");
        }

        var unexpectedEvent = events.FirstOrDefault(item => HasEventKind(item, EventKind.UnexpectedExit));
        if (status == SessionStatus.UnexpectedExit || unexpectedEvent is not null)
        {
            return (SessionExitKind.UnexpectedExit, unexpectedEvent?.Details ?? "Target exited without a known normal signal.");
        }

        var externalEvent = events.FirstOrDefault(item => HasEventKind(item, EventKind.ExternalExit));
        if (status == SessionStatus.ExternalExit || externalEvent is not null)
        {
            return (SessionExitKind.ExternalClose, externalEvent?.Details ?? "Target closed outside SessionPerfTracker.");
        }

        if (status == SessionStatus.Completed)
        {
            return (SessionExitKind.Completed, "Target completed normally.");
        }

        return (SessionExitKind.Unknown, "Exit classification is not available.");
    }

    private static (StabilityStatus Status, string Reason) ResolveStabilityStatus(
        bool crashLikeExit,
        bool unexpectedExit,
        int hangSuspectedCount,
        int thresholdBreachCount,
        int spikeCount,
        int? longStartupMs)
    {
        if (crashLikeExit)
        {
            return (StabilityStatus.Crashed, "Best-effort crash-like exit signal was recorded.");
        }

        if (hangSuspectedCount > 0)
        {
            return (StabilityStatus.Hung, "Best-effort not responding event was detected.");
        }

        if (unexpectedExit)
        {
            return (StabilityStatus.Degraded, "Target exited unexpectedly, but no crash-like signal was recorded.");
        }

        if (thresholdBreachCount > 0 || spikeCount > 0 || longStartupMs is not null)
        {
            return (StabilityStatus.Degraded, "Performance or startup stability events were detected.");
        }

        return (StabilityStatus.Stable, "No stability events were recorded.");
    }

    private static MetricSummary? SummarizeMetric(MetricDefinition definition, SessionRecordWithoutSummary session)
    {
        var values = session.Samples
            .Select(sample => sample.Values.TryGetValue(definition.Key, out var value) ? value : (double?)null)
            .Where(value => value.HasValue && double.IsFinite(value.Value))
            .Select(value => value!.Value)
            .ToArray();

        if (values.Length == 0)
        {
            return null;
        }

        return new MetricSummary
        {
            Key = definition.Key,
            Label = definition.Label,
            Unit = definition.Unit,
            Direction = definition.Direction,
            Min = values.Min(),
            Avg = values.Average(),
            Max = values.Max(),
            Samples = values.Length,
            Spikes = session.Events.Count(item => item.MetricKey == definition.Key && HasEventKind(item, EventKind.Spike)),
            ThresholdBreaches = session.Events.Count(item => item.MetricKey == definition.Key && HasEventKind(item, EventKind.ThresholdBreach)),
            Reliability = GetReliability(definition.Key, session)
        };
    }

    private static MetricReliability GetReliability(MetricKey key, SessionRecordWithoutSummary session)
    {
        var sampleReliability = session.Samples
            .Select(sample => sample.SourceReliability.TryGetValue(key, out var reliability) ? reliability : (MetricReliability?)null)
            .FirstOrDefault(reliability => reliability.HasValue);

        return sampleReliability ?? session.Capabilities.For(key);
    }

    private static int CountEvents(IReadOnlyList<PerformanceEvent> events, EventKind kind) =>
        events.Count(item => HasEventKind(item, kind));

    private static bool HasEventKind(PerformanceEvent performanceEvent, EventKind kind) =>
        performanceEvent.Kind == kind || performanceEvent.GroupedKinds.Contains(kind);

    private static TimeSpan GetDuration(DateTimeOffset startedAt, DateTimeOffset? endedAt)
    {
        var end = endedAt ?? DateTimeOffset.UtcNow;
        var duration = end - startedAt;
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }
}
