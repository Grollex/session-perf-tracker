using System;
using System.Collections.Generic;
using System.Linq;
using SessionPerfTracker.Domain.Metrics;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Domain.Services;
using Xunit;

namespace SessionPerfTracker.UnitTests;

public class SessionComparisonEngineTests
{
    private readonly SessionComparisonEngine _engine = new();

    private static SessionRecord CreateDummySession(string id, double avgCpu, double avgRam, int events, int spikes, int breaches, int hangs, double durationSec)
    {
        var target = new TargetDescriptor
        {
            Id = "test-target",
            DisplayName = "Test Target"
        };

        var metrics = new List<MetricSummary>
        {
            new()
            {
                Key = MetricKey.CpuPercent,
                Label = "CPU",
                Unit = "%",
                Direction = ComparisonDirection.LowerIsBetter,
                Min = 0,
                Avg = avgCpu,
                Max = 100,
                Samples = 10
            },
            new()
            {
                Key = MetricKey.MemoryMb,
                Label = "RAM",
                Unit = "MB",
                Direction = ComparisonDirection.LowerIsBetter,
                Min = 0,
                Avg = avgRam,
                Max = 2048,
                Samples = 10
            }
        };

        var summary = new SessionSummary
        {
            SessionId = id,
            TargetName = "Test Target",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-durationSec),
            EndedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(durationSec),
            SampleCount = 10,
            EventCount = events,
            SpikeCount = spikes,
            ThresholdBreachCount = breaches,
            HangSuspectedCount = hangs,
            Metrics = metrics
        };

        return new SessionRecord
        {
            Id = id,
            Target = target,
            Status = SessionStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-durationSec),
            EndedAt = DateTimeOffset.UtcNow,
            Summary = summary
        };
    }

    [Fact]
    public void Compare_CalculatesCorrectDeltas()
    {
        // Arrange
        var left = CreateDummySession("session-1", 10.0, 512.0, 5, 2, 2, 1, 60.0);
        var right = CreateDummySession("session-2", 20.0, 1024.0, 12, 5, 4, 3, 90.0);

        // Act
        var result = _engine.Compare(left, right);

        // Assert
        Assert.Equal("session-1", result.LeftSessionId);
        Assert.Equal("session-2", result.RightSessionId);
        Assert.Equal(TimeSpan.FromSeconds(30.0), result.DurationDelta);
        Assert.Equal(7, result.EventDelta);
        Assert.Equal(3, result.SpikeDelta);
        Assert.Equal(2, result.ThresholdBreachDelta);
        Assert.Equal(2, result.HangDelta);
    }

    [Fact]
    public void Compare_DeterminesWinnerCorrectly()
    {
        // Arrange
        // Lower CPU and RAM is better. Left has lower CPU/RAM, so Left is the winner.
        var left = CreateDummySession("session-1", 10.0, 500.0, 0, 0, 0, 0, 60.0);
        var right = CreateDummySession("session-2", 15.0, 600.0, 0, 0, 0, 0, 60.0);

        // Act
        var result = _engine.Compare(left, right);

        // Assert
        var cpuComp = result.MetricComparisons.First(m => m.Key == MetricKey.CpuPercent);
        var ramComp = result.MetricComparisons.First(m => m.Key == MetricKey.MemoryMb);

        Assert.Equal("left", cpuComp.Winner);
        Assert.Equal("left", ramComp.Winner);
    }

    [Fact]
    public void Compare_ReturnsTieWhenWithinTolerance()
    {
        // Arrange
        var left = CreateDummySession("session-1", 10.0, 500.001, 0, 0, 0, 0, 60.0);
        var right = CreateDummySession("session-2", 10.005, 500.002, 0, 0, 0, 0, 60.0);

        // Act
        var result = _engine.Compare(left, right);

        // Assert
        var cpuComp = result.MetricComparisons.First(m => m.Key == MetricKey.CpuPercent);
        var ramComp = result.MetricComparisons.First(m => m.Key == MetricKey.MemoryMb);

        Assert.Equal("tie", cpuComp.Winner);
        Assert.Equal("tie", ramComp.Winner);
    }
}
