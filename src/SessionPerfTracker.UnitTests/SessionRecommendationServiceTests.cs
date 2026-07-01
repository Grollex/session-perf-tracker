using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Domain.Services;
using Xunit;

namespace SessionPerfTracker.UnitTests;

public sealed class SessionRecommendationServiceTests
{
    private readonly SessionSummaryService _summaryService = new();
    private readonly SessionRecommendationService _recommendationService = new();

    [Fact]
    public void Recommend_ReturnsBaselineAdviceForStableSession()
    {
        var session = CreateSession(
            samples:
            [
                Sample(0, cpu: 12, ram: 200),
                Sample(1000, cpu: 18, ram: 215),
                Sample(2000, cpu: 15, ram: 210),
                Sample(3000, cpu: 14, ram: 208),
                Sample(4000, cpu: 13, ram: 212),
                Sample(5000, cpu: 16, ram: 214)
            ]);

        var recommendations = _recommendationService.Recommend(session);

        var recommendation = Assert.Single(recommendations);
        Assert.Equal("session-looks-stable", recommendation.Id);
        Assert.Equal(SessionRecommendationSeverity.Info, recommendation.Severity);
    }

    [Fact]
    public void Recommend_FlagsCpuPressureWithChildProcessAdvice()
    {
        var session = CreateSession(
            samples:
            [
                Sample(0, cpu: 72, ram: 500),
                Sample(1000, cpu: 88, ram: 520),
                Sample(2000, cpu: 94, ram: 530)
            ],
            events:
            [
                Event("cpu-breach", EventKind.ThresholdBreach, MetricKey.CpuPercent, observed: 94, threshold: 80)
            ]);

        var recommendations = _recommendationService.Recommend(session);

        Assert.Contains(recommendations, item => item.Id == "cpu-pressure"
            && item.Severity == SessionRecommendationSeverity.Warning
            && item.Recommendation.Contains("child processes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Recommend_FlagsMemoryGrowthAsLeakSignal()
    {
        var session = CreateSession(
            samples:
            [
                Sample(0, cpu: 20, ram: 400),
                Sample(1000, cpu: 21, ram: 450),
                Sample(2000, cpu: 22, ram: 900),
                Sample(3000, cpu: 23, ram: 1100),
                Sample(4000, cpu: 20, ram: 1250),
                Sample(5000, cpu: 19, ram: 1400)
            ]);

        var recommendations = _recommendationService.Recommend(session);

        Assert.Contains(recommendations, item => item.Id == "memory-pressure"
            && item.Title.Contains("climbs", StringComparison.OrdinalIgnoreCase)
            && item.Evidence?.Contains("estimated growth", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Recommend_PrioritizesCrashAndHangBeforeMetricAdvice()
    {
        var session = CreateSession(
            status: SessionStatus.CrashLikeExit,
            samples:
            [
                Sample(0, cpu: 91, ram: 600),
                Sample(1000, cpu: 95, ram: 650)
            ],
            events:
            [
                Event("hang", EventKind.HangSuspected),
                Event("cpu-breach", EventKind.ThresholdBreach, MetricKey.CpuPercent, observed: 95, threshold: 80)
            ]);

        var recommendations = _recommendationService.Recommend(session);

        Assert.Equal("crash-like-exit", recommendations[0].Id);
        Assert.Equal("hang-suspected", recommendations[1].Id);
    }

    private SessionRecord CreateSession(
        SessionStatus status = SessionStatus.Completed,
        IReadOnlyList<MetricSample>? samples = null,
        IReadOnlyList<PerformanceEvent>? events = null,
        bool includeChildProcesses = false)
    {
        var startedAt = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
        var endedAt = startedAt.AddSeconds(Math.Max(1, samples?.Count ?? 1));
        var withoutSummary = new SessionRecordWithoutSummary
        {
            Id = "session-1",
            Target = new TargetDescriptor
            {
                Id = "target-1",
                DisplayName = "Target App",
                IncludeChildProcesses = includeChildProcesses
            },
            Status = status,
            StartedAt = startedAt,
            EndedAt = endedAt,
            Capabilities = new MetricCapabilities
            {
                CpuPercent = MetricReliability.Stable,
                MemoryMb = MetricReliability.Stable,
                DiskReadMbPerSec = MetricReliability.BestEffort,
                DiskWriteMbPerSec = MetricReliability.BestEffort
            },
            Samples = samples ?? [],
            Events = events ?? []
        };

        return withoutSummary.WithSummary(_summaryService.Summarize(withoutSummary));
    }

    private static MetricSample Sample(
        long elapsedMs,
        double cpu,
        double ram,
        double read = 0,
        double write = 0) => new()
        {
            Id = $"sample-{elapsedMs}",
            SessionId = "session-1",
            Timestamp = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero).AddMilliseconds(elapsedMs),
            ElapsedMs = elapsedMs,
            Values = new Dictionary<MetricKey, double>
            {
                [MetricKey.CpuPercent] = cpu,
                [MetricKey.MemoryMb] = ram,
                [MetricKey.DiskReadMbPerSec] = read,
                [MetricKey.DiskWriteMbPerSec] = write
            },
            SourceReliability = new Dictionary<MetricKey, MetricReliability>
            {
                [MetricKey.CpuPercent] = MetricReliability.Stable,
                [MetricKey.MemoryMb] = MetricReliability.Stable,
                [MetricKey.DiskReadMbPerSec] = MetricReliability.BestEffort,
                [MetricKey.DiskWriteMbPerSec] = MetricReliability.BestEffort
            }
        };

    private static PerformanceEvent Event(
        string id,
        EventKind kind,
        MetricKey? metricKey = null,
        double? observed = null,
        double? threshold = null) => new()
        {
            Id = id,
            SessionId = "session-1",
            Kind = kind,
            MetricKey = metricKey,
            Timestamp = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
            Title = kind.ToString(),
            Details = "Test event",
            ObservedValue = observed,
            ThresholdValue = threshold,
            DetectionProvider = "test"
        };
}
