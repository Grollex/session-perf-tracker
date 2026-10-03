using System.Globalization;
using System.IO;
using SessionPerfTracker.App.Controls;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.App.ViewModels;

public sealed record TimelineRangeOption(string Label, double Seconds)
{
    public override string ToString() => Label;
}

/// <summary>
/// Live dashboard: target card, KPI tiles, timeline, system top processes, RAM and session summary.
/// When nothing is recorded the dashboard shows whole-system data from Global Watch scans.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private const int LiveTopProcessCount = 5;
    private readonly TimelineHistory _sessionTimeline = new();
    private readonly TimelineHistory _systemTimeline = new();
    private readonly double _fallbackMemoryTotalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d;
    private bool _timelineShowsSystem = true;
    private int _timelineVersion;
    private TimelineRangeOption? _selectedTimelineRange;
    private int _selectedLiveTopSortIndex;
    private bool _isTargetPickerOpen;
    private double _systemDiskReadMbPerSec;
    private double _systemDiskWriteMbPerSec;
    private double _ramPeakSinceStartMb;
    private double _ramPeakSessionMb;
    private SessionRecord? _lastCompletedSessionRecord;

    public RangeObservableCollection<GlobalProcessRowViewModel> LiveTopProcesses { get; } = new();

    public IReadOnlyList<TimelineRangeOption> TimelineRanges => _timelineRanges ??=
    [
        new(GetText("Ui_Range1Min"), 60),
        new(GetText("Ui_Range5Min"), 300),
        new(GetText("Ui_Range10Min"), 600),
        new(GetText("Ui_Range30Min"), 1800),
        new(GetText("Ui_Range1Hour"), 3600),
        new(GetText("Ui_RangeAll"), 0)
    ];

    private IReadOnlyList<TimelineRangeOption>? _timelineRanges;

    public TimelineRangeOption SelectedTimelineRange
    {
        get => _selectedTimelineRange ??= TimelineRanges[2];
        set
        {
            if (value is not null && SetProperty(ref _selectedTimelineRange, value))
            {
                OnPropertyChanged(nameof(TimelineWindowSeconds));
            }
        }
    }

    public double TimelineWindowSeconds => SelectedTimelineRange.Seconds;

    public void ZoomTimeline(bool zoomIn)
    {
        var index = TimelineRanges.ToList().IndexOf(SelectedTimelineRange);
        index = Math.Clamp(index + (zoomIn ? -1 : 1), 0, TimelineRanges.Count - 1);
        SelectedTimelineRange = TimelineRanges[index];
    }

    public TimelineHistory ActiveTimeline => TimelineShowsSystem ? _systemTimeline : _sessionTimeline;

    public int TimelineVersion
    {
        get => _timelineVersion;
        private set => SetProperty(ref _timelineVersion, value);
    }

    public bool TimelineShowsSystem
    {
        get => _timelineShowsSystem;
        set
        {
            if (SetProperty(ref _timelineShowsSystem, value))
            {
                OnPropertyChanged(nameof(ActiveTimeline));
                OnPropertyChanged(nameof(TimelineShowsSession));
                OnPropertyChanged(nameof(TimelineEmptyText));
                TimelineVersion++;
            }
        }
    }

    public bool TimelineShowsSession
    {
        get => !TimelineShowsSystem;
        set => TimelineShowsSystem = !value;
    }

    public bool HasSessionTimeline => _sessionTimeline.Count > 0 || IsRecording;

    public string TimelineEmptyText => TimelineShowsSystem
        ? GetText("Ui_TimelineWaitingSystem")
        : GetText("Ui_TimelineWaitingSession");

    public bool IsTargetPickerOpen
    {
        get => _isTargetPickerOpen;
        set => SetProperty(ref _isTargetPickerOpen, value);
    }

    // ---- Target card ----

    public string LiveTargetNameText
    {
        get
        {
            var path = LiveTargetIconPath;
            if (IsRecording)
            {
                return string.IsNullOrWhiteSpace(path) ? _activeTargetName : Path.GetFileName(path);
            }

            return LiveSessionTargetText;
        }
    }

    public string LiveTargetPathText => LiveTargetIconPath ?? (IsRecording || CanStart
        ? GetText("Ui_LiveTargetPathUnknown")
        : GetText("Ui_LiveTargetPickHint"));

    public string? LiveTargetIconPath
    {
        get
        {
            if (IsRecording)
            {
                var targetPath = _activeHandle?.Target.ExecutablePath;
                return IsUsablePath(targetPath) ? targetPath : FindRunningProcessPath(_liveRootProcessId);
            }

            if (IsExecutableMode)
            {
                return IsUsablePath(SelectedExecutablePath) ? SelectedExecutablePath : null;
            }

            if (IsAssignedAppsMode)
            {
                return FindRunningProcessPath(SelectedAssignedTarget?.RunningProcessId);
            }

            return FindRunningProcessPath(SelectedRunningTarget?.ProcessId);
        }
    }

    public string LivePidText
    {
        get
        {
            int? processId = IsRecording
                ? _liveRootProcessId
                : IsRunningProcessMode
                    ? SelectedRunningTarget?.ProcessId
                    : IsAssignedAppsMode ? SelectedAssignedTarget?.RunningProcessId : null;
            return processId?.ToString(CultureInfo.CurrentCulture) ?? "—";
        }
    }

    public string LiveElapsedText => IsRecording && _activeStartedAt is not null
        ? FormatClock(DateTimeOffset.UtcNow - _activeStartedAt.Value)
        : "00:00:00";

    public string LiveSamplesText => IsRecording ? _liveSampleCount.ToString("N0", CultureInfo.CurrentCulture) : "—";

    public string LiveEventsText => IsRecording ? _liveEventCount.ToString("N0", CultureInfo.CurrentCulture) : "—";

    public IReadOnlyList<string> ChildScopeOptions => _childScopeOptions ??=
        [GetText("Ui_ChildScopeIncludeAll"), GetText("Ui_ChildScopeRootOnly")];

    private IReadOnlyList<string>? _childScopeOptions;

    public int SelectedChildScopeIndex
    {
        get => IncludeChildProcesses ? 0 : 1;
        set
        {
            var include = value != 1;
            if (include != IncludeChildProcesses)
            {
                IncludeChildProcesses = include;
            }

            OnPropertyChanged();
        }
    }

    // ---- Top processes (system context) ----

    public IReadOnlyList<string> LiveTopSortOptions => _liveTopSortOptions ??=
    [
        GetText("Ui_SortByCpu"),
        GetText("Ui_SortByGpu"),
        GetText("Ui_SortByRam"),
        GetText("Ui_SortByDisk")
    ];

    private IReadOnlyList<string>? _liveTopSortOptions;

    public int SelectedLiveTopSortIndex
    {
        get => _selectedLiveTopSortIndex;
        set
        {
            if (SetProperty(ref _selectedLiveTopSortIndex, value))
            {
                RefreshLiveTopProcesses();
            }
        }
    }

    private void RefreshLiveTopProcesses()
    {
        var rows = _allGlobalProcessRows;
        IEnumerable<GlobalProcessRowViewModel> ordered = SelectedLiveTopSortIndex switch
        {
            1 => rows.OrderByDescending(row => row.GpuPercent ?? 0).ThenByDescending(row => row.GpuDedicatedMb ?? 0),
            2 => rows.OrderByDescending(row => row.MemoryMb ?? 0),
            3 => rows.OrderByDescending(row => row.DiskTotalMbPerSec),
            _ => rows.OrderByDescending(row => row.CpuPercent ?? 0).ThenByDescending(row => row.MemoryMb ?? 0)
        };
        LiveTopProcesses.ReplaceWith(ordered.Take(LiveTopProcessCount));
    }

    // ---- RAM diagnostic (whole system) ----

    private double SystemMemoryTotalMb => _systemLoad?.MemoryTotalMb is > 0 ? _systemLoad.MemoryTotalMb!.Value : _fallbackMemoryTotalMb;

    public string RamTotalText => FormatGigabytes(SystemMemoryTotalMb);

    public string RamInUseText => _systemLoad?.MemoryUsedMb is double used
        ? $"{FormatGigabytes(used)} ({used / SystemMemoryTotalMb * 100:N0}%)"
        : "—";

    public string RamAvailableText => _systemLoad?.MemoryUsedMb is double used
        ? $"{FormatGigabytes(SystemMemoryTotalMb - used)} ({(SystemMemoryTotalMb - used) / SystemMemoryTotalMb * 100:N0}%)"
        : "—";

    public string RamPeakLabel => IsRecording ? GetText("Ui_RamPeakSession") : GetText("Ui_RamPeakSinceStart");

    public string RamPeakText
    {
        get
        {
            var peak = IsRecording ? _ramPeakSessionMb : _ramPeakSinceStartMb;
            return peak <= 0 ? "—" : $"{FormatGigabytes(peak)} ({peak / SystemMemoryTotalMb * 100:N0}%)";
        }
    }

    public string RamCommitText => _systemLoad?.CommitUsedMb is double used && _systemLoad.CommitLimitMb is double limit && limit > 0
        ? $"{FormatGigabytes(used)} / {FormatGigabytes(limit)}"
        : "—";

    public string RamPressureTone
    {
        get
        {
            var load = _systemLoad?.MemoryUsedMb is double used ? used / SystemMemoryTotalMb * 100 : 0;
            return load >= 90 ? "bad" : load >= 75 ? "warn" : "good";
        }
    }

    public string RamPressureText => _systemLoad?.MemoryUsedMb is null
        ? "—"
        : RamPressureTone switch
        {
            "bad" => GetText("Ui_RamPressureHigh"),
            "warn" => GetText("Ui_RamPressureElevated"),
            _ => GetText("Ui_RamPressureNormal")
        };

    public double RamUsedPercent => _systemLoad?.MemoryUsedMb is double used ? used / SystemMemoryTotalMb * 100 : 0;

    // ---- Session summary ----

    public bool HasSummarySession => IsRecording || _lastCompletedSessionRecord is not null;

    public string SummaryStatusText => IsRecording || _lastCompletedSessionRecord is null
        ? LiveSessionStateText
        : new SessionListItemViewModel(_lastCompletedSessionRecord).StatusText;

    public string SummaryStartText
    {
        get
        {
            var started = IsRecording ? _activeStartedAt : _lastCompletedSessionRecord?.StartedAt;
            return started is null ? "—" : started.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        }
    }

    public string SummaryTargetText
    {
        get
        {
            if (IsRecording)
            {
                return _liveRootProcessId is int pid ? $"{LiveTargetNameText} (PID {pid})" : LiveTargetNameText;
            }

            return _lastCompletedSessionRecord?.Target.DisplayName ?? "—";
        }
    }

    public string SummaryChildText
    {
        get
        {
            var include = IsRecording || _lastCompletedSessionRecord is null
                ? IncludeChildProcesses
                : _lastCompletedSessionRecord.Target.IncludeChildProcesses;
            if (!include)
            {
                return GetText("Ui_ChildScopeRootOnly");
            }

            return IsRecording && _liveTrackedProcessCount > 0
                ? FormatText("Ui_SummaryChildIncludedCount", _liveTrackedProcessCount)
                : GetText("Ui_SummaryChildIncluded");
        }
    }

    public string SummaryDurationText => IsRecording
        ? LiveElapsedText
        : _lastCompletedSessionRecord is null ? "—" : FormatClock(_lastCompletedSessionRecord.Summary.Duration);

    /// <summary>The row is already labelled, so only the profile part of the source label is shown.</summary>
    public string SummaryProfileText
    {
        get
        {
            var label = LocalizeThresholdSourceLabel(IsRecording || _lastCompletedSessionRecord is null
                ? ActiveThresholdSourceText
                : _lastCompletedSessionRecord.Sampling.ThresholdSourceLabel ?? string.Empty);
            var prefix = GetText("Ui_SessionProfile") + ": ";
            return label.StartsWith(prefix, StringComparison.Ordinal) ? label[prefix.Length..] : label;
        }
    }

    public string SummarySamplesText => IsRecording
        ? _liveSampleCount.ToString("N0", CultureInfo.CurrentCulture)
        : _lastCompletedSessionRecord?.Summary.SampleCount.ToString("N0", CultureInfo.CurrentCulture) ?? "—";

    public string SummaryEventsText => IsRecording
        ? _liveEventCount.ToString("N0", CultureInfo.CurrentCulture)
        : _lastCompletedSessionRecord?.Summary.EventCount.ToString("N0", CultureInfo.CurrentCulture) ?? "—";

    // ---- Feeding ----

    private void ResetSessionTimeline()
    {
        _sessionTimeline.Reset(DateTimeOffset.UtcNow);
        _ramPeakSessionMb = 0;
        TimelineShowsSystem = false;
        OnPropertyChanged(nameof(HasSessionTimeline));
        TimelineVersion++;
    }

    /// <summary>Sampling thread.</summary>
    private void AppendSessionTimeline(MetricSample sample)
    {
        var values = sample.Values;
        var ramMb = ValueOrNaN(values, MetricKey.MemoryMb);
        var hasDisk = values.ContainsKey(MetricKey.DiskReadMbPerSec) || values.ContainsKey(MetricKey.DiskWriteMbPerSec);
        values.TryGetValue(MetricKey.DiskReadMbPerSec, out var read);
        values.TryGetValue(MetricKey.DiskWriteMbPerSec, out var write);
        _sessionTimeline.Add(new TimelinePoint(
            sample.ElapsedMs / 1000d,
            ValueOrNaN(values, MetricKey.CpuPercent),
            ValueOrNaN(values, MetricKey.GpuPercent),
            double.IsFinite(ramMb) ? ramMb / SystemMemoryTotalMb * 100 : double.NaN,
            ramMb,
            hasDisk ? read + write : double.NaN));
    }

    private void AppendSessionMarkers(IReadOnlyList<PerformanceEvent> events)
    {
        foreach (var performanceEvent in events)
        {
            _sessionTimeline.AddMarker(new TimelineMarker(
                performanceEvent.ElapsedMs / 1000d,
                performanceEvent.Severity switch
                {
                    EventSeverity.Critical => 2,
                    EventSeverity.Warning => 1,
                    _ => 0
                },
                performanceEvent.Title));
        }
    }

    /// <summary>Scan thread: whole-system point for the idle dashboard and RAM peaks.</summary>
    private void AppendSystemTimeline(Domain.Abstractions.GlobalProcessScan scan, IReadOnlyList<GlobalProcessRowViewModel> rows)
    {
        double read = 0, write = 0;
        foreach (var row in rows)
        {
            read += row.DiskReadMbPerSec ?? 0;
            write += row.DiskWriteMbPerSec ?? 0;
        }

        _systemDiskReadMbPerSec = read;
        _systemDiskWriteMbPerSec = write;
        var system = scan.System;
        var total = system?.MemoryTotalMb is > 0 ? system.MemoryTotalMb.Value : _fallbackMemoryTotalMb;
        var used = system?.MemoryUsedMb ?? double.NaN;
        if (double.IsFinite(used))
        {
            _ramPeakSinceStartMb = Math.Max(_ramPeakSinceStartMb, used);
            if (IsRecording)
            {
                _ramPeakSessionMb = Math.Max(_ramPeakSessionMb, used);
            }
        }

        _systemTimeline.Add(new TimelinePoint(
            (scan.CapturedAt - _systemTimeline.Origin).TotalSeconds,
            system?.CpuPercent ?? double.NaN,
            system?.GpuPercent ?? double.NaN,
            double.IsFinite(used) ? used / total * 100 : double.NaN,
            used,
            scan.ComparedToPreviousScan is null ? double.NaN : read + write));
    }

    /// <summary>UI thread, after each scan: refresh what the Live dashboard shows from system data.</summary>
    private void OnSystemScanAppliedForLive()
    {
        if (!IsLiveSurfaceVisible)
        {
            return;
        }

        RefreshLiveTopProcesses();
        if (!IsRecording)
        {
            RefreshLiveSurface();
        }
        else
        {
            NotifyRamDiagnosticProperties();
            if (TimelineShowsSystem)
            {
                TimelineVersion++;
            }
        }
    }

    private void ApplySystemTiles()
    {
        var system = _systemLoad;
        var history = _systemTimeline.GetWindow(LiveMetricHistory.Capacity * 6, LiveMetricHistory.Capacity).Points;
        foreach (var tile in LiveTiles)
        {
            tile.Tone = "normal";
            switch (tile.Key)
            {
                case "cpu":
                    tile.IsAvailable = system?.CpuPercent is not null;
                    tile.ValueText = system?.CpuPercent is double cpu ? $"{cpu:N1}%" : "—";
                    tile.SubValueText = string.Empty;
                    tile.DetailText = GetText("Ui_TileWholeSystem");
                    tile.Detail2Text = system is null ? string.Empty : FormatText("Ui_TileProcessCount", system.ProcessCount);
                    tile.ScaleMaximum = 100;
                    tile.History = history.Select(point => point.Cpu).Where(double.IsFinite).ToArray();
                    break;
                case "gpu":
                    tile.IsAvailable = HasSystemGpu;
                    tile.ValueText = HasSystemGpu && system?.GpuPercent is double gpu ? $"{gpu:N1}%" : "—";
                    tile.SubValueText = string.Empty;
                    tile.DetailText = FormatText("Ui_TileVram", SystemVramText);
                    tile.Detail2Text = HasSystemGpuTemperature ? SystemGpuTemperatureText : string.Empty;
                    tile.ScaleMaximum = 100;
                    tile.History = history.Select(point => point.Gpu).Where(double.IsFinite).ToArray();
                    break;
                case "ram":
                    tile.IsAvailable = system?.MemoryUsedMb is not null;
                    tile.ValueText = system?.MemoryUsedMb is double used ? FormatGigabytes(used) : "—";
                    tile.SubValueText = $"/ {FormatGigabytes(SystemMemoryTotalMb)}";
                    tile.DetailText = system?.MemoryUsedMb is double usedMb ? FormatText("Ui_TileRamUsedShare", usedMb / SystemMemoryTotalMb * 100) : string.Empty;
                    tile.Detail2Text = GetText("Ui_TileWholeSystem");
                    tile.ScaleMaximum = 100;
                    tile.History = history.Select(point => point.RamPercent).Where(double.IsFinite).ToArray();
                    break;
                case "disk":
                    var hasDisk = _systemTimeline.Count > 1;
                    tile.IsAvailable = hasDisk;
                    tile.ValueText = hasDisk ? $"{_systemDiskReadMbPerSec + _systemDiskWriteMbPerSec:N1} MB/s" : "—";
                    tile.SubValueText = string.Empty;
                    tile.DetailText = FormatText("Ui_TileDiskRead", _systemDiskReadMbPerSec);
                    tile.Detail2Text = FormatText("Ui_TileDiskWrite", _systemDiskWriteMbPerSec);
                    tile.ScaleMaximum = 0;
                    tile.History = history.Select(point => point.Disk).Where(double.IsFinite).ToArray();
                    break;
            }
        }
    }

    private void NotifyRamDiagnosticProperties()
    {
        OnPropertyChanged(nameof(RamTotalText));
        OnPropertyChanged(nameof(RamInUseText));
        OnPropertyChanged(nameof(RamAvailableText));
        OnPropertyChanged(nameof(RamPeakLabel));
        OnPropertyChanged(nameof(RamPeakText));
        OnPropertyChanged(nameof(RamCommitText));
        OnPropertyChanged(nameof(RamPressureText));
        OnPropertyChanged(nameof(RamPressureTone));
        OnPropertyChanged(nameof(RamUsedPercent));
    }

    private void NotifyLiveDashboardProperties()
    {
        OnPropertyChanged(nameof(LiveTargetNameText));
        OnPropertyChanged(nameof(LiveTargetPathText));
        OnPropertyChanged(nameof(LiveTargetIconPath));
        OnPropertyChanged(nameof(LivePidText));
        OnPropertyChanged(nameof(LiveElapsedText));
        OnPropertyChanged(nameof(LiveSamplesText));
        OnPropertyChanged(nameof(LiveEventsText));
        OnPropertyChanged(nameof(SummaryDurationText));
        OnPropertyChanged(nameof(SelectedChildScopeIndex));
        OnPropertyChanged(nameof(HasSessionTimeline));
        OnPropertyChanged(nameof(HasSummarySession));
        OnPropertyChanged(nameof(SummaryStatusText));
        OnPropertyChanged(nameof(SummaryStartText));
        OnPropertyChanged(nameof(SummaryTargetText));
        OnPropertyChanged(nameof(SummaryChildText));
        OnPropertyChanged(nameof(SummaryProfileText));
        OnPropertyChanged(nameof(SummarySamplesText));
        OnPropertyChanged(nameof(SummaryEventsText));
        NotifyRamDiagnosticProperties();
    }

    private string? FindRunningProcessPath(int? processId)
    {
        if (processId is null)
        {
            return null;
        }

        var row = _allGlobalProcessRows.FirstOrDefault(item => item.ProcessId == processId.Value);
        return IsUsablePath(row?.FullPath) ? row!.FullPath : null;
    }

    private static bool IsUsablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);

    private static double ValueOrNaN(IReadOnlyDictionary<MetricKey, double> values, MetricKey key) =>
        values.TryGetValue(key, out var value) && double.IsFinite(value) ? value : double.NaN;

    private static string FormatGigabytes(double megabytes) =>
        megabytes >= 1024 ? $"{megabytes / 1024:N1} GB" : $"{megabytes:N0} MB";

    private static string FormatClock(TimeSpan duration) =>
        $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
}
