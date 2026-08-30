using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using SessionPerfTracker.Domain.Metrics;
using SessionPerfTracker.Domain.Services;
using SessionPerfTracker.App.Localization;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace SessionPerfTracker.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static readonly SessionRecommendationService SessionRecommendations = new();

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

    public void OpenFeedback()
    {
        SelectedSettingsTabIndex = SettingsFeedbackTabIndex;
        SelectedTabIndex = SettingsTabIndex;
    }

    public void OpenLanguageSettings(bool firstRun = false)
    {
        SelectedSettingsTabIndex = SettingsLanguageTabIndex;
        SelectedTabIndex = SettingsTabIndex;
        if (firstRun)
        {
            LanguageSettingsStatusText = GetText("Ui_LanguageFirstRunPrompt");
        }
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
            SessionDetailRecommendations.Clear();
            SessionDetailMetricSummaries.Clear();
            SessionDetailEvents.Clear();
            SessionDetailUnsupportedMetricNotices.Clear();
            NotifySessionDetailProperties();
            return;
        }

        SessionDetailFacts.ReplaceWith(CreateSessionDetailFacts(session));
        SessionDetailRecommendations.ReplaceWith(SessionRecommendations
            .Recommend(session)
            .Select(recommendation => new SessionRecommendationViewModel(recommendation)));
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
            NotifyComparisonStateProperties();
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
        NotifyComparisonStateProperties();
    }

    private void NotifyComparisonStateProperties()
    {
        OnPropertyChanged(nameof(HasComparisonRows));
        OnPropertyChanged(nameof(ShowComparisonEmptyState));
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
                ? UiText.Format("Ui_MetricNotRecordedInSession", metric.Label)
                : UiText.Format("Ui_MetricNotCollectedInBuild", metric.Label));
    }

    private IEnumerable<SessionDetailFactViewModel> CreateSessionDetailFacts(SessionRecord session)
    {
        var facts = new List<SessionDetailFactViewModel>
        {
            new(GetText("Ui_FactApp"), session.Target.DisplayName),
            new(GetText("Ui_FactStarted"), session.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
            new(GetText("Ui_FactDuration"), FormatDuration(session.Summary.Duration)),
            new(GetText("Ui_FactSampling"), $"{session.Sampling.IntervalMs} ms"),
            new(GetText("Ui_FactChildProcesses"), session.Target.IncludeChildProcesses ? GetText("Ui_On") : GetText("Ui_Off")),
            new(GetText("Ui_FactCapture"), FormatCaptureScope(session.Sampling)),
            new(GetText("Ui_FactStatus"), FormatSessionStatus(session.Status)),
            new(GetText("Ui_FactStability"), FormatStability(session.Summary)),
            new(GetText("Ui_FactExit"), FormatExit(session.Summary))
        };

        if (!string.IsNullOrWhiteSpace(session.Target.ExecutablePath))
        {
            facts.Insert(1, new SessionDetailFactViewModel(GetText("Ui_FactExe"), Path.GetFileName(session.Target.ExecutablePath)));
        }

        if (session.EndedAt is not null)
        {
            facts.Insert(3, new SessionDetailFactViewModel(
                GetText("Ui_FactEnded"),
                session.EndedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
        }

        if (!string.IsNullOrWhiteSpace(session.Sampling.SessionProfileName))
        {
            facts.Add(new SessionDetailFactViewModel(GetText("Ui_FactSessionProfile"), session.Sampling.SessionProfileName));
        }

        if (!string.IsNullOrWhiteSpace(session.Sampling.ThresholdSourceLabel))
        {
            facts.Add(new SessionDetailFactViewModel(GetText("Ui_FactThresholdSource"), session.Sampling.ThresholdSourceLabel));
        }

        if (session.Summary.LongStartupMs is not null)
        {
            facts.Add(new SessionDetailFactViewModel(
                GetText("Ui_FactStartup"),
                UiText.Format("Ui_LongStartupSeconds", TimeSpan.FromMilliseconds(session.Summary.LongStartupMs.Value).TotalSeconds)));
        }

        if (session.Summary.Overhead is { SampleCount: > 0 } overhead)
        {
            facts.Add(new SessionDetailFactViewModel(
                GetText("Ui_FactSelfCpuAvgPeak"),
                $"{FormatPercent(overhead.AvgCpuPercent)} / {FormatPercent(overhead.MaxCpuPercent)}"));
            facts.Add(new SessionDetailFactViewModel(
                GetText("Ui_FactSelfRamAvgPeak"),
                $"{FormatMb(overhead.AvgMemoryMb)} / {FormatMb(overhead.MaxMemoryMb)}"));
        }

        return facts;
    }

    private string FormatCaptureScope(SamplingSettings sampling)
    {
        var enabled = new List<string>();
        if (sampling.CaptureCpu)
        {
            enabled.Add(GetText("Ui_CPU"));
        }

        if (sampling.CaptureRam)
        {
            enabled.Add(GetText("Ui_RAM"));
        }

        if (sampling.CaptureDiskRead)
        {
            enabled.Add(GetText("Ui_DiskRead"));
        }

        if (sampling.CaptureDiskWrite)
        {
            enabled.Add(GetText("Ui_DiskWrite"));
        }

        return enabled.Count == 0 ? GetText("Ui_NoMetricsEnabled") : string.Join(", ", enabled);
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

    private string FormatStability(SessionSummary summary)
    {
        var status = FormatStabilityStatus(summary.StabilityStatus);
        var reason = FormatStabilityReason(summary.StabilityReason);
        return string.IsNullOrWhiteSpace(reason) ? status : $"{status}: {reason}";
    }

    private string FormatStabilityStatus(StabilityStatus status) => status switch
    {
        StabilityStatus.Stable => GetText("Ui_StabilityHealthy"),
        StabilityStatus.Degraded => GetText("Ui_StabilityDegraded"),
        StabilityStatus.Hung => GetText("Ui_StabilityHung"),
        StabilityStatus.Crashed => GetText("Ui_StabilityCrashed"),
        _ => status.ToString()
    };

    private string FormatStabilityReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return string.Empty;
        }

        return reason switch
        {
            "Best-effort crash-like exit signal was recorded." => GetText("Ui_StabilityReasonCrashLike"),
            "Target exited unexpectedly, but no crash-like signal was recorded." => GetText("Ui_StabilityReasonUnexpectedExit"),
            "No stability issues detected." => GetText("Ui_StabilityReasonHealthy"),
            _ => reason
        };
    }

    private string FormatExit(SessionSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.ExitReason))
        {
            return FormatExitReason(summary.ExitReason);
        }

        return summary.ExitKind switch
        {
            SessionExitKind.NormalStop => GetText("Ui_ExitNormalStop"),
            SessionExitKind.ExternalClose => GetText("Ui_ExitExternalClose"),
            SessionExitKind.UnexpectedExit => GetText("Ui_StatusUnexpectedExit"),
            SessionExitKind.CrashLikeExit => GetText("Ui_StatusCrashLikeExit"),
            SessionExitKind.Running => GetText("Ui_Running"),
            SessionExitKind.Completed => GetText("Ui_StatusCompleted"),
            _ => GetText("Ui_StatusUnknown")
        };
    }

    private string FormatExitReason(string reason)
    {
        if (reason.StartsWith("The target process exited with non-zero exit code", StringComparison.OrdinalIgnoreCase))
        {
            return GetText("Ui_ExitReasonNonZero");
        }

        return reason switch
        {
            "Target process was closed externally." => GetText("Ui_ExitReasonExternalClose"),
            "Target process stopped normally." => GetText("Ui_ExitReasonNormalStop"),
            _ => reason
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
