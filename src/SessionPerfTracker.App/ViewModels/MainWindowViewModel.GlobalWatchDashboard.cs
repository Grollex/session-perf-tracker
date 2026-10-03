using System.Globalization;

namespace SessionPerfTracker.App.ViewModels;

/// <summary>Global Watch overview: toolbar filters, inspector details and the three summary panels.</summary>
public sealed partial class MainWindowViewModel
{
    private const int DashboardListSize = 5;
    private bool _globalWatchAutoRefresh = true;
    private bool _globalWatchOnlySuspicious;
    private int _selectedTopConsumerIndex;

    public RangeObservableCollection<GlobalProcessRowViewModel> TopConsumers { get; } = new();
    public RangeObservableCollection<GlobalProcessRowViewModel> RecentlyStartedProcesses { get; } = new();
    public RangeObservableCollection<GlobalWatchJournalEntryViewModel> RecentWatchAlerts { get; } = new();

    private string GlobalSortStarted => GetText("Ui_Started");

    public string GlobalWatchStartedHeaderText => FormatGlobalWatchSortHeader(GetText("Ui_Started"), GlobalSortStarted);

    public void SortGlobalWatchByStarted() => ToggleGlobalWatchSort(GlobalSortStarted);

    /// <summary>Shows the newest processes first (Recently started → View all).</summary>
    public void ShowNewestGlobalProcesses()
    {
        if (!string.Equals(SelectedGlobalWatchSortMode, GlobalSortStarted, StringComparison.Ordinal) || !_globalWatchSortDescending)
        {
            _selectedGlobalWatchSortMode = GlobalSortStarted;
            _globalWatchSortDescending = true;
            OnPropertyChanged(nameof(SelectedGlobalWatchSortMode));
            ApplyGlobalWatchFilterAndSort();
            NotifyGlobalWatchSortHeaderProperties();
        }
    }

    public bool GlobalWatchAutoRefresh
    {
        get => _globalWatchAutoRefresh;
        set
        {
            if (SetProperty(ref _globalWatchAutoRefresh, value) && value)
            {
                ApplyGlobalWatchFilterAndSort();
            }
        }
    }

    public string GlobalWatchAutoRefreshText => FormatText("Ui_GwAutoRefreshInterval", GlobalWatchIntervalMs / 1000d);

    public string GlobalWatchLastScanShortText => _lastSuccessfulGlobalWatchScanAt is { } scannedAt
        ? scannedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)
        : "—";

    public bool GlobalWatchOnlySuspicious
    {
        get => _globalWatchOnlySuspicious;
        set
        {
            if (SetProperty(ref _globalWatchOnlySuspicious, value))
            {
                ApplyGlobalWatchFilterAndSort();
            }
        }
    }

    public IReadOnlyList<string> GlobalWatchStateFilterOptions => _globalWatchStateFilterOptions ??=
    [
        GetText("Ui_GwFilterAllStates"),
        GetText("Ui_OnlyOverLimit"),
        GetText("Ui_OnlyCritical"),
        GetText("Ui_OnlyNearLimit"),
        GetText("Ui_OnlyUnassigned")
    ];

    private IReadOnlyList<string>? _globalWatchStateFilterOptions;

    /// <summary>One dropdown instead of four chips; the underlying flags stay for compatibility.</summary>
    public int SelectedGlobalWatchStateFilterIndex
    {
        get => _globalWatchOnlyOverLimit ? 1
            : _globalWatchOnlyCritical ? 2
            : _globalWatchOnlyNearLimit ? 3
            : _globalWatchOnlyUnassigned ? 4
            : 0;
        set
        {
            _globalWatchOnlyOverLimit = value == 1;
            _globalWatchOnlyCritical = value == 2;
            _globalWatchOnlyNearLimit = value == 3;
            _globalWatchOnlyUnassigned = value == 4;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GlobalWatchOnlyOverLimit));
            OnPropertyChanged(nameof(GlobalWatchOnlyCritical));
            OnPropertyChanged(nameof(GlobalWatchOnlyNearLimit));
            OnPropertyChanged(nameof(GlobalWatchOnlyUnassigned));
            ApplyGlobalWatchFilterAndSort();
        }
    }

    public string GlobalWatchProcessesTitle => FormatText(
        IsGlobalWatchGroupedMode ? "Ui_GwApplicationsTitle" : "Ui_GwProcessesTitle",
        GlobalWatchProcesses.Count);

    public IReadOnlyList<string> TopConsumerOptions => LiveTopSortOptions;

    public int SelectedTopConsumerIndex
    {
        get => _selectedTopConsumerIndex;
        set
        {
            if (SetProperty(ref _selectedTopConsumerIndex, value))
            {
                RefreshTopConsumers(IsGlobalWatchGroupedMode ? _allGlobalGroupRows : _allGlobalProcessRows);
            }
        }
    }

    private bool IsSuspiciousRow(GlobalProcessRowViewModel row, HashSet<string> suspiciousPaths) =>
        row.NormalizedFullPath is { } path && suspiciousPaths.Contains(path);

    private IEnumerable<GlobalProcessRowViewModel> FilterSuspiciousRows(IEnumerable<GlobalProcessRowViewModel> rows)
    {
        var suspiciousPaths = _thresholdSettingsStore.Current.SuspiciousWatchlist.Items
            .Select(item => item.NormalizedPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.Where(row => IsSuspiciousRow(row, suspiciousPaths));
    }

    private void RefreshGlobalWatchDashboardLists(
        IReadOnlyList<GlobalProcessRowViewModel> displayRows,
        IReadOnlyList<GlobalProcessRowViewModel> processRows)
    {
        RefreshTopConsumers(displayRows);
        RecentlyStartedProcesses.ReplaceWith(processRows
            .Where(row => row.StartedAt is not null)
            .OrderByDescending(row => row.StartedAt)
            .Take(DashboardListSize));
        OnPropertyChanged(nameof(GlobalWatchProcessesTitle));
    }

    private void RefreshTopConsumers(IReadOnlyList<GlobalProcessRowViewModel> rows)
    {
        IEnumerable<GlobalProcessRowViewModel> ordered = SelectedTopConsumerIndex switch
        {
            1 => rows.OrderByDescending(row => row.GpuPercent ?? 0).ThenByDescending(row => row.GpuDedicatedMb ?? 0),
            2 => rows.OrderByDescending(row => row.MemoryMb ?? 0),
            3 => rows.OrderByDescending(row => row.DiskTotalMbPerSec),
            _ => rows.OrderByDescending(row => row.CpuPercent ?? 0).ThenByDescending(row => row.MemoryMb ?? 0)
        };
        TopConsumers.ReplaceWith(ordered.Take(DashboardListSize));
    }

    private void RefreshRecentWatchAlerts() =>
        RecentWatchAlerts.ReplaceWith(GlobalWatchJournalEntries.Take(DashboardListSize));

    // ---- Inspector panel extras ----

    public string? SelectedGlobalProcessIconPath => SelectedGlobalProcess?.IconPath;

    public string SelectedGlobalProcessNameText => SelectedGlobalProcess is null
        ? GetText("Ui_StNoProcessSelected")
        : SelectedGlobalProcess.IsGroup ? SelectedGlobalProcess.AppName : SelectedGlobalProcess.Name;

    public string SelectedGlobalProcessSubtitleText => SelectedGlobalProcess is null
        ? GetText("Ui_StSelectRowHint")
        : SelectedGlobalProcess.IsGroup
            ? FormatText("Ui_GenGroupInstances", SelectedGlobalProcess.InstanceCount, SelectedGlobalProcess.IncludedProcesses.Count)
            : $"PID {SelectedGlobalProcess.ProcessId} · {SelectedGlobalProcess.ExeName}";

    public string SelectedGlobalProcessHealthTag => SelectedGlobalProcess?.HealthBadgeText ?? "OK";

    public string SelectedGlobalProcessHealthDisplayText => SelectedGlobalProcess?.HealthDisplayText ?? string.Empty;

    public string SelectedGlobalProcessStartedText
    {
        get
        {
            if (SelectedGlobalProcess?.StartedAt is not { } started)
            {
                return "—";
            }

            var age = DateTimeOffset.UtcNow - started;
            var ago = age.TotalMinutes < 1
                ? GetText("Ui_JustNow")
                : age.TotalHours < 1
                    ? FormatText("Ui_MinutesAgo", (int)age.TotalMinutes)
                    : age.TotalDays < 1
                        ? FormatText("Ui_HoursAgo", (int)age.TotalHours)
                        : FormatText("Ui_DaysAgo", (int)age.TotalDays);
            return $"{started.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} ({ago})";
        }
    }

    public string SelectedGlobalProcessChildCountText => SelectedGlobalProcess is null
        ? "—"
        : SelectedGlobalProcess.IsGroup
            ? SelectedGlobalProcess.IncludedProcesses.Count.ToString(CultureInfo.CurrentCulture)
            : SelectedGlobalProcess.DescendantCountText;

    public double SelectedGlobalProcessCpuPercent => SelectedGlobalProcess?.CpuPercent ?? 0;
    public double SelectedGlobalProcessGpuPercent => SelectedGlobalProcess?.GpuPercent ?? 0;

    public double SelectedGlobalProcessRamPercent => SelectedGlobalProcess?.MemoryMb is double memory
        ? memory / SystemMemoryTotalMb * 100
        : 0;

    /// <summary>Disk meter: 50 MB/s fills the bar.</summary>
    public double SelectedGlobalProcessDiskPercent => Math.Min(100, (SelectedGlobalProcess?.DiskTotalMbPerSec ?? 0) * 2);

    public string SelectedGlobalProcessRamShareText => SelectedGlobalProcess?.MemoryMb is double memory
        ? $"{SelectedGlobalProcess.MemoryText} ({memory / SystemMemoryTotalMb * 100:N1}%)"
        : "—";

    private void NotifyInspectorDashboardProperties()
    {
        OnPropertyChanged(nameof(SelectedGlobalProcessIconPath));
        OnPropertyChanged(nameof(SelectedGlobalProcessNameText));
        OnPropertyChanged(nameof(SelectedGlobalProcessSubtitleText));
        OnPropertyChanged(nameof(SelectedGlobalProcessHealthTag));
        OnPropertyChanged(nameof(SelectedGlobalProcessHealthDisplayText));
        OnPropertyChanged(nameof(SelectedGlobalProcessStartedText));
        OnPropertyChanged(nameof(SelectedGlobalProcessChildCountText));
        OnPropertyChanged(nameof(SelectedGlobalProcessCpuPercent));
        OnPropertyChanged(nameof(SelectedGlobalProcessGpuPercent));
        OnPropertyChanged(nameof(SelectedGlobalProcessRamPercent));
        OnPropertyChanged(nameof(SelectedGlobalProcessDiskPercent));
        OnPropertyChanged(nameof(SelectedGlobalProcessRamShareText));
    }
}
