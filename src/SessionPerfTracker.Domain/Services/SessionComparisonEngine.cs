using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Metrics;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Domain.Services;

public sealed class SessionComparisonEngine : IComparisonEngine
{
    public SessionComparisonResult Compare(SessionRecord left, SessionRecord right)
    {
        return new SessionComparisonResult
        {
            LeftSessionId = left.Id,
            RightSessionId = right.Id,
            GeneratedAt = DateTimeOffset.UtcNow,
            DurationDelta = right.Summary.Duration - left.Summary.Duration,
            EventDelta = right.Summary.EventCount - left.Summary.EventCount,
            SpikeDelta = right.Summary.SpikeCount - left.Summary.SpikeCount,
            ThresholdBreachDelta = right.Summary.ThresholdBreachCount - left.Summary.ThresholdBreachCount,
            HangDelta = right.Summary.HangSuspectedCount - left.Summary.HangSuspectedCount,
            MetricComparisons = MetricCatalog.All
                .Select(metric => CompareMetric(
                    metric,
                    left.Summary.Metrics.FirstOrDefault(item => item.Key == metric.Key),
                    right.Summary.Metrics.FirstOrDefault(item => item.Key == metric.Key)))
                .ToArray()
        };
    }

    private static MetricComparison CompareMetric(MetricDefinition definition, MetricSummary? left, MetricSummary? right)
    {
        if (left is null || right is null)
        {
            return new MetricComparison
            {
                Key = definition.Key,
                Label = definition.Label,
                Unit = definition.Unit,
                Direction = definition.Direction,
                Left = left,
                Right = right,
                Winner = "unknown"
            };
        }

        var avgDelta = right.Avg - left.Avg;
        var avgDeltaPercent = Math.Abs(left.Avg) < 0.0001 ? (double?)null : avgDelta / left.Avg * 100;
        var maxDelta = right.Max - left.Max;

        return new MetricComparison
        {
            Key = definition.Key,
            Label = definition.Label,
            Unit = definition.Unit,
            Direction = definition.Direction,
            Left = left,
            Right = right,
            AvgDelta = avgDelta,
            AvgDeltaPercent = avgDeltaPercent,
            MaxDelta = maxDelta,
            Winner = GetWinner(left, right)
        };
    }

    private static string GetWinner(MetricSummary left, MetricSummary right)
    {
        const double tolerance = 0.01;
        if (Math.Abs(left.Avg - right.Avg) <= tolerance)
        {
            return "tie";
        }

        return left.Direction switch
        {
            ComparisonDirection.HigherIsBetter => left.Avg > right.Avg ? "left" : "right",
            ComparisonDirection.LowerIsBetter => left.Avg < right.Avg ? "left" : "right",
            _ => "unknown"
        };
    }
}
