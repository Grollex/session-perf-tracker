using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.MockData;

public static class MockSessionFactory
{
    private static readonly MetricCapabilities DefaultCapabilities = new()
    {
        CpuPercent = MetricReliability.Stable,
        MemoryMb = MetricReliability.Stable,
        GpuPercent = MetricReliability.BestEffort,
        DiskReadMbPerSec = MetricReliability.BestEffort,
        DiskWriteMbPerSec = MetricReliability.BestEffort,
        TemperatureC = MetricReliability.Unavailable
    };

    public static IReadOnlyList<SessionRecord> Create(ISessionSummaryService summaryService) =>
    [
        Build(summaryService, new MockInput(
            Id: "session_mock_editor_baseline",
            Name: "PhotoForge.exe",
            StartedAt: new DateTimeOffset(2026, 04, 29, 08, 12, 00, TimeSpan.Zero),
            DurationMinutes: 14,
            CpuBase: 22,
            CpuSpike: 82,
            MemoryBase: 880,
            MemoryPeak: 1320,
            GpuBase: 18,
            DiskBase: 7,
            IssueMultiplier: 1.0)),
        Build(summaryService, new MockInput(
            Id: "session_mock_editor_regression",
            Name: "PhotoForge.exe",
            StartedAt: new DateTimeOffset(2026, 04, 29, 09, 05, 00, TimeSpan.Zero),
            DurationMinutes: 16,
            CpuBase: 32,
            CpuSpike: 94,
            MemoryBase: 1180,
            MemoryPeak: 2140,
            GpuBase: 31,
            DiskBase: 12,
            IssueMultiplier: 1.8))
    ];

    private static SessionRecord Build(ISessionSummaryService summaryService, MockInput input)
    {
        var endedAt = input.StartedAt.AddMinutes(input.DurationMinutes);
        var target = new TargetDescriptor
        {
            Id = $"{input.Id}_target",
            Kind = TargetSelectionKind.Executable,
            LifecycleMode = TargetLifecycleMode.LaunchAndTrack,
            DisplayName = input.Name,
            ExecutablePath = $@"C:\Apps\{input.Name}",
            IncludeChildProcesses = false,
            ScopeMode = ProcessScopeMode.RootOnly
        };

        var samples = BuildSamples(input).ToArray();
        var events = BuildEvents(input, samples).ToArray();
        var withoutSummary = new SessionRecordWithoutSummary
        {
            Id = input.Id,
            Target = target,
            Status = SessionStatus.Completed,
            StartedAt = input.StartedAt,
            EndedAt = endedAt,
            Sampling = new SamplingSettings
            {
                IntervalMs = 1000,
                LiveUiRefreshMs = 2000,
                StorageBatchSize = 10
            },
            Capabilities = DefaultCapabilities,
            Samples = samples,
            Events = events,
            Notes = "Mock session for WPF foundation UI."
        };

        return withoutSummary.WithSummary(summaryService.Summarize(withoutSummary));
    }

    private static IEnumerable<MetricSample> BuildSamples(MockInput input)
    {
        var count = input.DurationMinutes * 60;

        for (var index = 0; index < count; index++)
        {
            var elapsedMs = index * 1000L;
            var wave = Math.Sin(index / 11.0) * 5;
            var burst = index > count * 0.45 && index < count * 0.58;
            var lateBurst = index > count * 0.74 && index < count * 0.79;

            yield return new MetricSample
            {
                Id = $"{input.Id}_sample_{index}",
                SessionId = input.Id,
                Timestamp = input.StartedAt.AddMilliseconds(elapsedMs),
                ElapsedMs = elapsedMs,
                RootProcessId = 4200,
                ProcessCount = 1,
                Values = new Dictionary<MetricKey, double>
                {
                    [MetricKey.CpuPercent] = Clamp(input.CpuBase + wave + (burst ? input.CpuSpike - input.CpuBase : 0), 0, 100),
                    [MetricKey.MemoryMb] = Math.Round(input.MemoryBase + index * input.IssueMultiplier + (lateBurst ? input.MemoryPeak / 3 : 0)),
                    [MetricKey.GpuPercent] = Clamp(input.GpuBase + Math.Cos(index / 17.0) * 7 + (burst ? 25 : 0), 0, 100),
                    [MetricKey.DiskReadMbPerSec] = Math.Max(0, input.DiskBase + Math.Sin(index / 9.0) * 4 + (lateBurst ? 75 : 0)),
                    [MetricKey.DiskWriteMbPerSec] = Math.Max(0, input.DiskBase / 2 + Math.Cos(index / 13.0) * 3 + (lateBurst ? 42 : 0))
                },
                SourceReliability = new Dictionary<MetricKey, MetricReliability>
                {
                    [MetricKey.GpuPercent] = MetricReliability.BestEffort,
                    [MetricKey.DiskReadMbPerSec] = MetricReliability.BestEffort,
                    [MetricKey.DiskWriteMbPerSec] = MetricReliability.BestEffort,
                    [MetricKey.TemperatureC] = MetricReliability.Unavailable
                }
            };
        }
    }

    private static IEnumerable<PerformanceEvent> BuildEvents(MockInput input, IReadOnlyList<MetricSample> samples)
    {
        var cpuSpike = samples.FirstOrDefault(sample => sample.Values.GetValueOrDefault(MetricKey.CpuPercent) > 80);
        var diskSpike = samples.FirstOrDefault(sample => sample.Values.GetValueOrDefault(MetricKey.DiskReadMbPerSec) > 60);

        if (cpuSpike is not null)
        {
            yield return new PerformanceEvent
            {
                Id = $"{input.Id}_event_cpu_spike",
                SessionId = input.Id,
                Kind = EventKind.Spike,
                MetricKey = MetricKey.CpuPercent,
                Timestamp = cpuSpike.Timestamp,
                ElapsedMs = cpuSpike.ElapsedMs,
                Severity = input.IssueMultiplier > 1.5 ? EventSeverity.Critical : EventSeverity.Warning,
                Title = "CPU spike",
                Details = "Mock spike event. Future provider can attach process tree context here.",
                ObservedValue = cpuSpike.Values[MetricKey.CpuPercent],
                ThresholdValue = 80,
                DetectionProvider = "mock-spike-detector",
                Context = new SpikeContextSnapshot
                {
                    CapturedAt = cpuSpike.Timestamp,
                    WindowMsBefore = 5000,
                    WindowMsAfter = 5000,
                    Note = "Reserved for future spike context snapshot provider."
                }
            };
        }

        if (diskSpike is not null)
        {
            yield return new PerformanceEvent
            {
                Id = $"{input.Id}_event_disk_breach",
                SessionId = input.Id,
                Kind = EventKind.ThresholdBreach,
                MetricKey = MetricKey.DiskReadMbPerSec,
                Timestamp = diskSpike.Timestamp,
                ElapsedMs = diskSpike.ElapsedMs,
                Severity = EventSeverity.Warning,
                Title = "Disk read threshold breach",
                Details = "Mock threshold breach for compare UI.",
                ObservedValue = diskSpike.Values[MetricKey.DiskReadMbPerSec],
                ThresholdValue = 60,
                DetectionProvider = "mock-threshold-detector"
            };
        }

        if (input.IssueMultiplier > 1.5)
        {
            var hangSample = samples[(int)(samples.Count * 0.56)];
            yield return new PerformanceEvent
            {
                Id = $"{input.Id}_event_hang_suspected",
                SessionId = input.Id,
                Kind = EventKind.HangSuspected,
                Timestamp = hangSample.Timestamp,
                ElapsedMs = hangSample.ElapsedMs,
                Severity = EventSeverity.Warning,
                Title = "Hang suspected",
                Details = "Placeholder event. Reliable hang detection is intentionally behind IHangDetector.",
                DetectionProvider = "mock-hang-detector"
            };
        }
    }

    private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));

    private sealed record MockInput(
        string Id,
        string Name,
        DateTimeOffset StartedAt,
        int DurationMinutes,
        double CpuBase,
        double CpuSpike,
        double MemoryBase,
        double MemoryPeak,
        double GpuBase,
        double DiskBase,
        double IssueMultiplier);
}
