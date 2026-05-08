using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Text;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Metrics;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Export;

public sealed class HtmlCsvExportService : IExportService
{
    private static readonly Encoding CsvEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private static readonly MetricKey[] ReportMetricKeys =
    [
        MetricKey.CpuPercent,
        MetricKey.MemoryMb,
        MetricKey.DiskReadMbPerSec,
        MetricKey.DiskWriteMbPerSec
    ];

    private string _exportDirectory = string.Empty;

    public HtmlCsvExportService(string exportDirectory)
    {
        SetExportDirectory(exportDirectory);
    }

    public string ExportDirectory => _exportDirectory;

    public void SetExportDirectory(string exportDirectory)
    {
        if (string.IsNullOrWhiteSpace(exportDirectory))
        {
            throw new ArgumentException("Export directory cannot be empty.", nameof(exportDirectory));
        }

        _exportDirectory = Path.GetFullPath(exportDirectory.Trim());
        Directory.CreateDirectory(_exportDirectory);
    }

    public Task<IReadOnlyList<ExportFileDescriptor>> ListExportsAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_exportDirectory);
        IReadOnlyList<ExportFileDescriptor> files = Directory
            .EnumerateFiles(_exportDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new ExportFileDescriptor(
                    info.FullName,
                    info.Name,
                    new DateTimeOffset(info.LastWriteTime),
                    info.Length);
            })
            .OrderByDescending(file => file.LastWriteTime)
            .ToArray();
        return Task.FromResult(files);
    }

    public Task OpenExportAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Export file does not exist.", path);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    public Task OpenExportDirectoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_exportDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = _exportDirectory,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    public async Task<string> ExportSessionAsync(
        SessionRecord session,
        string format,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_exportDirectory);
        return NormalizeFormat(format) switch
        {
            "html" => await WriteTextAsync(CreateFilePath(session, "session", "html"), BuildSessionHtml(session), cancellationToken),
            "csv" => await ExportSessionCsvAsync(session, cancellationToken),
            _ => throw new ArgumentException($"Unsupported export format: {format}", nameof(format))
        };
    }

    public async Task<string> ExportComparisonAsync(
        SessionRecord left,
        SessionRecord right,
        SessionComparisonResult comparison,
        string format,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_exportDirectory);
        return NormalizeFormat(format) switch
        {
            "html" => await WriteTextAsync(CreateCompareFilePath(left, right, "html"), BuildCompareHtml(left, right, comparison), cancellationToken),
            "csv" => await WriteTextAsync(CreateCompareFilePath(left, right, "csv"), BuildCompareCsv(left, right, comparison), cancellationToken, CsvEncoding),
            _ => throw new ArgumentException($"Unsupported export format: {format}", nameof(format))
        };
    }

    private async Task<string> ExportSessionCsvAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        var samplesPath = CreateFilePath(session, "session_samples", "csv");
        var eventsPath = CreateFilePath(session, "session_events", "csv");

        await WriteTextAsync(samplesPath, BuildSessionSamplesCsv(session), cancellationToken, CsvEncoding);
        await WriteTextAsync(eventsPath, BuildSessionEventsCsv(session), cancellationToken, CsvEncoding);

        return $"{samplesPath}; {eventsPath}";
    }

    private static async Task<string> WriteTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken,
        Encoding? encoding = null)
    {
        await File.WriteAllTextAsync(path, content, encoding ?? Encoding.UTF8, cancellationToken);
        return path;
    }

    private string CreateFilePath(SessionRecord session, string suffix, string extension)
    {
        var appName = SanitizeFilePart(GetAppName(session));
        var timestamp = session.StartedAt.ToLocalTime().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(_exportDirectory, $"{appName}_{timestamp}_{suffix}.{extension}");
    }

    private string CreateCompareFilePath(SessionRecord left, SessionRecord right, string extension)
    {
        var leftName = SanitizeFilePart(GetAppName(left));
        var rightName = SanitizeFilePart(GetAppName(right));
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(_exportDirectory, $"{leftName}_vs_{rightName}_{timestamp}_compare.{extension}");
    }

    private static string BuildSessionHtml(SessionRecord session)
    {
        var builder = CreateHtmlShell(
            $"Session report - {Html(GetAppName(session))}",
            $"Session report: {Html(GetAppName(session))}");

        builder.AppendLine("<section class=\"panel\"><h2>Session</h2><div class=\"grid\">");
        AddKeyValue(builder, "App / exe", GetAppName(session));
        AddKeyValue(builder, "Target", session.Target.DisplayName);
        AddKeyValue(builder, "Start", FormatDate(session.StartedAt));
        AddKeyValue(builder, "End", session.EndedAt is null ? "n/a" : FormatDate(session.EndedAt.Value));
        AddKeyValue(builder, "Duration", FormatDuration(session.Summary.Duration));
        AddKeyValue(builder, "Stability", session.Summary.StabilityStatus.ToString());
        AddKeyValue(builder, "Exit", FormatExitKind(session.Summary.ExitKind));
        AddKeyValue(builder, "Exit reason", session.Summary.ExitReason ?? "n/a");
        AddKeyValue(builder, "Session profile", FormatSessionProfile(session));
        AddKeyValue(builder, "Threshold source", session.Sampling.ThresholdSourceLabel ?? FormatSessionProfile(session));
        AddKeyValue(builder, "Sampling", $"{session.Sampling.IntervalMs} ms");
        AddKeyValue(builder, "Child processes", session.Target.IncludeChildProcesses ? "ON" : "OFF");
        builder.AppendLine("</div></section>");

        AddMetricSummary(builder, session);
        AddEvents(builder, session.Events);
        AddOverhead(builder, session.Summary.Overhead);
        AddUnsupported(builder, session);
        return CloseHtml(builder);
    }

    private static string BuildCompareHtml(
        SessionRecord left,
        SessionRecord right,
        SessionComparisonResult comparison)
    {
        var builder = CreateHtmlShell(
            "Compare report",
            $"Compare: {Html(GetAppName(left))} vs {Html(GetAppName(right))}");

        builder.AppendLine("<section class=\"panel two\"><div><h2>Left</h2>");
        AddSessionIdentity(builder, left);
        builder.AppendLine("</div><div><h2>Right</h2>");
        AddSessionIdentity(builder, right);
        builder.AppendLine("</div></section>");

        builder.AppendLine("<section class=\"panel\"><h2>Metric comparison</h2>");
        builder.AppendLine("<table><thead><tr><th>Metric</th><th>Left avg</th><th>Right avg</th><th>Avg delta</th><th>Left max</th><th>Right max</th><th>Max delta</th><th>Better</th></tr></thead><tbody>");
        foreach (var metric in comparison.MetricComparisons.Where(IsReportableComparison))
        {
            var leftClass = metric.Winner == "left" ? "good" : metric.Winner == "right" ? "bad" : string.Empty;
            var rightClass = metric.Winner == "right" ? "good" : metric.Winner == "left" ? "bad" : string.Empty;
            builder.AppendLine("<tr>");
            AppendCell(builder, metric.Label);
            AppendCell(builder, FormatMetric(metric.Left!.Avg, metric.Unit), leftClass);
            AppendCell(builder, FormatMetric(metric.Right!.Avg, metric.Unit), rightClass);
            AppendCell(builder, metric.AvgDeltaPercent is null
                ? "n/a"
                : $"{metric.AvgDeltaPercent.Value:+0.0;-0.0;0.0}%", GetDeltaClass(metric));
            AppendCell(builder, FormatMetric(metric.Left.Max, metric.Unit), leftClass);
            AppendCell(builder, FormatMetric(metric.Right.Max, metric.Unit), rightClass);
            AppendCell(builder, metric.MaxDelta is null ? "n/a" : FormatMetric(metric.MaxDelta.Value, metric.Unit), GetDeltaClass(metric));
            AppendCell(builder, metric.Winner);
            builder.AppendLine("</tr>");
        }

        builder.AppendLine("</tbody></table>");
        var unavailable = comparison.MetricComparisons
            .Where(item => ReportMetricKeys.Contains(item.Key))
            .Where(item => item.Left is null || item.Right is null)
            .Select(item => item.Label)
            .ToArray();
        if (unavailable.Length > 0)
        {
            builder.AppendLine($"<p class=\"muted\">Not included because data is unavailable in one or both sessions: {Html(string.Join(", ", unavailable))}.</p>");
        }

        builder.AppendLine("</section>");
        return CloseHtml(builder);
    }

    private static string BuildSessionSamplesCsv(SessionRecord session)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Csv("session_id", "timestamp", "elapsed_ms", "root_process_id", "process_count", "cpu_percent", "ram_mb", "disk_read_mb_per_sec", "disk_write_mb_per_sec"));
        foreach (var sample in session.Samples.OrderBy(item => item.ElapsedMs))
        {
            builder.AppendLine(Csv(
                sample.SessionId,
                FormatDate(sample.Timestamp),
                sample.ElapsedMs,
                sample.RootProcessId,
                sample.ProcessCount,
                GetMetricValue(sample, MetricKey.CpuPercent),
                GetMetricValue(sample, MetricKey.MemoryMb),
                GetMetricValue(sample, MetricKey.DiskReadMbPerSec),
                GetMetricValue(sample, MetricKey.DiskWriteMbPerSec)));
        }

        return builder.ToString();
    }

    private static string BuildSessionEventsCsv(SessionRecord session)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Csv("session_id", "timestamp", "elapsed_ms", "kind", "grouped_kinds", "metric", "severity", "title", "details", "observed_value", "threshold_value", "detection_provider", "noise_policy", "has_context"));
        foreach (var performanceEvent in session.Events.OrderBy(item => item.ElapsedMs))
        {
            builder.AppendLine(Csv(
                performanceEvent.SessionId,
                FormatDate(performanceEvent.Timestamp),
                performanceEvent.ElapsedMs,
                performanceEvent.Kind,
                FormatKinds(performanceEvent),
                performanceEvent.MetricKey,
                performanceEvent.Severity,
                performanceEvent.Title,
                performanceEvent.Details,
                FormatNumber(performanceEvent.ObservedValue),
                FormatNumber(performanceEvent.ThresholdValue),
                performanceEvent.DetectionProvider,
                performanceEvent.NoisePolicy,
                performanceEvent.Context is not null));
        }

        return builder.ToString();
    }

    private static string BuildCompareCsv(
        SessionRecord left,
        SessionRecord right,
        SessionComparisonResult comparison)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Csv("left_session", SessionLabel(left)));
        builder.AppendLine(Csv("right_session", SessionLabel(right)));
        builder.AppendLine();
        builder.AppendLine(Csv("metric", "left_avg", "right_avg", "avg_delta", "avg_delta_percent", "left_max", "right_max", "max_delta", "winner"));
        foreach (var metric in comparison.MetricComparisons.Where(IsReportableComparison))
        {
            builder.AppendLine(Csv(
                metric.Label,
                FormatNumber(metric.Left!.Avg),
                FormatNumber(metric.Right!.Avg),
                FormatNumber(metric.AvgDelta),
                FormatNumber(metric.AvgDeltaPercent),
                FormatNumber(metric.Left.Max),
                FormatNumber(metric.Right.Max),
                FormatNumber(metric.MaxDelta),
                metric.Winner));
        }

        return builder.ToString();
    }

    private static void AddMetricSummary(StringBuilder builder, SessionRecord session)
    {
        var summaries = session.Summary.Metrics.ToDictionary(item => item.Key);
        builder.AppendLine("<section class=\"panel\"><h2>Metric summary</h2><table><thead><tr><th>Metric</th><th>Min</th><th>Avg</th><th>Max</th><th>Spikes</th><th>Breaches</th><th>Status</th></tr></thead><tbody>");
        foreach (var key in ReportMetricKeys)
        {
            var definition = MetricCatalog.Get(key);
            if (!summaries.TryGetValue(key, out var summary) || summary.Reliability == MetricReliability.Unavailable)
            {
                builder.AppendLine("<tr>");
                AppendCell(builder, definition.Label);
                AppendCell(builder, "Not recorded", "muted", colspan: 6);
                builder.AppendLine("</tr>");
                continue;
            }

            builder.AppendLine("<tr>");
            AppendCell(builder, summary.Label);
            AppendCell(builder, FormatMetric(summary.Min, summary.Unit));
            AppendCell(builder, FormatMetric(summary.Avg, summary.Unit));
            AppendCell(builder, FormatMetric(summary.Max, summary.Unit));
            AppendCell(builder, summary.Spikes.ToString("N0", CultureInfo.InvariantCulture));
            AppendCell(builder, summary.ThresholdBreaches.ToString("N0", CultureInfo.InvariantCulture));
            AppendCell(builder, FormatReliability(summary.Reliability));
            builder.AppendLine("</tr>");
        }

        builder.AppendLine("</tbody></table></section>");
    }

    private static void AddEvents(StringBuilder builder, IReadOnlyList<PerformanceEvent> events)
    {
        builder.AppendLine("<section class=\"panel\"><h2>Events</h2>");
        if (events.Count == 0)
        {
            builder.AppendLine("<p class=\"muted\">No detector events were recorded.</p></section>");
            return;
        }

        builder.AppendLine("<table><thead><tr><th>Time</th><th>Kind</th><th>Metric</th><th>Severity</th><th>Observed</th><th>Threshold</th><th>Title</th><th>Details</th></tr></thead><tbody>");
        foreach (var performanceEvent in events.OrderBy(item => item.ElapsedMs))
        {
            builder.AppendLine("<tr>");
            AppendCell(builder, $"+{TimeSpan.FromMilliseconds(performanceEvent.ElapsedMs):mm\\:ss}");
            AppendCell(builder, FormatKinds(performanceEvent));
            AppendCell(builder, FormatMetricKey(performanceEvent.MetricKey));
            AppendCell(builder, performanceEvent.Severity.ToString());
            AppendCell(builder, FormatMetricValue(performanceEvent.MetricKey, performanceEvent.ObservedValue));
            AppendCell(builder, FormatMetricValue(performanceEvent.MetricKey, performanceEvent.ThresholdValue));
            AppendCell(builder, performanceEvent.Title);
            AppendCell(builder, performanceEvent.Details);
            builder.AppendLine("</tr>");
            if (performanceEvent.Context is not null)
            {
                builder.AppendLine($"<tr><td colspan=\"8\">{BuildContextHtml(performanceEvent.Context)}</td></tr>");
            }
        }

        builder.AppendLine("</tbody></table></section>");
    }

    private static void AddOverhead(StringBuilder builder, SelfOverheadSummary? overhead)
    {
        if (overhead is null || overhead.SampleCount == 0)
        {
            return;
        }

        builder.AppendLine("<section class=\"panel\"><h2>Utility overhead</h2><div class=\"grid\">");
        AddKeyValue(builder, "Avg CPU", FormatMetricValue(MetricKey.CpuPercent, overhead.AvgCpuPercent));
        AddKeyValue(builder, "Peak CPU", FormatMetricValue(MetricKey.CpuPercent, overhead.MaxCpuPercent));
        AddKeyValue(builder, "Avg RAM", FormatMetricValue(MetricKey.MemoryMb, overhead.AvgMemoryMb));
        AddKeyValue(builder, "Peak RAM", FormatMetricValue(MetricKey.MemoryMb, overhead.MaxMemoryMb));
        AddKeyValue(builder, "Samples", overhead.SampleCount.ToString("N0", CultureInfo.InvariantCulture));
        builder.AppendLine("</div></section>");
    }

    private static void AddUnsupported(StringBuilder builder, SessionRecord session)
    {
        var recorded = session.Summary.Metrics.Select(item => item.Key).ToHashSet();
        var unavailable = ReportMetricKeys
            .Where(key => !recorded.Contains(key))
            .Select(key => MetricCatalog.Get(key).Label)
            .ToArray();
        if (unavailable.Length == 0)
        {
            return;
        }

        builder.AppendLine($"<section class=\"panel\"><h2>Unavailable metrics</h2><p class=\"muted\">Not recorded in this session: {Html(string.Join(", ", unavailable))}.</p></section>");
    }

    private static string BuildContextHtml(SpikeContextSnapshot context)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<div class=\"context\"><strong>Context around event</strong><span class=\"muted\"> best effort, not a continuous system trace</span>");
        builder.AppendLine("<div class=\"context-grid\">");
        AddContextList(builder, "Top CPU", context.TopProcessesByCpu, process =>
            process.CpuPercent is null ? null : $"{process.Name} ({process.ProcessId}) - {process.CpuPercent.Value:N1}%");
        AddContextList(builder, "Top RAM", context.TopProcessesByMemory, process =>
            process.MemoryMb is null ? null : $"{process.Name} ({process.ProcessId}) - {process.MemoryMb.Value:N0} MB");
        AddContextList(builder, "Top Disk", context.TopProcessesByDisk, process =>
        {
            var read = process.DiskReadMbPerSec ?? 0;
            var write = process.DiskWriteMbPerSec ?? 0;
            return read <= 0 && write <= 0 ? null : $"{process.Name} ({process.ProcessId}) - R {read:N1} / W {write:N1} MB/s";
        });
        builder.AppendLine("</div>");
        if (context.NewProcessNames.Count > 0)
        {
            builder.AppendLine($"<p>New near event: {Html(string.Join(", ", context.NewProcessNames.Take(8)))}</p>");
        }

        builder.AppendLine("</div>");
        return builder.ToString();
    }

    private static void AddContextList(
        StringBuilder builder,
        string title,
        IReadOnlyList<ContextProcessSnapshot> processes,
        Func<ContextProcessSnapshot, string?> formatter)
    {
        var rows = processes
            .Select(formatter)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(5)
            .ToArray();
        builder.AppendLine($"<div><h3>{Html(title)}</h3>");
        if (rows.Length == 0)
        {
            builder.AppendLine("<p class=\"muted\">none</p></div>");
            return;
        }

        builder.AppendLine("<ul>");
        foreach (var row in rows)
        {
            builder.AppendLine($"<li>{Html(row!)}</li>");
        }

        builder.AppendLine("</ul></div>");
    }

    private static void AddSessionIdentity(StringBuilder builder, SessionRecord session)
    {
        builder.AppendLine("<div class=\"grid\">");
        AddKeyValue(builder, "Label", SessionLabel(session));
        AddKeyValue(builder, "Duration", FormatDuration(session.Summary.Duration));
        AddKeyValue(builder, "Stability", session.Summary.StabilityStatus.ToString());
        AddKeyValue(builder, "Exit", FormatExitKind(session.Summary.ExitKind));
        AddKeyValue(builder, "Profile", FormatSessionProfile(session));
        AddKeyValue(builder, "Threshold source", session.Sampling.ThresholdSourceLabel ?? FormatSessionProfile(session));
        AddKeyValue(builder, "Sampling", $"{session.Sampling.IntervalMs} ms");
        AddKeyValue(builder, "Child processes", session.Target.IncludeChildProcesses ? "ON" : "OFF");
        builder.AppendLine("</div>");
    }

    private static void AddKeyValue(StringBuilder builder, string key, string value)
    {
        builder.AppendLine($"<div><span class=\"muted\">{Html(key)}</span><strong>{Html(value)}</strong></div>");
    }

    private static bool IsReportableComparison(MetricComparison comparison) =>
        ReportMetricKeys.Contains(comparison.Key)
        && comparison.Left is not null
        && comparison.Right is not null
        && comparison.Left.Reliability != MetricReliability.Unavailable
        && comparison.Right.Reliability != MetricReliability.Unavailable;

    private static string GetDeltaClass(MetricComparison metric)
    {
        if (metric.AvgDelta is null || Math.Abs(metric.AvgDelta.Value) < 0.0001)
        {
            return string.Empty;
        }

        var rightWorse = metric.Direction switch
        {
            ComparisonDirection.HigherIsBetter => metric.AvgDelta < 0,
            ComparisonDirection.LowerIsBetter => metric.AvgDelta > 0,
            _ => false
        };
        return rightWorse ? "bad" : "good";
    }

    private static StringBuilder CreateHtmlShell(string title, string heading)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine($"<title>{title}</title>");
        builder.AppendLine("""
            <style>
            :root { color-scheme: dark; font-family: Segoe UI, Arial, sans-serif; background: #101317; color: #edf2f5; }
            body { margin: 0; padding: 28px; background: #101317; }
            h1 { margin: 0 0 18px; font-size: 28px; }
            h2 { margin: 0 0 14px; font-size: 18px; }
            h3 { margin: 10px 0 6px; font-size: 14px; }
            .panel { background: #171c21; border: 1px solid #303941; border-radius: 8px; padding: 16px; margin: 0 0 14px; }
            .two { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
            .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 12px; }
            .grid div { background: #11171c; border: 1px solid #303941; border-radius: 8px; padding: 10px; }
            .grid span { display: block; margin-bottom: 4px; }
            table { width: 100%; border-collapse: collapse; }
            th, td { border-bottom: 1px solid #303941; padding: 9px; text-align: left; vertical-align: top; }
            th { color: #97a3ad; font-weight: 600; }
            .muted { color: #97a3ad; }
            .good { color: #3ee079; }
            .bad { color: #f05d5e; }
            .context { background: #11171c; border: 1px solid #303941; border-radius: 8px; padding: 10px; }
            .context-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 10px; }
            ul { margin: 0; padding-left: 18px; }
            </style>
            """);
        builder.AppendLine("</head><body>");
        builder.AppendLine($"<h1>{heading}</h1>");
        return builder;
    }

    private static string CloseHtml(StringBuilder builder)
    {
        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    private static string Csv(params object?[] values) =>
        string.Join(",", values.Select(EscapeCsv));

    private static string EscapeCsv(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            double doubleValue => FormatNumber(doubleValue),
            float floatValue => FormatNumber(floatValue),
            decimal decimalValue => decimalValue.ToString("0.###", CultureInfo.InvariantCulture),
            DateTimeOffset date => FormatDate(date),
            _ => value.ToString() ?? string.Empty
        };

        return text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }

    private static string FormatMetricValue(MetricKey? key, double? value)
    {
        if (value is null)
        {
            return "n/a";
        }

        var unit = key switch
        {
            MetricKey.CpuPercent => "%",
            MetricKey.MemoryMb => "MB",
            MetricKey.DiskReadMbPerSec => "MB/s",
            MetricKey.DiskWriteMbPerSec => "MB/s",
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(unit)
            ? FormatNumber(value)
            : $"{FormatNumber(value)} {unit}";
    }

    private static string? GetMetricValue(MetricSample sample, MetricKey key) =>
        sample.Values.TryGetValue(key, out var value) ? FormatNumber(value) : null;

    private static string FormatMetric(double value, string unit) =>
        $"{value:N1} {unit}";

    private static string FormatNumber(double? value) =>
        value is null ? string.Empty : value.Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatDate(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string FormatDuration(TimeSpan duration) =>
        $"{(int)duration.TotalMinutes}m {duration.Seconds:00}s";

    private static string FormatReliability(MetricReliability reliability) => reliability switch
    {
        MetricReliability.Stable => "Recorded",
        MetricReliability.BestEffort => "Recorded, best effort",
        MetricReliability.Unavailable => "Not supported",
        _ => reliability.ToString()
    };

    private static string FormatExitKind(SessionExitKind exitKind) => exitKind switch
    {
        SessionExitKind.NormalStop => "Normal stop",
        SessionExitKind.ExternalClose => "External close / graceful exit",
        SessionExitKind.UnexpectedExit => "Unexpected exit",
        SessionExitKind.CrashLikeExit => "Crash-like exit",
        SessionExitKind.Completed => "Completed",
        SessionExitKind.Running => "Running",
        _ => "Unknown"
    };

    private static string FormatMetricKey(MetricKey? key) => key switch
    {
        MetricKey.CpuPercent => "CPU",
        MetricKey.MemoryMb => "RAM",
        MetricKey.DiskReadMbPerSec => "Disk Read",
        MetricKey.DiskWriteMbPerSec => "Disk Write",
        _ => key?.ToString() ?? "Session"
    };

    private static string FormatKinds(PerformanceEvent performanceEvent) =>
        performanceEvent.GroupedKinds.Count > 1
            ? $"Grouped: {string.Join(" + ", performanceEvent.GroupedKinds.Select(FormatKind))}"
            : FormatKind(performanceEvent.Kind);

    private static string FormatKind(EventKind kind) => kind switch
    {
        EventKind.ThresholdBreach => "Threshold breach",
        EventKind.Spike => "Spike",
        EventKind.HangSuspected => "Hang suspected",
        EventKind.LongStartup => "Long startup",
        EventKind.ExternalExit => "External close",
        EventKind.UnexpectedExit => "Unexpected exit",
        EventKind.CrashLikeExit => "Crash-like exit",
        _ => kind.ToString()
    };

    private static string FormatSessionProfile(SessionRecord session)
    {
        if (!string.IsNullOrWhiteSpace(session.Sampling.SessionProfileName))
        {
            return session.Sampling.SessionProfileName;
        }

        return session.Sampling.SessionProfileMode == "Auto"
            ? "Auto / global fallback"
            : session.Sampling.SessionProfileMode;
    }

    private static string SessionLabel(SessionRecord session) =>
        $"{GetAppName(session)} - {FormatDate(session.StartedAt)} - {FormatDuration(session.Summary.Duration)} - {(session.Target.IncludeChildProcesses ? "child ON" : "child OFF")} - {session.Sampling.IntervalMs} ms";

    private static string GetAppName(SessionRecord session)
    {
        if (!string.IsNullOrWhiteSpace(session.Target.ExecutablePath))
        {
            return Path.GetFileName(session.Target.ExecutablePath);
        }

        var displayName = session.Target.DisplayName;
        var pidMarkerIndex = displayName.IndexOf(" (", StringComparison.Ordinal);
        return pidMarkerIndex > 0 ? displayName[..pidMarkerIndex] : displayName;
    }

    private static string SanitizeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        cleaned = cleaned.Trim(' ', '.', '_');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "session";
        }

        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }

    private static string NormalizeFormat(string format) =>
        format.Trim().ToLowerInvariant();

    private static string Html(string value) =>
        WebUtility.HtmlEncode(value);

    private static void AppendCell(StringBuilder builder, string value, string? cssClass = null, int colspan = 1)
    {
        var classAttribute = string.IsNullOrWhiteSpace(cssClass) ? string.Empty : $" class=\"{cssClass}\"";
        var colspanAttribute = colspan <= 1 ? string.Empty : $" colspan=\"{colspan}\"";
        builder.Append($"<td{classAttribute}{colspanAttribute}>{Html(value)}</td>");
    }
}
