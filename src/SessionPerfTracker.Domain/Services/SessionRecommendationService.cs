using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Domain.Services;

public sealed class SessionRecommendationService
{
    private const double HighCpuAveragePercent = 70;
    private const double HighCpuPeakPercent = 90;
    private const double HighMemoryMb = 4096;
    private const double SignificantMemoryGrowthMb = 512;
    private const double HighDiskReadMbPerSec = 120;
    private const double HighDiskWriteMbPerSec = 80;
    private const int LongStartupAttentionMs = 15_000;

    public IReadOnlyList<SessionRecommendation> Recommend(SessionRecord session)
    {
        var recommendations = new List<SessionRecommendation>();

        AddStabilityRecommendations(session, recommendations);
        AddMetricRecommendations(session, recommendations);
        AddCaptureQualityRecommendations(session, recommendations);

        if (recommendations.Count == 0)
        {
            recommendations.Add(new SessionRecommendation
            {
                Id = "session-looks-stable",
                Severity = SessionRecommendationSeverity.Info,
                Title = "No obvious bottleneck found",
                Recommendation = "Keep this session as a baseline and compare future runs against it after app updates or settings changes.",
                Evidence = $"Recorded {session.Summary.SampleCount:N0} samples with no stability events."
            });
        }

        return recommendations
            .OrderBy(item => item.Severity == SessionRecommendationSeverity.Critical ? 0 : item.Severity == SessionRecommendationSeverity.Warning ? 1 : 2)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(6)
            .ToArray();
    }

    private static void AddStabilityRecommendations(
        SessionRecord session,
        List<SessionRecommendation> recommendations)
    {
        if (session.Summary.CrashLikeExit)
        {
            recommendations.Add(new SessionRecommendation
            {
                Id = "crash-like-exit",
                Severity = SessionRecommendationSeverity.Critical,
                Title = "Treat this run as a crash investigation",
                Recommendation = "Re-run the same target with child processes enabled, then compare the new session. If the exit repeats with similar timing, collect the exported report and check the target app logs around the recorded exit time.",
                Evidence = session.Summary.ExitReason ?? "A crash-like exit was recorded."
            });
        }
        else if (session.Summary.ExitKind == SessionExitKind.UnexpectedExit)
        {
            recommendations.Add(new SessionRecommendation
            {
                Id = "unexpected-exit",
                Severity = SessionRecommendationSeverity.Warning,
                Title = "Verify why the target closed",
                Recommendation = "If the close was not intentional, repeat the run and watch the last event before exit. A matching final CPU, RAM, or disk spike is a stronger lead than the exit signal alone.",
                Evidence = session.Summary.ExitReason ?? "The target exited without a normal stop signal."
            });
        }

        if (session.Summary.HangSuspectedCount > 0)
        {
            recommendations.Add(new SessionRecommendation
            {
                Id = "hang-suspected",
                Severity = SessionRecommendationSeverity.Critical,
                Title = "Investigate responsiveness, not only resource usage",
                Recommendation = "Check the event context for nearby top CPU, RAM, and disk processes. If resource usage is low during the hang, the target may be blocked on UI, I/O, network, or a child process instead of raw CPU pressure.",
                Evidence = $"{session.Summary.HangSuspectedCount:N0} not-responding event(s) were recorded."
            });
        }

        if (session.Summary.LongStartupMs is >= LongStartupAttentionMs)
        {
            recommendations.Add(new SessionRecommendation
            {
                Id = "long-startup",
                Severity = SessionRecommendationSeverity.Warning,
                Title = "Separate startup cost from runtime cost",
                Recommendation = "Record one startup-focused run and one already-running attach run. If only startup is slow, focus on launch-time plug-ins, disk reads, updates, and first-load caches.",
                Evidence = $"Startup readiness was delayed for {TimeSpan.FromMilliseconds(session.Summary.LongStartupMs.Value).TotalSeconds:N1}s."
            });
        }
    }

    private static void AddMetricRecommendations(
        SessionRecord session,
        List<SessionRecommendation> recommendations)
    {
        var cpu = FindMetric(session, MetricKey.CpuPercent);
        if (cpu is not null && (cpu.Avg >= HighCpuAveragePercent || cpu.Max >= HighCpuPeakPercent || cpu.ThresholdBreaches > 0))
        {
            recommendations.Add(new SessionRecommendation
            {
                Id = "cpu-pressure",
                Severity = cpu.ThresholdBreaches > 0 || cpu.Avg >= HighCpuAveragePercent
                    ? SessionRecommendationSeverity.Warning
                    : SessionRecommendationSeverity.Info,
                MetricKey = MetricKey.CpuPercent,
                Title = "CPU is the first suspect",
                Recommendation = "Repeat with child processes enabled and compare the CPU average. If the child-process run is much higher, the slowdown is probably in a helper process rather than the main executable.",
                Evidence = $"CPU avg {cpu.Avg:N1}%, peak {cpu.Max:N1}%, breaches {cpu.ThresholdBreaches:N0}, spikes {cpu.Spikes:N0}."
            });
        }

        var memory = FindMetric(session, MetricKey.MemoryMb);
        if (memory is not null)
        {
            var memoryGrowth = EstimateGrowth(session, MetricKey.MemoryMb);
            if (memory.Max >= HighMemoryMb || memory.ThresholdBreaches > 0 || memoryGrowth >= SignificantMemoryGrowthMb)
            {
                recommendations.Add(new SessionRecommendation
                {
                    Id = "memory-pressure",
                    Severity = memory.ThresholdBreaches > 0 || memoryGrowth >= SignificantMemoryGrowthMb
                        ? SessionRecommendationSeverity.Warning
                        : SessionRecommendationSeverity.Info,
                    MetricKey = MetricKey.MemoryMb,
                    Title = memoryGrowth >= SignificantMemoryGrowthMb ? "RAM climbs during the run" : "RAM usage deserves attention",
                    Recommendation = memoryGrowth >= SignificantMemoryGrowthMb
                        ? "Run a longer session and compare the first third against the final third. A steady climb is a stronger leak signal than one high peak."
                        : "Check whether the target is expected to hold this much memory. For browser, game, or launcher targets, repeat with child processes enabled before blaming the root process.",
                    Evidence = $"RAM avg {memory.Avg:N0} MB, peak {memory.Max:N0} MB, estimated growth {memoryGrowth:N0} MB."
                });
            }
        }

        AddDiskRecommendation(
            session,
            recommendations,
            MetricKey.DiskReadMbPerSec,
            "disk-read-pressure",
            "Disk reads are a likely source of stalls",
            "If this happens near startup or loading screens, compare a second run after caches are warm. If reads stay high, check antivirus scans, game asset loading, or a slow drive.",
            HighDiskReadMbPerSec);

        AddDiskRecommendation(
            session,
            recommendations,
            MetricKey.DiskWriteMbPerSec,
            "disk-write-pressure",
            "Disk writes may be causing stutter",
            "Check whether the target is writing logs, shader caches, recordings, or temp files. If writes align with spikes, move captures/cache to a faster drive or reduce write-heavy options.",
            HighDiskWriteMbPerSec);
    }

    private static void AddDiskRecommendation(
        SessionRecord session,
        List<SessionRecommendation> recommendations,
        MetricKey metricKey,
        string id,
        string title,
        string recommendation,
        double highPeak)
    {
        var metric = FindMetric(session, metricKey);
        if (metric is null || (metric.Max < highPeak && metric.ThresholdBreaches == 0 && metric.Spikes == 0))
        {
            return;
        }

        recommendations.Add(new SessionRecommendation
        {
            Id = id,
            Severity = metric.ThresholdBreaches > 0 ? SessionRecommendationSeverity.Warning : SessionRecommendationSeverity.Info,
            MetricKey = metricKey,
            Title = title,
            Recommendation = recommendation,
            Evidence = $"{metric.Label} avg {metric.Avg:N1} {metric.Unit}, peak {metric.Max:N1} {metric.Unit}, breaches {metric.ThresholdBreaches:N0}, spikes {metric.Spikes:N0}."
        });
    }

    private static void AddCaptureQualityRecommendations(
        SessionRecord session,
        List<SessionRecommendation> recommendations)
    {
        if (!session.Target.IncludeChildProcesses)
        {
            var hasPressure = session.Summary.ThresholdBreachCount > 0
                || session.Summary.SpikeCount > 0
                || session.Summary.HangSuspectedCount > 0
                || session.Summary.CrashLikeExit;

            if (hasPressure)
            {
                recommendations.Add(new SessionRecommendation
                {
                    Id = "enable-child-processes",
                    Severity = SessionRecommendationSeverity.Info,
                    Title = "Repeat once with child processes enabled",
                    Recommendation = "Many launchers, browsers, and games move real work into helper processes. A child-process run makes CPU/RAM/Disk attribution much more trustworthy.",
                    Evidence = "This session tracked only the root process."
                });
            }
        }

        if (session.Summary.SampleCount < 5 && session.Status != SessionStatus.Running)
        {
            recommendations.Add(new SessionRecommendation
            {
                Id = "short-session",
                Severity = SessionRecommendationSeverity.Info,
                Title = "This run is too short for confident trends",
                Recommendation = "Use this session for exit/startup clues only. For CPU, RAM, and disk trends, record at least 30-60 seconds under the workload you care about.",
                Evidence = $"Only {session.Summary.SampleCount:N0} sample(s) were recorded."
            });
        }
    }

    private static MetricSummary? FindMetric(SessionRecord session, MetricKey key) =>
        session.Summary.Metrics.FirstOrDefault(metric => metric.Key == key);

    private static double EstimateGrowth(SessionRecord session, MetricKey key)
    {
        var values = session.Samples
            .Where(sample => sample.Values.TryGetValue(key, out var value) && double.IsFinite(value))
            .Select(sample => sample.Values[key])
            .ToArray();

        if (values.Length < 4)
        {
            return 0;
        }

        var windowSize = Math.Max(1, values.Length / 3);
        var first = values.Take(windowSize).Average();
        var last = values.Skip(values.Length - windowSize).Average();
        return last - first;
    }
}
