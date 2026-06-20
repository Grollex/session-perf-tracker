using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using SessionPerfTracker.Domain.Metrics;
using SessionPerfTracker.App.Localization;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace SessionPerfTracker.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public Task ExportSelectedSessionHtmlAsync(CancellationToken cancellationToken = default) =>
        ExportSelectedSessionAsync("html", cancellationToken);

    public Task ExportSelectedSessionCsvAsync(CancellationToken cancellationToken = default) =>
        ExportSelectedSessionAsync("csv", cancellationToken);

    public Task ExportCurrentCompareHtmlAsync(CancellationToken cancellationToken = default) =>
        ExportCurrentCompareAsync("html", cancellationToken);

    public Task ExportCurrentCompareCsvAsync(CancellationToken cancellationToken = default) =>
        ExportCurrentCompareAsync("csv", cancellationToken);

    public void OpenSelectedSessionDetails()
    {
        if (SelectedSession is null)
        {
            StorageStatusText = "Select a saved session first.";
            return;
        }

        RefreshSessionDetails();
        SelectedTabIndex = SessionDetailsTabIndex;
    }

    public void BackToSessions()
    {
        SelectedTabIndex = SessionsTabIndex;
    }

    public void OpenLive()
    {
        SelectedTabIndex = LiveTabIndex;
    }

    private async Task ExportSelectedSessionAsync(string format, CancellationToken cancellationToken)
    {
        if (SelectedSession is null)
        {
            ExportStatusText = "Select a saved session first.";
            return;
        }

        try
        {
            var path = await _exportService.ExportSessionAsync(SelectedSession.Session, format, cancellationToken);
            ExportStatusText = $"Session {format.ToUpperInvariant()} exported: {path}";
            await RefreshExportFilesAsync(cancellationToken);
        }
        catch (Exception error)
        {
            ExportStatusText = $"Session export failed: {error.Message}";
        }
    }

    private async Task ExportCurrentCompareAsync(string format, CancellationToken cancellationToken)
    {
        if (CompareLeft is null || CompareRight is null || CompareLeft.Id == CompareRight.Id)
        {
            ExportStatusText = "Select two different saved sessions to export compare.";
            return;
        }

        try
        {
            var comparison = _comparisonEngine.Compare(CompareLeft.Session, CompareRight.Session);
            var path = await _exportService.ExportComparisonAsync(
                CompareLeft.Session,
                CompareRight.Session,
                comparison,
                format,
                cancellationToken);
            ExportStatusText = $"Compare {format.ToUpperInvariant()} exported: {path}";
            await RefreshExportFilesAsync(cancellationToken);
        }
        catch (Exception error)
        {
            ExportStatusText = $"Compare export failed: {error.Message}";
        }
    }

    public async Task ReloadSessionsAsync(string? selectSessionId = null, CancellationToken cancellationToken = default)
    {
        var sessions = await _sessionStore.ListSessionsAsync(cancellationToken);
        _allSessionItems.Clear();
        _allSessionItems.AddRange(sessions
            .Select(session => new SessionListItemViewModel(session))
            .OrderBy(session => session.IsPrimarySession ? 0 : session.IsShortSession ? 1 : 2)
            .ThenByDescending(session => session.Session.StartedAt));

        if (!HasLastCompletedSession && _allSessionItems.FirstOrDefault() is { } latestSession)
        {
            UpdateLastCompletedSession(latestSession.Session);
        }

        ApplySessionFilter(selectSessionId);
    }

    private void ApplySessionFilter(string? selectSessionId = null)
    {
        var query = SessionSearchText.Trim();
        var profileFilter = SelectedSessionProfileFilter;
        var filtered = _allSessionItems.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(session => session.AppName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || session.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (session.Session.Target.ExecutablePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (session.Session.Sampling.SessionProfileName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (session.Session.Sampling.ThresholdSourceLabel?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (profileFilter is { IsAll: false })
        {
            filtered = profileFilter.IsGlobalFallback
                ? filtered.Where(IsGlobalFallbackSession)
                : filtered.Where(session => string.Equals(
                    session.Session.Sampling.SessionProfileId,
                    profileFilter.ProfileId,
                    StringComparison.OrdinalIgnoreCase));
        }

        var filteredList = filtered.ToList();

        var previousId = selectSessionId ?? SelectedSession?.Id;
        Sessions.ReplaceWith(filteredList);
        SelectedSession = selectSessionId is null
            ? previousId is null
                ? Sessions.FirstOrDefault()
                : Sessions.FirstOrDefault(session => session.Id == previousId) ?? Sessions.FirstOrDefault()
            : Sessions.FirstOrDefault(session => session.Id == selectSessionId) ?? Sessions.FirstOrDefault();

        CompareLeft = Sessions.Skip(1).FirstOrDefault() ?? Sessions.FirstOrDefault();
        CompareRight = Sessions.FirstOrDefault();
        RefreshComparison();
        var profileLabel = profileFilter is null or { IsAll: true }
            ? "all profiles"
            : profileFilter.Label;
        StorageStatusText = string.IsNullOrWhiteSpace(query)
            ? $"{Sessions.Count:N0} sessions shown ({profileLabel})."
            : $"{Sessions.Count:N0} sessions match \"{query}\" ({profileLabel}).";
    }

    private bool HasActiveSessionFilter() =>
        !string.IsNullOrWhiteSpace(SessionSearchText)
        || SelectedSessionProfileFilter is { IsAll: false };

    private static bool IsGlobalFallbackSession(SessionListItemViewModel session) =>
        string.IsNullOrWhiteSpace(session.Session.Sampling.SessionProfileId)
        || (session.Session.Sampling.ThresholdSourceLabel?.Contains("Global fallback", StringComparison.OrdinalIgnoreCase) ?? false);

    private async Task<int> ApplyRetentionPolicyAsync(
        RetentionSettings retention,
        CancellationToken cancellationToken = default)
    {
        return retention.RetentionDays is null
            ? 0
            : await _sessionStore.DeleteSessionsOlderThanAsync(TimeSpan.FromDays(retention.RetentionDays.Value), cancellationToken);
    }

    private void RefreshSelectedSession()
    {
        SelectedMetricSummaries.ReplaceWith(
            SelectedSession?.Session.Summary.Metrics.Select(metric => new MetricSummaryRowViewModel(metric))
            ?? []);
        SelectedEvents.ReplaceWith(
            SelectedSession?.Session.Events.Select(performanceEvent => new EventRowViewModel(performanceEvent))
            ?? []);
        UnsupportedMetricNotices.ReplaceWith(CreateUnsupportedMetricNotices(SelectedSession?.Session));
        RefreshSessionDetails();

        NotifySummaryProperties();
    }

    private void RefreshSessionDetails()
    {
        var session = SelectedSession?.Session;
        if (session is null)
        {
            SessionDetailFacts.Clear();
            SessionDetailMetricSummaries.Clear();
            SessionDetailEvents.Clear();
            SessionDetailUnsupportedMetricNotices.Clear();
            NotifySessionDetailProperties();
            return;
        }

        SessionDetailFacts.ReplaceWith(CreateSessionDetailFacts(session));
        SessionDetailMetricSummaries.ReplaceWith(session.Summary.Metrics
            .Select(metric => new MetricSummaryRowViewModel(metric)));
        SessionDetailEvents.ReplaceWith(session.Events
            .OrderBy(performanceEvent => performanceEvent.ElapsedMs)
            .ThenBy(performanceEvent => performanceEvent.Timestamp)
            .Select(performanceEvent => new EventRowViewModel(performanceEvent)));
        SessionDetailUnsupportedMetricNotices.ReplaceWith(CreateUnsupportedMetricNotices(session));
        NotifySessionDetailProperties();
    }

    private void RefreshComparison()
    {
        ComparisonRows.Clear();
        UnavailableComparisonMetrics.Clear();

        if (CompareLeft is null || CompareRight is null || CompareLeft.Id == CompareRight.Id)
        {
            return;
        }

        var comparison = _comparisonEngine.Compare(CompareLeft.Session, CompareRight.Session);
        var availableMetrics = comparison.MetricComparisons
            .Where(item => item.Left is not null && item.Right is not null)
            .ToArray();
        var unavailableMetrics = comparison.MetricComparisons
            .Where(item => item.Left is null || item.Right is null)
            .ToArray();

        ComparisonRows.ReplaceWith(availableMetrics.Select(item => new ComparisonMetricRowViewModel(item)));
        UnavailableComparisonMetrics.ReplaceWith(unavailableMetrics.Select(item => new UnavailableComparisonMetricViewModel(item)));
    }

    private static IEnumerable<string> CreateUnsupportedMetricNotices(SessionRecord? session)
    {
        if (session is null)
        {
            return [];
        }

        var recorded = session.Summary.Metrics
            .Select(metric => metric.Key)
            .ToHashSet();

        return MetricCatalog.All
            .Where(metric => metric.Key is not MetricKey.CpuPercent and not MetricKey.MemoryMb)
            .Where(metric => !recorded.Contains(metric.Key))
            .Select(metric => metric.Key is MetricKey.DiskReadMbPerSec or MetricKey.DiskWriteMbPerSec
                ? $"{metric.Label}: not recorded in this session"
                : $"{metric.Label}: not collected in this build");
    }

    private IEnumerable<SessionDetailFactViewModel> CreateSessionDetailFacts(SessionRecord session)
    {
        var facts = new List<SessionDetailFactViewModel>
        {
            new("App", session.Target.DisplayName),
            new("Started", session.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
            new("Duration", FormatDuration(session.Summary.Duration)),
            new("Sampling", $"{session.Sampling.IntervalMs} ms"),
            new("Child processes", session.Target.IncludeChildProcesses ? "On" : "Off"),
            new("Capture", FormatCaptureScope(session.Sampling)),
            new("Status", FormatSessionStatus(session.Status)),
            new("Stability", FormatStability(session.Summary)),
            new("Exit", FormatExit(session.Summary))
        };

        if (!string.IsNullOrWhiteSpace(session.Target.ExecutablePath))
        {
            facts.Insert(1, new SessionDetailFactViewModel("Exe", Path.GetFileName(session.Target.ExecutablePath)));
        }

        if (session.EndedAt is not null)
        {
            facts.Insert(3, new SessionDetailFactViewModel(
                "Ended",
                session.EndedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
        }

        if (!string.IsNullOrWhiteSpace(session.Sampling.SessionProfileName))
        {
            facts.Add(new SessionDetailFactViewModel("Session profile", session.Sampling.SessionProfileName));
        }

        if (!string.IsNullOrWhiteSpace(session.Sampling.ThresholdSourceLabel))
        {
            facts.Add(new SessionDetailFactViewModel("Threshold source", session.Sampling.ThresholdSourceLabel));
        }

        if (session.Summary.LongStartupMs is not null)
        {
            facts.Add(new SessionDetailFactViewModel(
                "Startup",
                $"{TimeSpan.FromMilliseconds(session.Summary.LongStartupMs.Value).TotalSeconds:N1}s long startup event"));
        }

        if (session.Summary.Overhead is { SampleCount: > 0 } overhead)
        {
            facts.Add(new SessionDetailFactViewModel(
                "Self CPU avg / peak",
                $"{FormatPercent(overhead.AvgCpuPercent)} / {FormatPercent(overhead.MaxCpuPercent)}"));
            facts.Add(new SessionDetailFactViewModel(
                "Self RAM avg / peak",
                $"{FormatMb(overhead.AvgMemoryMb)} / {FormatMb(overhead.MaxMemoryMb)}"));
        }

        return facts;
    }

    private static string FormatCaptureScope(SamplingSettings sampling)
    {
        var enabled = new List<string>();
        if (sampling.CaptureCpu)
        {
            enabled.Add("CPU");
        }

        if (sampling.CaptureRam)
        {
            enabled.Add("RAM");
        }

        if (sampling.CaptureDiskRead)
        {
            enabled.Add("Disk Read");
        }

        if (sampling.CaptureDiskWrite)
        {
            enabled.Add("Disk Write");
        }

        return enabled.Count == 0 ? "No metrics enabled" : string.Join(", ", enabled);
    }

    private string FormatSessionStatus(SessionStatus status) => status switch
    {
        SessionStatus.Completed => GetText("Ui_StatusCompleted"),
        SessionStatus.Running => GetText("Ui_StatusRecording"),
        SessionStatus.Stopped => GetText("Ui_StatusStoppedByUser"),
        SessionStatus.ExternalExit => GetText("Ui_StatusClosedExternally"),
        SessionStatus.UnexpectedExit => GetText("Ui_StatusUnexpectedExit"),
        SessionStatus.CrashLikeExit => GetText("Ui_StatusCrashLikeExit"),
        SessionStatus.Planned => GetText("Ui_StatusNotRecorded"),
        _ => status.ToString()
    };

    private static string FormatStability(SessionSummary summary) =>
        string.IsNullOrWhiteSpace(summary.StabilityReason)
            ? summary.StabilityStatus.ToString()
            : $"{summary.StabilityStatus}: {summary.StabilityReason}";

    private string FormatExit(SessionSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.ExitReason))
        {
            return summary.ExitReason;
        }

        return summary.ExitKind switch
        {
            SessionExitKind.NormalStop => GetText("Ui_ExitNormalStop"),
            SessionExitKind.ExternalClose => "External close / graceful exit",
            SessionExitKind.UnexpectedExit => "Unexpected exit",
            SessionExitKind.CrashLikeExit => "Crash-like exit",
            SessionExitKind.Running => "Still running",
            SessionExitKind.Completed => "Completed",
            _ => "Unknown"
        };
    }

    private static string FormatRamDiagnostic(RamAccountingDiagnosticSnapshot snapshot)
    {
        var descendants = snapshot.Processes
            .Where(process => !process.IsRoot)
            .Select(process => process.ProcessId.ToString())
            .ToArray();
        var lines = new List<string>
        {
            $"Root PID: {snapshot.RootProcessId}",
            $"Root name: {snapshot.RootProcessName}",
            $"Root parent PID: {snapshot.RootParentProcessId?.ToString() ?? "n/a"}",
            $"Include child processes: {snapshot.IncludeChildProcesses}",
            $"Memory metric: {snapshot.MemoryMetricName}",
            $"Descendant PIDs: {(descendants.Length == 0 ? "none" : string.Join(", ", descendants))}",
            $"Aggregated RAM: {snapshot.AggregatedMemoryMb:N1} MB",
            "",
            "PID | Parent | Name | RAM MB | Metric"
        };

        lines.AddRange(snapshot.Processes.Select(process =>
            $"{process.ProcessId} | {process.ParentProcessId?.ToString() ?? "n/a"} | {process.ProcessName} | {process.MemoryMb:N1} | {process.MemoryMetricName}"));

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatSystemContext(SpikeContextSnapshot snapshot)
    {
        var lines = new List<string>
        {
            $"Captured: {snapshot.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            $"Target: {snapshot.RootTargetName ?? "n/a"}",
            "Best-effort process context, not a continuous trace.",
            "",
            "Top CPU"
        };

        lines.AddRange(FormatContextRows(snapshot.TopProcessesByCpu, process =>
            $"{process.Name} ({process.ProcessId}) | CPU {process.CpuPercent ?? 0:N1}% | RAM {process.MemoryMb ?? 0:N0} MB"));
        lines.Add("");
        lines.Add("Top RAM");
        lines.AddRange(FormatContextRows(snapshot.TopProcessesByMemory, process =>
            $"{process.Name} ({process.ProcessId}) | RAM {process.MemoryMb ?? 0:N0} MB | CPU {process.CpuPercent ?? 0:N1}%"));
        lines.Add("");
        lines.Add("Top Disk");
        lines.AddRange(FormatContextRows(snapshot.TopProcessesByDisk, process =>
            $"{process.Name} ({process.ProcessId}) | R {process.DiskReadMbPerSec ?? 0:N1} MB/s | W {process.DiskWriteMbPerSec ?? 0:N1} MB/s"));

        if (snapshot.NewProcessNames.Count > 0)
        {
            lines.Add("");
            lines.Add($"New near event: {string.Join(", ", snapshot.NewProcessNames)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<string> FormatContextRows(
        IReadOnlyList<ContextProcessSnapshot> processes,
        Func<ContextProcessSnapshot, string> formatter)
    {
        var rows = processes.Take(6).Select(formatter).ToArray();
        return rows.Length == 0 ? ["none"] : rows;
    }

}
