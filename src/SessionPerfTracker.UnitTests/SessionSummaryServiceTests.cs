using System;
using System.Collections.Generic;
using System.Linq;
using SessionPerfTracker.Domain.Metrics;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Services;
using Xunit;

namespace SessionPerfTracker.UnitTests;

public class SessionSummaryServiceTests
{
    private readonly SessionSummaryService _service = new();

    private static SessionRecordWithoutSummary CreateDummySessionWithoutSummary(
        string id, 
        SessionStatus status, 
        DateTimeOffset startedAt, 
        DateTimeOffset? endedAt, 
        List<MetricSample> samples, 
        List<PerformanceEvent> events)
    {
        var target = new TargetDescriptor
        {
            Id = "test-target",
            DisplayName = "Test Target"
        };

        return new SessionRecordWithoutSummary
        {
            Id = id,
            Target = target,
            Status = status,
            StartedAt = startedAt,
            EndedAt = endedAt,
            Sampling = new SamplingSettings(),
            Capabilities = new MetricCapabilities
            {
                CpuPercent = MetricReliability.Stable,
                MemoryMb = MetricReliability.Stable
            },
            Samples = samples,
            Events = events
        };
    }

    [Fact]
    public void Summarize_CalculatesCorrectBasicDeltasAndDuration()
    {
        // Arrange
        var startedAt = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);
        var endedAt = startedAt.AddMinutes(10);
        var samples = new List<MetricSample>
        {
            new() { Id = "s1", SessionId = "sess-1", Timestamp = startedAt, ElapsedMs = 0, Values = new Dictionary<MetricKey, double> { { MetricKey.CpuPercent, 10.0 } } },
            new() { Id = "s2", SessionId = "sess-1", Timestamp = endedAt, ElapsedMs = 600000, Values = new Dictionary<MetricKey, double> { { MetricKey.CpuPercent, 20.0 } } }
        };
        var events = new List<PerformanceEvent>
        {
            new() { Id = "e1", SessionId = "sess-1", Kind = EventKind.Spike, Title = "CPU Spike", Details = "CPU peaked", DetectionProvider = "test" }
        };
        var session = CreateDummySessionWithoutSummary("sess-1", SessionStatus.Completed, startedAt, endedAt, samples, events);

        // Act
        var summary = _service.Summarize(session);

        // Assert
        Assert.Equal("sess-1", summary.SessionId);
        Assert.Equal(TimeSpan.FromMinutes(10), summary.Duration);
        Assert.Equal(2, summary.SampleCount);
        Assert.Equal(1, summary.EventCount);
        Assert.Equal(1, summary.SpikeCount);
        Assert.Equal(0, summary.ThresholdBreachCount);
    }

    [Fact]
    public void Summarize_ResolvesExitKindCorrectly()
    {
        // Arrange & Act & Assert
        // 1. Running
        var sessRunning = CreateDummySessionWithoutSummary("s", SessionStatus.Running, DateTimeOffset.UtcNow, null, [], []);
        Assert.Equal(SessionExitKind.Running, _service.Summarize(sessRunning).ExitKind);

        // 2. Stopped by user
        var sessStopped = CreateDummySessionWithoutSummary("s", SessionStatus.Stopped, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], []);
        Assert.Equal(SessionExitKind.NormalStop, _service.Summarize(sessStopped).ExitKind);

        // 3. Completed
        var sessCompleted = CreateDummySessionWithoutSummary("s", SessionStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], []);
        Assert.Equal(SessionExitKind.Completed, _service.Summarize(sessCompleted).ExitKind);

        // 4. CrashLikeExit
        var sessCrashed = CreateDummySessionWithoutSummary("s", SessionStatus.CrashLikeExit, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], []);
        Assert.Equal(SessionExitKind.CrashLikeExit, _service.Summarize(sessCrashed).ExitKind);
    }

    [Fact]
    public void Summarize_ResolvesStabilityCorrectly()
    {
        // Arrange & Act & Assert
        // 1. Stable
        var sessStable = CreateDummySessionWithoutSummary("s", SessionStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], []);
        Assert.Equal(StabilityStatus.Stable, _service.Summarize(sessStable).StabilityStatus);

        // 2. Hung (if HangSuspectedCount > 0)
        var eventsHung = new List<PerformanceEvent>
        {
            new() { Id = "e1", SessionId = "s", Kind = EventKind.HangSuspected, Title = "Hang", Details = "App hung", DetectionProvider = "test" }
        };
        var sessHung = CreateDummySessionWithoutSummary("s", SessionStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], eventsHung);
        Assert.Equal(StabilityStatus.Hung, _service.Summarize(sessHung).StabilityStatus);

        // 3. Crashed
        var sessCrashed = CreateDummySessionWithoutSummary("s", SessionStatus.CrashLikeExit, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], []);
        Assert.Equal(StabilityStatus.Crashed, _service.Summarize(sessCrashed).StabilityStatus);
    }

    [Fact]
    public void Summarize_CalculatesMetricAveragesCorrectly()
    {
        // Arrange
        var startedAt = DateTimeOffset.UtcNow;
        var samples = new List<MetricSample>
        {
            new() { Id = "s1", SessionId = "sess-1", Values = new Dictionary<MetricKey, double> { { MetricKey.CpuPercent, 10.0 }, { MetricKey.MemoryMb, 100.0 } } },
            new() { Id = "s2", SessionId = "sess-1", Values = new Dictionary<MetricKey, double> { { MetricKey.CpuPercent, 30.0 }, { MetricKey.MemoryMb, 200.0 } } }
        };
        var session = CreateDummySessionWithoutSummary("sess-1", SessionStatus.Completed, startedAt, startedAt, samples, []);

        // Act
        var summary = _service.Summarize(session);

        // Assert
        var cpuSummary = summary.Metrics.First(m => m.Key == MetricKey.CpuPercent);
        Assert.Equal(10.0, cpuSummary.Min);
        Assert.Equal(20.0, cpuSummary.Avg);
        Assert.Equal(30.0, cpuSummary.Max);

        var ramSummary = summary.Metrics.First(m => m.Key == MetricKey.MemoryMb);
        Assert.Equal(100.0, ramSummary.Min);
        Assert.Equal(150.0, ramSummary.Avg);
        Assert.Equal(200.0, ramSummary.Max);
    }
}
