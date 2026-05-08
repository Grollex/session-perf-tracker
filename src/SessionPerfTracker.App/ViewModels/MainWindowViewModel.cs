using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using SessionPerfTracker.Domain.Metrics;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace SessionPerfTracker.App.ViewModels;

public enum UpdatePromptChoice
{
    UpdateNow,
    Later,
    SkipVersion
}

public sealed class UpdateAvailablePromptEventArgs : EventArgs
{
    public UpdateAvailablePromptEventArgs(UpdateCheckResult result)
    {
        Result = result;
    }

    public UpdateCheckResult Result { get; }
    public UpdatePromptChoice Choice { get; set; } = UpdatePromptChoice.Later;
}

public sealed class MainWindowViewModel : ObservableObject
{
    private const double AvgCpuBudgetPercent = 2;
    private const double PeakRamBudgetMb = 150;
    private const int SelfMonitoringIntervalMs = 5000;
    private const int MaxSelfMonitoringSamples = 120;
    private const int GlobalWatchIntervalMs = 3000;
    private const int LiveTabIndex = 0;
    private const int GlobalWatchTabIndex = 1;
    private const int SessionsTabIndex = 2;
    private const int SessionDetailsTabIndex = 3;
    private const int GlobalWatchOverviewSectionIndex = 0;
    private const int GlobalWatchJournalSectionIndex = 1;
    private const int GlobalWatchRecommendationsSectionIndex = 2;
    private const int GlobalWatchSuspiciousSectionIndex = 3;
    private const int GlobalWatchJournalUiLimit = 120;
    private static readonly TimeSpan GlobalWatchJournalCooldown = TimeSpan.FromSeconds(60);
    private const string TargetModeRunningProcess = "Running process";
    private const string TargetModeAssignedApps = "Assigned apps / profiles";
    private const string TargetModeExecutable = "Executable";
    private const string GlobalSortCpu = "CPU";
    private const string GlobalSortRam = "RAM";
    private const string GlobalSortDisk = "Disk";
    private const string GlobalSortName = "Name";
    private const string GlobalModeApplications = "Applications";
    private const string GlobalModeProcesses = "Processes";

    private enum LiveSessionUiState
    {
        ReadyToStart,
        Recording,
        StoppedManually,
        Stopped,
        EndedUnexpectedly,
        ChangesPending
    }

    private readonly ISessionStore _sessionStore;
    private readonly IComparisonEngine _comparisonEngine;
    private readonly IProcessTargetResolver _targetResolver;
    private readonly ITargetSessionRunner _sessionRunner;
    private readonly IRamAccountingDiagnosticProvider _ramDiagnosticProvider;
    private readonly IThresholdSettingsStore _thresholdSettingsStore;
    private readonly ISpikeContextProvider _spikeContextProvider;
    private readonly ISelfMonitoringProvider _selfMonitoringProvider;
    private readonly IGlobalProcessScanner _globalProcessScanner;
    private readonly IProcessControlService _processControlService;
    private readonly IExportService _exportService;
    private readonly IUpdateService _updateService;
    private readonly string _defaultExportDirectory;
    private readonly string _updateDownloadDirectory;
    private readonly List<SelfMonitoringSample> _selfMonitoringSamples = [];
    private readonly List<GlobalProcessRowViewModel> _allGlobalProcessRows = [];
    private readonly Dictionary<string, List<DateTimeOffset>> _globalWatchOverLimitWarnings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _globalWatchJournalLastEntryByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _runningSuspiciousPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _runningBannedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _globalWatchScanLock = new(1, 1);
    private SessionListItemViewModel? _selectedSession;
    private SessionListItemViewModel? _compareLeft;
    private SessionListItemViewModel? _compareRight;
    private RetentionOptionViewModel? _selectedRetentionOption;
    private SessionProfileFilterOptionViewModel? _selectedSessionProfileFilter;
    private ThresholdProfileOptionViewModel? _selectedThresholdProfile;
    private ThresholdProfileOptionViewModel? _selectedAssignmentProfile;
    private ThresholdProfileOptionViewModel? _selectedLiveAssignmentProfile;
    private SessionProfileOptionViewModel? _selectedSessionProfileOption;
    private AppProfileAssignmentViewModel? _selectedAppProfileAssignment;
    private ExportFileItemViewModel? _selectedExportFile;
    private ProfileRecommendationViewModel? _selectedProfileRecommendation;
    private DeniedProfileRecommendationViewModel? _selectedDeniedProfileRecommendation;
    private SuspiciousWatchItemViewModel? _selectedSuspiciousWatchItem;
    private ProcessBanRuleViewModel? _selectedProcessBan;
    private ProcessBanDurationOptionViewModel? _selectedProcessBanDurationOption;
    private TargetOptionViewModel? _selectedRunningTarget;
    private AssignedTargetOptionViewModel? _selectedAssignedTarget;
    private GlobalProcessRowViewModel? _selectedGlobalProcess;
    private ProcessInspectorTargetViewModel? _processInspectorTarget;
    private SamplingOptionViewModel _selectedSamplingOption;
    private readonly List<TargetOptionViewModel> _allRunningTargets = [];
    private readonly List<SessionListItemViewModel> _allSessionItems = [];
    private string _storagePath = string.Empty;
    private string _selectedExecutablePath = string.Empty;
    private string _selectedTargetMode = TargetModeRunningProcess;
    private string _processFilterText = string.Empty;
    private string _sessionSearchText = string.Empty;
    private string _recordingStatusText = "Idle";
    private string _storageStatusText = "SQLite storage ready.";
    private string _liveSnapshotText = "idle";
    private string _ramDiagnosticText = "No RAM diagnostic snapshot captured.";
    private string _systemContextText = "No system context snapshot captured.";
    private string _cpuThresholdPercentText = "80";
    private string _ramThresholdMbText = "4096";
    private string _diskReadThresholdMbPerSecText = "180";
    private string _diskWriteThresholdMbPerSecText = "120";
    private string _profileCpuThresholdPercentText = "75";
    private string _profileRamThresholdMbText = "4000";
    private string _profileDiskReadThresholdMbPerSecText = "180";
    private string _profileDiskWriteThresholdMbPerSecText = "120";
    private string _startupGraceSecondsText = "15";
    private string _eventCooldownSecondsText = "30";
    private string _groupingWindowSecondsText = "2";
    private string _snapshotSuppressionSecondsText = "3";
    private string _assignmentExeNameText = "opera.exe";
    private string _thresholdSettingsStatusText = "Detector thresholds are loaded from settings.";
    private string _exportStatusText = "Exports are saved to LocalAppData\\SessionPerfTracker\\exports.";
    private string _exportDirectoryText = string.Empty;
    private string _updateManifestUrlText = string.Empty;
    private string _updateStatusText = "Update checks are manual unless automatic checks are enabled.";
    private string _updateLatestVersionText = "Not checked";
    private string _updateReleaseNotesText = "No update checked yet.";
    private string _downloadedUpdateInstallerPath = string.Empty;
    private string _liveAssignmentStatusText = "Select a target and profile to assign thresholds.";
    private string _liveWarningText = string.Empty;
    private string _selfMonitoringStatusText = "warming up";
    private string _selfCurrentCpuText = "n/a";
    private string _selfAvgCpuText = "n/a";
    private string _selfPeakCpuText = "n/a";
    private string _selfCurrentRamText = "n/a";
    private string _selfAvgRamText = "n/a";
    private string _selfPeakRamText = "n/a";
    private string _selfDiskWriteText = "n/a";
    private string _selfSampleCountText = "0";
    private string _selfCpuBudgetStatusText = "unknown";
    private string _selfRamBudgetStatusText = "unknown";
    private string _selfWritesBudgetStatusText = "configured";
    private string _selfSnapshotsBudgetStatusText = "event-only";
    private string _globalWatchFilterText = string.Empty;
    private string _globalWatchStatusText = "Global Watch is a lightweight process overview.";
    private string _globalWatchLastScanText = "Waiting for first scan";
    private string _globalWatchJournalStatusText = "Watch Journal records profile-aware watch states over time.";
    private string _profileRecommendationStatusText = "Recommendations appear after repeated over-limit warnings.";
    private string _suspiciousWatchStatusText = "Mark a selected process or application as suspicious to watch for future launches.";
    private string _processBanStatusText = "Process bans are enforced by Global Watch while this utility is running.";
    private string _selectedGlobalWatchSortMode = GlobalSortCpu;
    private string _selectedGlobalWatchMode = GlobalModeApplications;
    private string? _selectedGlobalProcessKey;
    private string _activeTargetName = string.Empty;
    private string _activeSessionProfileText = string.Empty;
    private string _lastCompletedSessionText = string.Empty;
    private string _lastCompletedSessionHint = string.Empty;
    private int _selectedTabIndex;
    private int _selectedGlobalWatchSectionIndex;
    private DateTimeOffset? _activeStartedAt;
    private int _liveSampleCount;
    private int _liveEventCount;
    private int _liveSpikeCount;
    private int _liveBreachCount;
    private int _liveTrackedProcessCount;
    private int? _liveRootProcessId;
    private bool _isRecording;
    private bool _includeChildProcesses = true;
    private bool _isLiveMonitorEnabled = true;
    private bool _captureCpu = true;
    private bool _captureRam = true;
    private bool _captureDiskRead = true;
    private bool _captureDiskWrite = true;
    private bool _automaticallyCheckForUpdates;
    private bool _isCheckingForUpdates;
    private bool _isUpdateAvailable;
    private bool _isUpdateRestartRequested;
    private bool _minimizeToTrayOnClose = true;
    private bool _globalWatchOnlyOverLimit;
    private bool _globalWatchOnlyCritical;
    private bool _globalWatchOnlyUnassigned;
    private bool _globalWatchOnlyNearLimit;
    private bool _autoStopInProgress;
    private bool _manualStopInProgress;
    private LiveSessionUiState _liveSessionState = LiveSessionUiState.ReadyToStart;
    private string _autoStopStatusText = "Monitoring stopped. Press Start to apply changes.";
    private IRunningSessionHandle? _activeHandle;
    private CancellationTokenSource? _selfMonitoringCts;
    private CancellationTokenSource? _globalWatchCts;
    private DateTimeOffset _lastLiveUiRefresh = DateTimeOffset.MinValue;
    private MetricCapabilities _liveCapabilities = new()
    {
        CpuPercent = MetricReliability.Stable,
        MemoryMb = MetricReliability.Stable,
        DiskReadMbPerSec = MetricReliability.BestEffort,
        DiskWriteMbPerSec = MetricReliability.BestEffort
    };
    private UpdateManifest? _availableUpdateManifest;

    public event EventHandler<UpdateAvailablePromptEventArgs>? UpdateAvailablePromptRequested;
    public event EventHandler? UpdateInstallerLaunched;

    public MainWindowViewModel(
        ISessionStore sessionStore,
        IComparisonEngine comparisonEngine,
        IProcessTargetResolver targetResolver,
        ITargetSessionRunner sessionRunner,
        IRamAccountingDiagnosticProvider ramDiagnosticProvider,
        IThresholdSettingsStore thresholdSettingsStore,
        ISpikeContextProvider spikeContextProvider,
        ISelfMonitoringProvider selfMonitoringProvider,
        IGlobalProcessScanner globalProcessScanner,
        IProcessControlService processControlService,
        IExportService exportService,
        IUpdateService updateService,
        string defaultExportDirectory,
        string updateDownloadDirectory)
    {
        _sessionStore = sessionStore;
        _comparisonEngine = comparisonEngine;
        _targetResolver = targetResolver;
        _sessionRunner = sessionRunner;
        _ramDiagnosticProvider = ramDiagnosticProvider;
        _thresholdSettingsStore = thresholdSettingsStore;
        _spikeContextProvider = spikeContextProvider;
        _selfMonitoringProvider = selfMonitoringProvider;
        _globalProcessScanner = globalProcessScanner;
        _processControlService = processControlService;
        _exportService = exportService;
        _updateService = updateService;
        _defaultExportDirectory = defaultExportDirectory;
        _updateDownloadDirectory = updateDownloadDirectory;
        _exportDirectoryText = defaultExportDirectory;
        SamplingOptions = [new SamplingOptionViewModel(1000), new SamplingOptionViewModel(500)];
        RetentionOptions =
        [
            new RetentionOptionViewModel(1, "1 day"),
            new RetentionOptionViewModel(7, "7 days"),
            new RetentionOptionViewModel(30, "30 days"),
            new RetentionOptionViewModel(90, "90 days"),
            new RetentionOptionViewModel(null, "Keep until manual delete")
        ];
        _selectedSamplingOption = SamplingOptions[0];
        ProcessBanDurationOptions =
        [
            new ProcessBanDurationOptionViewModel("5 seconds", TimeSpan.FromSeconds(5)),
            new ProcessBanDurationOptionViewModel("30 seconds", TimeSpan.FromSeconds(30)),
            new ProcessBanDurationOptionViewModel("1 minute", TimeSpan.FromMinutes(1)),
            new ProcessBanDurationOptionViewModel("Permanent", null)
        ];
        _selectedProcessBanDurationOption = ProcessBanDurationOptions[1];
    }

    public ObservableCollection<SessionListItemViewModel> Sessions { get; } = [];
    public ObservableCollection<TargetOptionViewModel> RunningTargets { get; } = [];
    public ObservableCollection<MetricValueViewModel> CurrentMetrics { get; } = [];
    public ObservableCollection<MetricSummaryRowViewModel> SelectedMetricSummaries { get; } = [];
    public ObservableCollection<EventRowViewModel> SelectedEvents { get; } = [];
    public ObservableCollection<SessionDetailFactViewModel> SessionDetailFacts { get; } = [];
    public ObservableCollection<MetricSummaryRowViewModel> SessionDetailMetricSummaries { get; } = [];
    public ObservableCollection<EventRowViewModel> SessionDetailEvents { get; } = [];
    public ObservableCollection<string> SessionDetailUnsupportedMetricNotices { get; } = [];
    public ObservableCollection<EventRowViewModel> VisibleEvents { get; } = [];
    public ObservableCollection<string> UnsupportedMetricNotices { get; } = [];
    public ObservableCollection<ComparisonMetricRowViewModel> ComparisonRows { get; } = [];
    public ObservableCollection<UnavailableComparisonMetricViewModel> UnavailableComparisonMetrics { get; } = [];
    public ObservableCollection<ThresholdProfileOptionViewModel> ThresholdProfiles { get; } = [];
    public ObservableCollection<SessionProfileOptionViewModel> SessionProfileOptions { get; } = [];
    public ObservableCollection<AppProfileAssignmentViewModel> AppProfileAssignments { get; } = [];
    public ObservableCollection<ExportFileItemViewModel> ExportFiles { get; } = [];
    public ObservableCollection<AssignedTargetOptionViewModel> AssignedTargets { get; } = [];
    public ObservableCollection<GlobalProcessRowViewModel> GlobalWatchProcesses { get; } = [];
    public ObservableCollection<GlobalProcessRowViewModel> TopCpuOffenders { get; } = [];
    public ObservableCollection<GlobalProcessRowViewModel> TopRamOffenders { get; } = [];
    public ObservableCollection<GlobalProcessRowViewModel> TopDiskOffenders { get; } = [];
    public ObservableCollection<ProfileRecommendationViewModel> ProfileRecommendations { get; } = [];
    public ObservableCollection<DeniedProfileRecommendationViewModel> DeniedProfileRecommendations { get; } = [];
    public ObservableCollection<GlobalWatchJournalEntryViewModel> GlobalWatchJournalEntries { get; } = [];
    public ObservableCollection<GlobalWatchJournalGroupViewModel> GlobalWatchJournalGroups { get; } = [];
    public ObservableCollection<SuspiciousWatchItemViewModel> SuspiciousWatchItems { get; } = [];
    public ObservableCollection<SuspiciousLaunchEntryViewModel> SuspiciousLaunchEntries { get; } = [];
    public ObservableCollection<ProcessBanRuleViewModel> ProcessBans { get; } = [];
    public ObservableCollection<ProcessBanEventViewModel> ProcessBanEvents { get; } = [];
    public ObservableCollection<SamplingOptionViewModel> SamplingOptions { get; }
    public ObservableCollection<ProcessBanDurationOptionViewModel> ProcessBanDurationOptions { get; }
    public ObservableCollection<RetentionOptionViewModel> RetentionOptions { get; }
    public ObservableCollection<SessionProfileFilterOptionViewModel> SessionProfileFilters { get; } = [];
    public IReadOnlyList<string> TargetModes { get; } = [TargetModeRunningProcess, TargetModeAssignedApps, TargetModeExecutable];
    public IReadOnlyList<string> GlobalWatchSortModes { get; } = [GlobalSortCpu, GlobalSortRam, GlobalSortDisk, GlobalSortName];
    public IReadOnlyList<string> GlobalWatchModes { get; } = [GlobalModeApplications, GlobalModeProcesses];

    public SessionListItemViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value))
            {
                RefreshSelectedSession();
            }
        }
    }

    public SessionListItemViewModel? CompareLeft
    {
        get => _compareLeft;
        set
        {
            if (SetProperty(ref _compareLeft, value))
            {
                RefreshComparison();
            }
        }
    }

    public SessionListItemViewModel? CompareRight
    {
        get => _compareRight;
        set
        {
            if (SetProperty(ref _compareRight, value))
            {
                RefreshComparison();
            }
        }
    }

    public RetentionOptionViewModel? SelectedRetentionOption
    {
        get => _selectedRetentionOption;
        set => SetProperty(ref _selectedRetentionOption, value);
    }

    public SessionProfileFilterOptionViewModel? SelectedSessionProfileFilter
    {
        get => _selectedSessionProfileFilter;
        set
        {
            if (SetProperty(ref _selectedSessionProfileFilter, value))
            {
                ApplySessionFilter();
            }
        }
    }

    public ThresholdProfileOptionViewModel? SelectedThresholdProfile
    {
        get => _selectedThresholdProfile;
        set
        {
            if (SetProperty(ref _selectedThresholdProfile, value))
            {
                LoadSelectedProfileFields();
            }
        }
    }

    public ThresholdProfileOptionViewModel? SelectedAssignmentProfile
    {
        get => _selectedAssignmentProfile;
        set => SetProperty(ref _selectedAssignmentProfile, value);
    }

    public ThresholdProfileOptionViewModel? SelectedLiveAssignmentProfile
    {
        get => _selectedLiveAssignmentProfile;
        set => SetProperty(ref _selectedLiveAssignmentProfile, value);
    }

    public SessionProfileOptionViewModel? SelectedSessionProfileOption
    {
        get => _selectedSessionProfileOption;
        set
        {
            var changedForSession = !SameSessionProfile(_selectedSessionProfileOption, value);
            if (SetProperty(ref _selectedSessionProfileOption, value))
            {
                if (changedForSession)
                {
                    StopForCaptureChange("session profile changed");
                    MarkReadyForConfigurationChange();
                }

                NotifyTargetSelectionProperties();
            }
        }
    }

    public AppProfileAssignmentViewModel? SelectedAppProfileAssignment
    {
        get => _selectedAppProfileAssignment;
        set
        {
            if (SetProperty(ref _selectedAppProfileAssignment, value) && value is not null)
            {
                AssignmentExeNameText = value.ExeName;
            }
        }
    }

    public ExportFileItemViewModel? SelectedExportFile
    {
        get => _selectedExportFile;
        set => SetProperty(ref _selectedExportFile, value);
    }

    public ProfileRecommendationViewModel? SelectedProfileRecommendation
    {
        get => _selectedProfileRecommendation;
        set => SetProperty(ref _selectedProfileRecommendation, value);
    }

    public DeniedProfileRecommendationViewModel? SelectedDeniedProfileRecommendation
    {
        get => _selectedDeniedProfileRecommendation;
        set => SetProperty(ref _selectedDeniedProfileRecommendation, value);
    }

    public SuspiciousWatchItemViewModel? SelectedSuspiciousWatchItem
    {
        get => _selectedSuspiciousWatchItem;
        set => SetProperty(ref _selectedSuspiciousWatchItem, value);
    }

    public ProcessBanRuleViewModel? SelectedProcessBan
    {
        get => _selectedProcessBan;
        set
        {
            if (SetProperty(ref _selectedProcessBan, value))
            {
                OnPropertyChanged(nameof(CanRemoveSelectedProcessBan));
            }
        }
    }

    public ProcessBanDurationOptionViewModel? SelectedProcessBanDurationOption
    {
        get => _selectedProcessBanDurationOption;
        set => SetProperty(ref _selectedProcessBanDurationOption, value);
    }

    public TargetOptionViewModel? SelectedRunningTarget
    {
        get => _selectedRunningTarget;
        set
        {
            var changedForSession = _selectedRunningTarget?.ProcessId != value?.ProcessId;
            if (SetProperty(ref _selectedRunningTarget, value))
            {
                if (changedForSession)
                {
                    StopForParameterChange("target process changed");
                    MarkReadyForConfigurationChange();
                }

                NotifyTargetSelectionProperties();
            }
        }
    }

    public AssignedTargetOptionViewModel? SelectedAssignedTarget
    {
        get => _selectedAssignedTarget;
        set
        {
            var changedForSession = !SameAssignedTarget(_selectedAssignedTarget, value);
            if (SetProperty(ref _selectedAssignedTarget, value))
            {
                if (changedForSession)
                {
                    StopForParameterChange("assigned target changed");
                    MarkReadyForConfigurationChange();
                }

                NotifyTargetSelectionProperties();
            }
        }
    }

    public GlobalProcessRowViewModel? SelectedGlobalProcess
    {
        get => _selectedGlobalProcess;
        set
        {
            if (SetProperty(ref _selectedGlobalProcess, value))
            {
                _selectedGlobalProcessKey = value?.SelectionKey;
                OnPropertyChanged(nameof(CanMonitorSelectedGlobalProcess));
                NotifySelectedGlobalProcessProperties();
            }
        }
    }

    public SamplingOptionViewModel SelectedSamplingOption
    {
        get => _selectedSamplingOption;
        set
        {
            if (SetProperty(ref _selectedSamplingOption, value))
            {
                StopForCaptureChange("sampling changed");
                MarkReadyForConfigurationChange();
                NotifySessionStateProperties();
            }
        }
    }

    public string SelectedTargetMode
    {
        get => _selectedTargetMode;
        set
        {
            if (SetProperty(ref _selectedTargetMode, value))
            {
                StopForParameterChange("target source changed");
                MarkReadyForConfigurationChange();
                NotifyTargetSelectionProperties();
            }
        }
    }

    public string ProcessFilterText
    {
        get => _processFilterText;
        set
        {
            if (SetProperty(ref _processFilterText, value))
            {
                ApplyProcessFilter();
            }
        }
    }

    public string SessionSearchText
    {
        get => _sessionSearchText;
        set
        {
            if (SetProperty(ref _sessionSearchText, value))
            {
                ApplySessionFilter();
            }
        }
    }

    public string StorageStatusText
    {
        get => _storageStatusText;
        private set => SetProperty(ref _storageStatusText, value);
    }

    public bool IncludeChildProcesses
    {
        get => _includeChildProcesses;
        set
        {
            if (SetProperty(ref _includeChildProcesses, value))
            {
                StopForCaptureChange("child process scope changed");
                MarkReadyForConfigurationChange();
                NotifySessionStateProperties();
            }
        }
    }

    public bool IsLiveMonitorEnabled
    {
        get => _isLiveMonitorEnabled;
        set
        {
            if (SetProperty(ref _isLiveMonitorEnabled, value))
            {
                StopForCaptureChange("live monitor changed");
                MarkReadyForConfigurationChange();
                NotifyLivePanelProperties();
            }
        }
    }

    public bool CaptureCpu
    {
        get => _captureCpu;
        set
        {
            if (SetProperty(ref _captureCpu, value))
            {
                StopForCaptureChange("CPU collector changed");
                MarkReadyForConfigurationChange();
            }
        }
    }

    public bool CaptureRam
    {
        get => _captureRam;
        set
        {
            if (SetProperty(ref _captureRam, value))
            {
                StopForCaptureChange("RAM collector changed");
                MarkReadyForConfigurationChange();
            }
        }
    }

    public bool CaptureDiskRead
    {
        get => _captureDiskRead;
        set
        {
            if (SetProperty(ref _captureDiskRead, value))
            {
                StopForCaptureChange("Disk Read collector changed");
                MarkReadyForConfigurationChange();
            }
        }
    }

    public bool CaptureDiskWrite
    {
        get => _captureDiskWrite;
        set
        {
            if (SetProperty(ref _captureDiskWrite, value))
            {
                StopForCaptureChange("Disk Write collector changed");
                MarkReadyForConfigurationChange();
            }
        }
    }

    public bool MinimizeToTrayOnClose
    {
        get => _minimizeToTrayOnClose;
        set => SetProperty(ref _minimizeToTrayOnClose, value);
    }

    public string StoragePath
    {
        get => _storagePath;
        set => SetProperty(ref _storagePath, value);
    }

    public string SelectedExecutablePath
    {
        get => _selectedExecutablePath;
        private set
        {
            if (SetProperty(ref _selectedExecutablePath, value))
            {
                OnPropertyChanged(nameof(SelectedExecutableDisplay));
                StopForParameterChange("target executable changed");
                MarkReadyForConfigurationChange();
                NotifyTargetSelectionProperties();
            }
        }
    }

    public string SelectedExecutableDisplay =>
        string.IsNullOrWhiteSpace(SelectedExecutablePath) ? "No executable selected" : SelectedExecutablePath;

    public string RecordingStatusText
    {
        get => _recordingStatusText;
        private set => SetProperty(ref _recordingStatusText, value);
    }

    public string LiveSnapshotText
    {
        get => _liveSnapshotText;
        private set => SetProperty(ref _liveSnapshotText, value);
    }

    public string RamDiagnosticText
    {
        get => _ramDiagnosticText;
        private set => SetProperty(ref _ramDiagnosticText, value);
    }

    public string SystemContextText
    {
        get => _systemContextText;
        private set => SetProperty(ref _systemContextText, value);
    }

    public string CpuThresholdPercentText
    {
        get => _cpuThresholdPercentText;
        set => SetProperty(ref _cpuThresholdPercentText, value);
    }

    public string RamThresholdMbText
    {
        get => _ramThresholdMbText;
        set => SetProperty(ref _ramThresholdMbText, value);
    }

    public string DiskReadThresholdMbPerSecText
    {
        get => _diskReadThresholdMbPerSecText;
        set => SetProperty(ref _diskReadThresholdMbPerSecText, value);
    }

    public string DiskWriteThresholdMbPerSecText
    {
        get => _diskWriteThresholdMbPerSecText;
        set => SetProperty(ref _diskWriteThresholdMbPerSecText, value);
    }

    public string ProfileCpuThresholdPercentText
    {
        get => _profileCpuThresholdPercentText;
        set => SetProperty(ref _profileCpuThresholdPercentText, value);
    }

    public string ProfileRamThresholdMbText
    {
        get => _profileRamThresholdMbText;
        set => SetProperty(ref _profileRamThresholdMbText, value);
    }

    public string ProfileDiskReadThresholdMbPerSecText
    {
        get => _profileDiskReadThresholdMbPerSecText;
        set => SetProperty(ref _profileDiskReadThresholdMbPerSecText, value);
    }

    public string ProfileDiskWriteThresholdMbPerSecText
    {
        get => _profileDiskWriteThresholdMbPerSecText;
        set => SetProperty(ref _profileDiskWriteThresholdMbPerSecText, value);
    }

    public string StartupGraceSecondsText
    {
        get => _startupGraceSecondsText;
        set => SetProperty(ref _startupGraceSecondsText, value);
    }

    public string EventCooldownSecondsText
    {
        get => _eventCooldownSecondsText;
        set => SetProperty(ref _eventCooldownSecondsText, value);
    }

    public string GroupingWindowSecondsText
    {
        get => _groupingWindowSecondsText;
        set => SetProperty(ref _groupingWindowSecondsText, value);
    }

    public string SnapshotSuppressionSecondsText
    {
        get => _snapshotSuppressionSecondsText;
        set => SetProperty(ref _snapshotSuppressionSecondsText, value);
    }

    public string AssignmentExeNameText
    {
        get => _assignmentExeNameText;
        set => SetProperty(ref _assignmentExeNameText, value);
    }

    public string ThresholdSettingsStatusText
    {
        get => _thresholdSettingsStatusText;
        private set => SetProperty(ref _thresholdSettingsStatusText, value);
    }

    public string ExportStatusText
    {
        get => _exportStatusText;
        private set => SetProperty(ref _exportStatusText, value);
    }

    public string ExportDirectoryText
    {
        get => _exportDirectoryText;
        set => SetProperty(ref _exportDirectoryText, value);
    }

    public string DefaultExportDirectoryText => _defaultExportDirectory;
    public string CurrentVersionText => _updateService.CurrentVersion;
    public string AppVersionBadgeText => $"v{_updateService.CurrentVersion}";
    public bool IsUpdateRestartRequested => _isUpdateRestartRequested;

    public string UpdateManifestUrlText
    {
        get => _updateManifestUrlText;
        set => SetProperty(ref _updateManifestUrlText, value);
    }

    public bool AutomaticallyCheckForUpdates
    {
        get => _automaticallyCheckForUpdates;
        set => SetProperty(ref _automaticallyCheckForUpdates, value);
    }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetProperty(ref _updateStatusText, value);
    }

    public string UpdateLatestVersionText
    {
        get => _updateLatestVersionText;
        private set => SetProperty(ref _updateLatestVersionText, value);
    }

    public string UpdateReleaseNotesText
    {
        get => _updateReleaseNotesText;
        private set => SetProperty(ref _updateReleaseNotesText, value);
    }

    public string DownloadedUpdateInstallerPath
    {
        get => _downloadedUpdateInstallerPath;
        private set => SetProperty(ref _downloadedUpdateInstallerPath, value);
    }

    public bool IsCheckingForUpdates
    {
        get => _isCheckingForUpdates;
        private set
        {
            if (SetProperty(ref _isCheckingForUpdates, value))
            {
                OnPropertyChanged(nameof(CanDownloadUpdateInstaller));
                OnPropertyChanged(nameof(CanSkipAvailableUpdate));
            }
        }
    }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set
        {
            if (SetProperty(ref _isUpdateAvailable, value))
            {
                OnPropertyChanged(nameof(CanDownloadUpdateInstaller));
                OnPropertyChanged(nameof(CanSkipAvailableUpdate));
            }
        }
    }

    public bool CanDownloadUpdateInstaller => IsUpdateAvailable && !IsCheckingForUpdates;
    public bool CanSkipAvailableUpdate => IsUpdateAvailable && _availableUpdateManifest is not null;

    public string LiveAssignmentStatusText
    {
        get => _liveAssignmentStatusText;
        private set => SetProperty(ref _liveAssignmentStatusText, value);
    }

    public string LiveWarningText
    {
        get => _liveWarningText;
        private set
        {
            if (SetProperty(ref _liveWarningText, value))
            {
                OnPropertyChanged(nameof(HasLiveWarning));
                NotifySessionStateProperties();
            }
        }
    }

    public bool HasLiveWarning => !string.IsNullOrWhiteSpace(LiveWarningText);

    public string SelfMonitoringStatusText
    {
        get => _selfMonitoringStatusText;
        private set => SetProperty(ref _selfMonitoringStatusText, value);
    }

    public string SelfCurrentCpuText
    {
        get => _selfCurrentCpuText;
        private set => SetProperty(ref _selfCurrentCpuText, value);
    }

    public string SelfAvgCpuText
    {
        get => _selfAvgCpuText;
        private set => SetProperty(ref _selfAvgCpuText, value);
    }

    public string SelfPeakCpuText
    {
        get => _selfPeakCpuText;
        private set => SetProperty(ref _selfPeakCpuText, value);
    }

    public string SelfCurrentRamText
    {
        get => _selfCurrentRamText;
        private set => SetProperty(ref _selfCurrentRamText, value);
    }

    public string SelfAvgRamText
    {
        get => _selfAvgRamText;
        private set => SetProperty(ref _selfAvgRamText, value);
    }

    public string SelfPeakRamText
    {
        get => _selfPeakRamText;
        private set => SetProperty(ref _selfPeakRamText, value);
    }

    public string SelfDiskWriteText
    {
        get => _selfDiskWriteText;
        private set => SetProperty(ref _selfDiskWriteText, value);
    }

    public string SelfSampleCountText
    {
        get => _selfSampleCountText;
        private set => SetProperty(ref _selfSampleCountText, value);
    }

    public string SelfCpuBudgetStatusText
    {
        get => _selfCpuBudgetStatusText;
        private set => SetProperty(ref _selfCpuBudgetStatusText, value);
    }

    public string SelfRamBudgetStatusText
    {
        get => _selfRamBudgetStatusText;
        private set => SetProperty(ref _selfRamBudgetStatusText, value);
    }

    public string SelfWritesBudgetStatusText
    {
        get => _selfWritesBudgetStatusText;
        private set => SetProperty(ref _selfWritesBudgetStatusText, value);
    }

    public string SelfSnapshotsBudgetStatusText
    {
        get => _selfSnapshotsBudgetStatusText;
        private set => SetProperty(ref _selfSnapshotsBudgetStatusText, value);
    }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
            {
                OnPropertyChanged(nameof(ShowSessionHeader));
                OnPropertyChanged(nameof(IsGlobalWatchTab));
                NotifySessionStateProperties();
            }
        }
    }

    public int SelectedGlobalWatchSectionIndex
    {
        get => _selectedGlobalWatchSectionIndex;
        set => SetProperty(ref _selectedGlobalWatchSectionIndex, value);
    }

    public string GlobalWatchFilterText
    {
        get => _globalWatchFilterText;
        set
        {
            if (SetProperty(ref _globalWatchFilterText, value))
            {
                ApplyGlobalWatchFilterAndSort();
            }
        }
    }

    public bool GlobalWatchOnlyOverLimit
    {
        get => _globalWatchOnlyOverLimit;
        set
        {
            if (SetProperty(ref _globalWatchOnlyOverLimit, value))
            {
                ApplyGlobalWatchFilterAndSort();
            }
        }
    }

    public bool GlobalWatchOnlyCritical
    {
        get => _globalWatchOnlyCritical;
        set
        {
            if (SetProperty(ref _globalWatchOnlyCritical, value))
            {
                ApplyGlobalWatchFilterAndSort();
            }
        }
    }

    public bool GlobalWatchOnlyUnassigned
    {
        get => _globalWatchOnlyUnassigned;
        set
        {
            if (SetProperty(ref _globalWatchOnlyUnassigned, value))
            {
                ApplyGlobalWatchFilterAndSort();
            }
        }
    }

    public bool GlobalWatchOnlyNearLimit
    {
        get => _globalWatchOnlyNearLimit;
        set
        {
            if (SetProperty(ref _globalWatchOnlyNearLimit, value))
            {
                ApplyGlobalWatchFilterAndSort();
            }
        }
    }

    public string SelectedGlobalWatchSortMode
    {
        get => _selectedGlobalWatchSortMode;
        set
        {
            if (SetProperty(ref _selectedGlobalWatchSortMode, value))
            {
                ApplyGlobalWatchFilterAndSort();
            }
        }
    }

    public string SelectedGlobalWatchMode
    {
        get => _selectedGlobalWatchMode;
        set
        {
            if (SetProperty(ref _selectedGlobalWatchMode, value))
            {
                OnPropertyChanged(nameof(IsGlobalWatchGroupedMode));
                OnPropertyChanged(nameof(GlobalWatchEmptyStateText));
                NotifySelectedGlobalProcessProperties();
                ApplyGlobalWatchFilterAndSort();
            }
        }
    }

    public string GlobalWatchStatusText
    {
        get => _globalWatchStatusText;
        private set => SetProperty(ref _globalWatchStatusText, value);
    }

    public string ProfileRecommendationStatusText
    {
        get => _profileRecommendationStatusText;
        private set => SetProperty(ref _profileRecommendationStatusText, value);
    }

    public string ProcessBanStatusText
    {
        get => _processBanStatusText;
        private set => SetProperty(ref _processBanStatusText, value);
    }

    public string GlobalWatchLastScanText
    {
        get => _globalWatchLastScanText;
        private set => SetProperty(ref _globalWatchLastScanText, value);
    }

    public string GlobalWatchJournalStatusText
    {
        get => _globalWatchJournalStatusText;
        private set => SetProperty(ref _globalWatchJournalStatusText, value);
    }

    public string SuspiciousWatchStatusText
    {
        get => _suspiciousWatchStatusText;
        private set => SetProperty(ref _suspiciousWatchStatusText, value);
    }

    public bool HasProcessBans => ProcessBans.Count > 0;
    public bool HasProcessBanEvents => ProcessBanEvents.Count > 0;
    public bool CanRemoveSelectedProcessBan => SelectedProcessBan is not null;

    public bool IsGlobalWatchGroupedMode => SelectedGlobalWatchMode == GlobalModeApplications;
    public bool IsGlobalWatchTab => SelectedTabIndex == GlobalWatchTabIndex;
    public bool HasGlobalWatchJournalEntries => GlobalWatchJournalEntries.Count > 0;
    public bool HasGlobalWatchJournalGroups => GlobalWatchJournalGroups.Count > 0;
    public bool HasSuspiciousWatchItems => SuspiciousWatchItems.Count > 0;
    public bool HasSuspiciousLaunchEntries => SuspiciousLaunchEntries.Count > 0;
    public bool ShowSessionHeader => false;
    public bool HasCurrentMonitoring => IsRecording;
    public string CurrentMonitoringStripText => IsRecording
        ? $"Live monitoring: {_activeTargetName} - {SampleCountText} samples - {EventCountText} events"
        : "Live monitoring idle";
    public bool HasCrossScreenMonitoringStrip => IsRecording && SelectedTabIndex != LiveTabIndex;
    public string CrossScreenMonitoringStripText => IsRecording
        ? $"Live monitoring active: {_activeTargetName} - {DurationText} - Recording"
        : string.Empty;
    public string LiveSessionStateText
    {
        get
        {
            if (_liveSessionState == LiveSessionUiState.ReadyToStart && !CanStart)
            {
                return "Ready";
            }

            return _liveSessionState switch
            {
                LiveSessionUiState.Recording => "Recording",
                LiveSessionUiState.StoppedManually => "Stopped manually",
                LiveSessionUiState.Stopped => "Stopped",
                LiveSessionUiState.EndedUnexpectedly => "Ended unexpectedly",
                LiveSessionUiState.ChangesPending => "Changes pending / restart required",
                _ => "Ready to start"
            };
        }
    }

    public string LiveSessionStateTone
    {
        get => _liveSessionState switch
        {
            LiveSessionUiState.Recording => "recording",
            LiveSessionUiState.ChangesPending => "warning",
            LiveSessionUiState.EndedUnexpectedly => "danger",
            _ => "neutral"
        };
    }

    public string LiveSessionTargetText => IsRecording
        ? _activeTargetName
        : SelectedTargetMode switch
        {
            TargetModeRunningProcess when SelectedRunningTarget is not null => SelectedRunningTarget.DisplayName,
            TargetModeAssignedApps when SelectedAssignedTarget is not null => SelectedAssignedTarget.DisplayText,
            TargetModeExecutable when !string.IsNullOrWhiteSpace(SelectedExecutablePath) => Path.GetFileName(SelectedExecutablePath),
            _ => "No target selected"
        };

    public string LiveSessionMetaText =>
        IsRecording
            ? $"{DurationText} - sampling {SelectedSamplingOption.Label} - child {(IncludeChildProcesses ? "ON" : "OFF")} - {ActiveThresholdSourceText}"
            : $"Current config - sampling {SelectedSamplingOption.Label} - child {(IncludeChildProcesses ? "ON" : "OFF")} - {ActiveThresholdSourceText}";

    public string LiveSessionHintText
    {
        get
        {
            return _liveSessionState switch
            {
                LiveSessionUiState.Recording => "Session is being recorded. Stop to save, or change capture settings to restart cleanly.",
                LiveSessionUiState.ChangesPending => string.IsNullOrWhiteSpace(LiveWarningText)
                    ? "Monitoring stopped. Press Start to apply changes."
                    : LiveWarningText,
                LiveSessionUiState.EndedUnexpectedly => string.IsNullOrWhiteSpace(_lastCompletedSessionHint)
                    ? "Target ended unexpectedly. Open Sessions for the saved review."
                    : _lastCompletedSessionHint,
                LiveSessionUiState.StoppedManually => "Session was stopped from the utility. Press Start to record again.",
                LiveSessionUiState.Stopped => "Session ended. Press Start to record again.",
                _ => CanStart ? "Press Start to begin a new session." : TargetReadinessText
            };
        }
    }
    public bool HasLastCompletedSession => !string.IsNullOrWhiteSpace(_lastCompletedSessionText);
    public string LastCompletedSessionText => _lastCompletedSessionText;
    public string LastCompletedSessionHint => _lastCompletedSessionHint;
    public bool HasActiveLiveMetrics => IsRecording && IsLiveMonitorEnabled && CurrentMetrics.Count > 0;
    public bool ShowLiveSnapshotEmptyState => !HasActiveLiveMetrics;
    public string LiveSnapshotTitle => IsRecording ? "Current Snapshot" : "Live Snapshot";
    public string LiveSnapshotStatusText => IsRecording ? LiveSnapshotText : "not recording";
    public string LiveSnapshotEmptyText => IsRecording
        ? IsLiveMonitorEnabled
            ? "Waiting for the first live UI refresh."
            : "Live monitor is disabled. Samples are still recorded and saved."
        : "No active live data. Start monitoring to populate this panel.";
    public bool HasLiveEvents => IsRecording && VisibleEvents.Count > 0;
    public bool ShowLiveEventEmptyState => !HasLiveEvents;
    public string LiveEventEmptyText => IsRecording
        ? "No detector events in this active session yet."
        : "No active live events. Saved session events are available in Sessions / Details.";
    public string SelectedGlobalProcessPanelTitle => IsGlobalWatchGroupedMode ? "Selected Application" : "Selected Process";
    public string SelectedGlobalProcessTitle => SelectedGlobalProcess is null
        ? (IsGlobalWatchGroupedMode ? "No application selected" : "No process selected")
        : IsGlobalWatchGroupedMode ? SelectedGlobalProcess.AppName : SelectedGlobalProcess.DisplayName;
    public string SelectedGlobalProcessHint => SelectedGlobalProcess is null
        ? "Select a row to inspect identity, profile health and actions."
        : SelectedGlobalProcess.IsGroup
            ? "Application aggregation. Detailed monitoring attaches to the root-candidate process and follows its child tree."
            : "Process detail mode. Identity metadata is best-effort and may be unavailable for protected processes.";
    public string SelectedGlobalProcessExeText => SelectedGlobalProcess?.ExeName ?? "n/a";
    public string SelectedGlobalProcessPathText => SelectedGlobalProcess?.FullPath ?? "n/a";
    public string SelectedGlobalProcessProductText => SelectedGlobalProcess?.ProductName ?? "n/a";
    public string SelectedGlobalProcessDescriptionText => SelectedGlobalProcess?.FileDescription ?? "n/a";
    public string SelectedGlobalProcessCompanyText => SelectedGlobalProcess?.CompanyName ?? "n/a";
    public string SelectedGlobalProcessSignerText => SelectedGlobalProcess?.SignerStatus ?? "n/a";
    public string SelectedGlobalProcessVersionText => SelectedGlobalProcess?.Version ?? "n/a";
    public string SelectedGlobalProcessOriginalFileText => SelectedGlobalProcess?.OriginalFileName ?? "n/a";
    public string SelectedGlobalProcessParentText => SelectedGlobalProcess?.ParentProcessText ?? "n/a";
    public string SelectedGlobalProcessDescendantsText => SelectedGlobalProcess is null
        ? "n/a"
        : SelectedGlobalProcess.IsGroup
            ? $"{SelectedGlobalProcess.InstanceCount:N0} instances; {SelectedGlobalProcess.IncludedProcesses.Count:N0} included processes"
            : $"{SelectedGlobalProcess.DescendantCountText} descendants";
    public string SelectedGlobalProcessPidText => SelectedGlobalProcess is null
        ? "n/a"
        : SelectedGlobalProcess.IsGroup ? $"{SelectedGlobalProcess.InstanceCount:N0} instances" : SelectedGlobalProcess.ProcessIdText;
    public string SelectedGlobalProcessCpuText => SelectedGlobalProcess?.CpuText ?? "n/a";
    public string SelectedGlobalProcessRamText => SelectedGlobalProcess is null
        ? "n/a"
        : $"{SelectedGlobalProcess.MemoryText} ({SelectedGlobalProcess.MemoryDeltaText})";
    public string SelectedGlobalProcessDiskText => SelectedGlobalProcess?.DiskReadWriteText ?? "n/a";
    public string SelectedGlobalProcessStatusText => SelectedGlobalProcess?.StateText ?? "not selected";
    public string SelectedGlobalProcessProfileText => SelectedGlobalProcess?.ProfileSourceText ?? "n/a";
    public string SelectedGlobalProcessHealthText => SelectedGlobalProcess?.HealthBadgeText ?? "n/a";
    public string SelectedGlobalProcessReasonText => SelectedGlobalProcess?.ProfileReason ?? "Select a process to see profile-aware health.";
    public string SelectedGlobalProcessIncludedText => SelectedGlobalProcess?.IncludedProcessSummaryText ?? "n/a";
    public string SelectedGlobalProcessWhatItDoesText
    {
        get
        {
            var selected = SelectedGlobalProcess;
            if (selected is null)
            {
                return "Select a running process or application to inspect what it is.";
            }

            var product = string.IsNullOrWhiteSpace(selected.ProductName) || selected.ProductName == "Unavailable"
                ? selected.AppName
                : selected.ProductName;
            var description = string.IsNullOrWhiteSpace(selected.FileDescription) || selected.FileDescription == "Unavailable"
                ? "No file description was exposed by Windows for this executable."
                : selected.FileDescription;
            var company = string.IsNullOrWhiteSpace(selected.CompanyName) || selected.CompanyName == "Unavailable"
                ? "publisher unknown"
                : selected.CompanyName;
            var role = selected.IsGroup
                ? $"Global Watch is showing this as an application group with {selected.InstanceCount:N0} running instance(s)."
                : selected.Process.ParentProcessId is not null
                    ? $"This looks like a helper/subprocess under {selected.ParentProcessText}."
                    : selected.Process.DescendantProcessCount > 0
                        ? $"This looks like a root/broker process with {selected.Process.DescendantProcessCount:N0} descendant(s)."
                        : "This looks like a standalone process in the last lightweight scan.";

            return $"{product}: {description} Publisher: {company}. {role}";
        }
    }
    public string SelectedGlobalProcessInspectorModeText => IsGlobalWatchGroupedMode
        ? "Applications mode: aggregated application-level view"
        : "Processes mode: individual process detail view";
    public string SelectedGlobalProcessRelationText => SelectedGlobalProcess is null
        ? "Select a row to inspect process relationships."
        : SelectedGlobalProcess.IsGroup
            ? $"Application group for {SelectedGlobalProcess.ExeName}; contains {SelectedGlobalProcess.InstanceCount:N0} running instance(s)."
            : SelectedGlobalProcess.Process.ParentProcessId is not null
                ? $"Likely helper/subprocess under {SelectedGlobalProcess.ParentProcessText}."
                : SelectedGlobalProcess.Process.DescendantProcessCount > 0
                    ? $"Root or broker candidate with {SelectedGlobalProcess.Process.DescendantProcessCount:N0} descendant(s)."
                    : "Standalone/root process candidate; no descendants were visible in the last scan.";
    public string SelectedGlobalProcessAppearsBecauseText => SelectedGlobalProcess is null
        ? "No selected row."
        : SelectedGlobalProcess.IsGroup
            ? "Shown because Global Watch groups currently running processes by exe name in Applications mode."
            : "Shown because this PID was present in the last lightweight Global Watch scan.";
    public string SelectedGlobalProcessCommandLineText => "Not captured in lightweight scan.";
    public string SelectedGlobalProcessSuspiciousLastLaunchText
    {
        get
        {
            var selected = SelectedGlobalProcess;
            if (selected is null || string.IsNullOrWhiteSpace(selected.NormalizedFullPath))
            {
                return "n/a";
            }

            var launch = _thresholdSettingsStore.Current.SuspiciousWatchlist.LaunchHistory
                .Where(item => string.Equals(item.NormalizedPath, selected.NormalizedFullPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Timestamp)
                .FirstOrDefault();

            return launch is null
                ? "No suspicious launch logged after mark."
                : $"Last launch: {launch.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                    + (launch.ParentProcessId is null
                        ? string.Empty
                        : $" from {(string.IsNullOrWhiteSpace(launch.ParentProcessName) ? "Unknown" : launch.ParentProcessName)} ({launch.ParentProcessId})");
        }
    }
    public string GlobalWatchEmptyStateText => IsGlobalWatchGroupedMode
        ? "No applications match the current Global Watch filters."
        : "No processes match the current Global Watch filters.";
    public bool HasGlobalWatchRows => GlobalWatchProcesses.Count > 0;
    public bool CanInspectSelectedGlobalProcess => SelectedGlobalProcess is not null;
    public bool CanOpenSelectedGlobalProcessFileLocation => TryGetSelectedGlobalProcessUsablePath(out var path)
        && File.Exists(path);
    public bool CanCopySelectedGlobalProcessPath => TryGetSelectedGlobalProcessUsablePath(out _);
    public bool CanKillSelectedGlobalProcess => SelectedGlobalProcess is not null;
    public bool CanBanSelectedGlobalProcess => SelectedGlobalProcess is not null
        && TryGetSelectedGlobalProcessUsablePath(out _);
    public string SelectedGlobalProcessKillTreeLabel => SelectedGlobalProcess?.IsGroup == true
        ? "Kill app group"
        : "Kill process tree";
    public string SelectedGlobalProcessBanText => SelectedGlobalProcess is null
        ? "Select a row to create an app-level ban."
        : !CanBanSelectedGlobalProcess
            ? "Full path unavailable; cannot create a ban."
            : $"Ban target: {SelectedGlobalProcess.ExeName} by full path.";
    public bool CanAssignSelectedGlobalProcessProfile => SelectedGlobalProcess is { IsUnassigned: true };
    public bool CanMarkSelectedGlobalProcessSuspicious => SelectedGlobalProcess is not null
        && TryGetSelectedGlobalProcessUsablePath(out _)
        && !IsSelectedGlobalProcessSuspicious;
    public bool CanRemoveSelectedGlobalProcessSuspicious => SelectedGlobalProcess is not null
        && IsSelectedGlobalProcessSuspicious;
    public bool IsSelectedGlobalProcessSuspicious => SelectedGlobalProcess is not null
        && !string.IsNullOrWhiteSpace(SelectedGlobalProcess.NormalizedFullPath)
        && _thresholdSettingsStore.Current.SuspiciousWatchlist.Items.Any(item =>
            string.Equals(item.NormalizedPath, SelectedGlobalProcess.NormalizedFullPath, StringComparison.OrdinalIgnoreCase));
    public string SelectedGlobalProcessSuspiciousText => SelectedGlobalProcess is null
        ? "Select a row to manage watchlist status."
        : !TryGetSelectedGlobalProcessUsablePath(out _)
            ? "Full path unavailable; cannot add to suspicious watchlist."
            : IsSelectedGlobalProcessSuspicious
                ? "Marked suspicious. Launch transitions will be logged."
                : "Not marked suspicious.";
    public bool SelectedGlobalProcessHasRecommendation => SelectedGlobalProcess is not null
        && ProfileRecommendations.Any(recommendation => string.Equals(recommendation.ExeName, SelectedGlobalProcess.ExeName, StringComparison.OrdinalIgnoreCase));
    public string SelectedGlobalProcessRecommendationText => SelectedGlobalProcessHasRecommendation
        ? "A profile recommendation is ready for this exe."
        : "No active recommendation for this exe.";
    public bool HasInspectorTarget => _processInspectorTarget is not null;
    public string InspectorTitle => _processInspectorTarget?.Title ?? "No target loaded";
    public string InspectorModeText => _processInspectorTarget?.ModeText ?? "No target loaded";
    public string InspectorSourceText => _processInspectorTarget?.SourceText ?? "No source";
    public string InspectorExeText => _processInspectorTarget?.ExeName ?? "n/a";
    public string InspectorPathText => _processInspectorTarget?.FullPath ?? "n/a";
    public string InspectorProductText => _processInspectorTarget?.ProductName ?? "n/a";
    public string InspectorDescriptionText => _processInspectorTarget?.FileDescription ?? "n/a";
    public string InspectorCompanyText => _processInspectorTarget?.CompanyName ?? "n/a";
    public string InspectorSignerText => _processInspectorTarget?.SignerStatus ?? "n/a";
    public string InspectorVersionText => _processInspectorTarget?.Version ?? "n/a";
    public string InspectorOriginalFileText => _processInspectorTarget?.OriginalFileName ?? "n/a";
    public string InspectorParentText => _processInspectorTarget?.ParentText ?? "n/a";
    public string InspectorDescendantsText => _processInspectorTarget?.DescendantsText ?? "n/a";
    public string InspectorPidText => _processInspectorTarget?.PidText ?? "n/a";
    public string InspectorCpuText => _processInspectorTarget?.CpuText ?? "n/a";
    public string InspectorRamText => _processInspectorTarget?.RamText ?? "n/a";
    public string InspectorDiskText => _processInspectorTarget?.DiskText ?? "n/a";
    public string InspectorStatusText => _processInspectorTarget?.StatusText ?? "not running";
    public string InspectorProfileText => _processInspectorTarget?.ProfileText ?? "n/a";
    public string InspectorHealthText => _processInspectorTarget?.HealthText ?? "n/a";
    public string InspectorReasonText => _processInspectorTarget?.ReasonText ?? "Select a target to inspect.";
    public string InspectorIncludedText => _processInspectorTarget?.IncludedText ?? "n/a";
    public string InspectorRelationText => _processInspectorTarget?.RelationText ?? "No process relationship data.";
    public string InspectorAppearsBecauseText => _processInspectorTarget?.AppearsBecauseText ?? "No selected target.";
    public string InspectorCommandLineText => _processInspectorTarget?.CommandLineText ?? "Not captured.";
    public bool InspectorIsGroup => _processInspectorTarget?.IsGroup ?? false;
    public IReadOnlyList<GlobalProcessMemberViewModel> InspectorIncludedProcessRows => _processInspectorTarget?.IncludedProcessRows ?? [];
    public string InspectorWhatItDoesText
    {
        get
        {
            var target = _processInspectorTarget;
            if (target is null)
            {
                return "No inspector target loaded.";
            }

            var product = string.IsNullOrWhiteSpace(target.ProductName) || target.ProductName.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                ? target.ExeName
                : target.ProductName;
            var description = string.IsNullOrWhiteSpace(target.FileDescription) || target.FileDescription == "Unavailable"
                ? "No file description is available for this executable."
                : target.FileDescription;
            var company = string.IsNullOrWhiteSpace(target.CompanyName) || target.CompanyName.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                ? "publisher unknown"
                : target.CompanyName;
            var running = target.IsRunning
                ? "It is currently running in the latest Global Watch scan."
                : "It is not running in the latest Global Watch scan, so metrics/tree details are saved best-effort context.";

            return $"{product}: {description} Publisher: {company}. {running}";
        }
    }

    public string InspectorSuspiciousLastLaunchText
    {
        get
        {
            if (_processInspectorTarget is null || string.IsNullOrWhiteSpace(_processInspectorTarget.NormalizedFullPath))
            {
                return "n/a";
            }

            var launch = _thresholdSettingsStore.Current.SuspiciousWatchlist.LaunchHistory
                .Where(item => string.Equals(item.NormalizedPath, _processInspectorTarget.NormalizedFullPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Timestamp)
                .FirstOrDefault();

            return launch is null
                ? "No suspicious launch logged after mark."
                : $"Last launch: {launch.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                    + (launch.ParentProcessId is null
                        ? string.Empty
                        : $" from {(string.IsNullOrWhiteSpace(launch.ParentProcessName) ? "Unknown" : launch.ParentProcessName)} ({launch.ParentProcessId})");
        }
    }

    public bool CanMonitorInspectorTarget => _processInspectorTarget?.ActiveRow is not null;
    public bool CanOpenInspectorFileLocation => TryGetInspectorUsablePath(out var inspectorPath) && File.Exists(inspectorPath);
    public bool CanCopyInspectorPath => TryGetInspectorUsablePath(out _);
    public bool CanKillInspectorTarget => _processInspectorTarget?.ActiveRow is not null;
    public bool CanBanInspectorTarget => TryGetInspectorUsablePath(out _);
    public bool CanMarkInspectorSuspicious => TryGetInspectorUsablePath(out _)
        && !IsInspectorTargetSuspicious;
    public bool CanRemoveInspectorSuspicious => IsInspectorTargetSuspicious;
    public bool IsInspectorTargetSuspicious => _processInspectorTarget is not null
        && !string.IsNullOrWhiteSpace(_processInspectorTarget.NormalizedFullPath)
        && _thresholdSettingsStore.Current.SuspiciousWatchlist.Items.Any(item =>
            string.Equals(item.NormalizedPath, _processInspectorTarget.NormalizedFullPath, StringComparison.OrdinalIgnoreCase));
    public string InspectorKillTreeLabel => _processInspectorTarget?.IsGroup == true
        ? "Kill app group"
        : "Kill process tree";
    public string InspectorSuspiciousText => _processInspectorTarget is null
        ? "No inspector target loaded."
        : !TryGetInspectorUsablePath(out _)
            ? "Full path unavailable; cannot add to suspicious watchlist."
            : IsInspectorTargetSuspicious
                ? "Marked suspicious. Launch transitions will be logged."
                : "Not marked suspicious.";
    public string InspectorBanText => _processInspectorTarget is null
        ? "No inspector target loaded."
        : !TryGetInspectorUsablePath(out _)
            ? "Full path unavailable; cannot create a ban."
            : $"Ban target: {_processInspectorTarget.ExeName} by full path.";
    public bool InspectorHasRecommendation => _processInspectorTarget is not null
        && ProfileRecommendations.Any(recommendation => string.Equals(recommendation.ExeName, _processInspectorTarget.ExeName, StringComparison.OrdinalIgnoreCase));
    public string InspectorRecommendationText => InspectorHasRecommendation
        ? "A profile recommendation is ready for this exe."
        : "No active recommendation for this exe.";
    public bool HasSessionDetails => SelectedSession is not null;
    public bool HasSessionDetailEvents => SessionDetailEvents.Count > 0;
    public bool HasSessionDetailMetrics => SessionDetailMetricSummaries.Count > 0;
    public string SessionDetailTitle => SelectedSession is null
        ? "No session selected"
        : $"{SelectedSession.AppName} review";
    public string SessionDetailSubtitle => SelectedSession?.SessionLabel ?? "Open a saved session from Sessions.";

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
                NotifyTargetSelectionProperties();
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(HasCurrentMonitoring));
                OnPropertyChanged(nameof(CurrentMonitoringStripText));
                NotifySessionStateProperties();
                NotifySummaryProperties();
            }
        }
    }

    public bool IsRunningProcessMode => SelectedTargetMode == TargetModeRunningProcess;
    public bool IsAssignedAppsMode => SelectedTargetMode == TargetModeAssignedApps;
    public bool IsExecutableMode => SelectedTargetMode == TargetModeExecutable;

    public bool CanStart =>
        !IsRecording
        && (SelectedTargetMode switch
        {
            TargetModeExecutable => !string.IsNullOrWhiteSpace(SelectedExecutablePath),
            TargetModeAssignedApps => SelectedAssignedTarget?.RunningProcessId is not null,
            _ => SelectedRunningTarget is not null
        });

    public bool CanStop => IsRecording;

    public bool CanMonitorSelectedGlobalProcess => SelectedGlobalProcess is not null;

    public string TargetReadinessText => SelectedTargetMode switch
    {
        TargetModeExecutable => string.IsNullOrWhiteSpace(SelectedExecutablePath)
            ? "Choose an executable in the manual launch section."
            : $"Ready to launch and track {Path.GetFileName(SelectedExecutablePath)}.",
        TargetModeAssignedApps => SelectedAssignedTarget is null
            ? "No assigned app selected."
            : SelectedAssignedTarget.RunningProcessId is null
                ? $"{SelectedAssignedTarget.ExeName} is not running. Start it manually, then refresh."
                : $"Ready to attach to {SelectedAssignedTarget.ExeName} ({SelectedAssignedTarget.RunningStatus}).",
        _ => SelectedRunningTarget is null
            ? "Select a running process."
            : $"Ready to attach to {SelectedRunningTarget.DisplayName}."
    };

    public string ActiveThresholdSourceText => ResolveActiveThresholdSourceText();
    public string LiveConfigTargetText => LiveSessionTargetText;
    public string LiveConfigCollectorsText
    {
        get
        {
            var enabled = new[]
                {
                    CaptureCpu ? "CPU" : null,
                    CaptureRam ? "RAM" : null,
                    CaptureDiskRead ? "Disk Read" : null,
                    CaptureDiskWrite ? "Disk Write" : null
                }
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();

            return enabled.Length == 0 ? "No collectors enabled" : string.Join(", ", enabled);
        }
    }
    public string LiveConfigSessionProfileText => SelectedSessionProfileOption?.DisplayName ?? "Auto";
    public string LiveConfigSamplingText => SelectedSamplingOption.Label;
    public string LiveConfigChildScopeText => IncludeChildProcesses ? "Include child processes" : "Root process only";
    public string LiveConfigMonitorText => IsLiveMonitorEnabled ? "Live monitor on" : "Live monitor off";
    public string TrackedProcessCountText
    {
        get
        {
            if (!IsRecording)
            {
                return IncludeChildProcesses ? "tree" : "1";
            }

            return _liveTrackedProcessCount > 0
                ? _liveTrackedProcessCount.ToString("N0")
                : "warming";
        }
    }

    public string LiveSnapshotScopeText
    {
        get
        {
            if (!IsRecording)
            {
                return IncludeChildProcesses
                    ? "Ready scope: selected root process + its descendant process tree."
                    : "Ready scope: selected root process only.";
            }

            var rootText = _liveRootProcessId?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
            if (!IncludeChildProcesses)
            {
                return $"Live scope: root PID {rootText} only. Current snapshot and saved samples use this same scope.";
            }

            if (_liveTrackedProcessCount <= 0)
            {
                return $"Live scope: root PID {rootText} + descendants; waiting for first aggregated sample.";
            }

            if (_liveTrackedProcessCount == 1)
            {
                return $"Live scope: root PID {rootText} + no live descendants found. Global Watch Applications may include sibling processes outside this tree.";
            }

            return $"Live scope: root PID {rootText} + {_liveTrackedProcessCount - 1:N0} descendants ({_liveTrackedProcessCount:N0} processes). Current snapshot and saved samples use this same aggregation.";
        }
    }

    public string TargetName => IsRecording ? _activeTargetName : LiveSessionTargetText;
    public string StartedText => IsRecording && _activeStartedAt is not null
        ? _activeStartedAt.Value.ToLocalTime().ToString("MMM dd, HH:mm")
        : "No active session";
    public string DurationText => IsRecording && _activeStartedAt is not null
        ? FormatDuration(DateTimeOffset.UtcNow - _activeStartedAt.Value)
        : "0m 00s";
    public string StatusText => LiveSessionStateText;
    public string SampleCountText => IsRecording ? _liveSampleCount.ToString("N0") : "0";
    public string EventCountText => IsRecording ? _liveEventCount.ToString("N0") : "0";
    public string SpikeCountText => IsRecording ? _liveSpikeCount.ToString("N0") : "0";
    public string BreachCountText => IsRecording ? _liveBreachCount.ToString("N0") : "0";
    public string HangCountText => "0";

    public async Task InitializeAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        StoragePath = storagePath;
        var thresholds = await _thresholdSettingsStore.LoadAsync(cancellationToken);
        LoadThresholdSettingsIntoUi(thresholds);
        RefreshGlobalWatchJournalCollection();
        RefreshSuspiciousWatchCollections();
        RefreshProcessBanCollections();
        ApplyExportSettingsToUi(thresholds.Export);
        ApplyUpdateSettingsToUi(thresholds.Updates);
        SelectedRetentionOption = RetentionOptions.FirstOrDefault(option => option.Days == thresholds.Retention.RetentionDays)
            ?? RetentionOptions.FirstOrDefault(option => option.Days == 30);
        await ApplyRetentionPolicyAsync(thresholds.Retention, cancellationToken);
        ThresholdSettingsStatusText = "Threshold profiles loaded. Assigned apps use profile limits; others use global fallback.";
        await RefreshRunningProcessesAsync(cancellationToken);
        await ReloadSessionsAsync(selectSessionId: null, cancellationToken);
        await RefreshExportFilesAsync(cancellationToken);
        StartSelfMonitoring();
        StartGlobalWatch();
        _ = AutoCheckForUpdatesIfNeededAsync(thresholds.Updates, cancellationToken);
    }

    public void Shutdown()
    {
        _selfMonitoringCts?.Cancel();
        _selfMonitoringCts?.Dispose();
        _selfMonitoringCts = null;
        _globalWatchCts?.Cancel();
        _globalWatchCts?.Dispose();
        _globalWatchCts = null;
    }

    public async Task RefreshRunningProcessesAsync(CancellationToken cancellationToken = default)
    {
        var targets = await _targetResolver.ListRunningTargetsAsync(cancellationToken);
        _allRunningTargets.Clear();
        _allRunningTargets.AddRange(targets.Select(target => new TargetOptionViewModel(target)));
        ApplyProcessFilter();
        SelectedRunningTarget ??= RunningTargets.FirstOrDefault();
        RefreshAssignedTargets();
        NotifyTargetSelectionProperties();
    }

    public Task RefreshGlobalWatchAsync(CancellationToken cancellationToken = default) =>
        RefreshGlobalWatchCoreAsync(manual: true, cancellationToken);

    public async Task MonitorSelectedGlobalProcessAsync(CancellationToken cancellationToken = default)
    {
        var selectedProcess = GetSelectedGlobalWatchRow();
        if (selectedProcess is null)
        {
            GlobalWatchStatusText = "Select a process in Global Watch first.";
            return;
        }

        var processId = selectedProcess.ProcessId;
        var wasRecording = IsRecording;
        await RefreshRunningProcessesAsync(cancellationToken);
        var target = _allRunningTargets.FirstOrDefault(item => item.ProcessId == processId);
        if (target is null)
        {
            GlobalWatchStatusText = $"{selectedProcess.Name} ({processId}) is no longer running.";
            return;
        }

        ProcessFilterText = string.Empty;
        SelectedTargetMode = TargetModeRunningProcess;
        SelectedRunningTarget = RunningTargets.FirstOrDefault(item => item.ProcessId == processId) ?? target;
        SelectedTabIndex = LiveTabIndex;

        if (wasRecording)
        {
            GlobalWatchStatusText = $"{target.DisplayName} selected in Live. Current monitoring was stopped; press Start to apply changes.";
            NotifyTargetSelectionProperties();
            return;
        }

        RecordingStatusText = $"Starting detailed monitoring for {target.DisplayName}.";
        if (!IsRecording)
        {
            LiveWarningText = string.Empty;
        }

        GlobalWatchStatusText = $"{target.DisplayName} selected in Live for detailed monitoring.";
        NotifyTargetSelectionProperties();
        await StartRecordingAsync(cancellationToken);
    }

    public Task OpenSelectedGlobalProcessFileLocationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetSelectedGlobalProcessUsablePath(out var path) || !File.Exists(path))
        {
            GlobalWatchStatusText = "File location is unavailable for the selected process.";
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
            GlobalWatchStatusText = $"Opened file location for {Path.GetFileName(path)}.";
        }
        catch (Exception error)
        {
            GlobalWatchStatusText = $"Could not open file location: {error.Message}";
        }

        return Task.CompletedTask;
    }

    public void CopySelectedGlobalProcessFullPath()
    {
        if (!TryGetSelectedGlobalProcessUsablePath(out var path))
        {
            GlobalWatchStatusText = "Full path is unavailable for the selected process.";
            return;
        }

        try
        {
            Clipboard.SetText(path);
            GlobalWatchStatusText = $"Copied full path for {Path.GetFileName(path)}.";
        }
        catch (Exception error)
        {
            GlobalWatchStatusText = $"Could not copy full path: {error.Message}";
        }
    }

    public bool PrepareInspectorFromSelectedGlobalProcess()
    {
        var selected = GetSelectedGlobalWatchRow();
        if (selected is null)
        {
            GlobalWatchStatusText = "Select a process or application first.";
            return false;
        }

        SetProcessInspectorTarget(ProcessInspectorTargetViewModel.FromGlobalProcess(
            selected,
            "Global Watch Overview - latest scan",
            IsGlobalWatchGroupedMode));
        return true;
    }

    public async Task MonitorInspectorTargetAsync(CancellationToken cancellationToken = default)
    {
        var row = _processInspectorTarget?.ActiveRow
            ?? FindActiveGlobalWatchRowForInspector(
                _processInspectorTarget?.ExeName,
                processId: null,
                _processInspectorTarget?.NormalizedFullPath,
                preferApplications: _processInspectorTarget?.IsGroup ?? true);
        if (row is null)
        {
            GlobalWatchStatusText = $"{_processInspectorTarget?.ExeName ?? "Selected target"} is not running in the latest scan.";
            return;
        }

        SelectGlobalWatchRowForAction(row);
        await MonitorSelectedGlobalProcessAsync(cancellationToken);
    }

    public Task OpenInspectorFileLocationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetInspectorUsablePath(out var path) || !File.Exists(path))
        {
            GlobalWatchStatusText = "File location is unavailable for the inspector target.";
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
            GlobalWatchStatusText = $"Opened file location for {Path.GetFileName(path)}.";
        }
        catch (Exception error)
        {
            GlobalWatchStatusText = $"Could not open file location: {error.Message}";
        }

        return Task.CompletedTask;
    }

    public void CopyInspectorFullPath()
    {
        if (!TryGetInspectorUsablePath(out var path))
        {
            GlobalWatchStatusText = "Full path is unavailable for the inspector target.";
            return;
        }

        try
        {
            Clipboard.SetText(path);
            GlobalWatchStatusText = $"Copied full path for {Path.GetFileName(path)}.";
        }
        catch (Exception error)
        {
            GlobalWatchStatusText = $"Could not copy full path: {error.Message}";
        }
    }

    public async Task KillInspectorProcessAsync(CancellationToken cancellationToken = default)
    {
        if (_processInspectorTarget?.ActiveRow is not { } activeRow)
        {
            GlobalWatchStatusText = "Inspector target is not running; nothing to kill.";
            return;
        }

        var result = await _processControlService.KillProcessAsync(
            activeRow.ProcessId,
            "manual Process Inspector hard kill",
            cancellationToken);
        GlobalWatchStatusText = FormatProcessControlResult("Inspector hard kill", result);
        await RefreshGlobalWatchAsync(cancellationToken);
    }

    public async Task KillInspectorTreeOrGroupAsync(CancellationToken cancellationToken = default)
    {
        if (_processInspectorTarget?.ActiveRow is not { } activeRow)
        {
            GlobalWatchStatusText = "Inspector target is not running; nothing to kill.";
            return;
        }

        var result = await KillGlobalWatchRowTreeOrGroupAsync(
            activeRow,
            activeRow.IsGroup
                ? "manual Process Inspector app-group hard kill"
                : "manual Process Inspector process-tree hard kill",
            cancellationToken);

        GlobalWatchStatusText = FormatProcessControlResult(
            activeRow.IsGroup ? "Inspector app-group hard kill" : "Inspector process-tree hard kill",
            result);
        await RefreshGlobalWatchAsync(cancellationToken);
    }

    public async Task MarkInspectorSuspiciousAsync(CancellationToken cancellationToken = default)
    {
        var target = _processInspectorTarget;
        if (target is null)
        {
            SuspiciousWatchStatusText = "Open a process or saved watch item in Details first.";
            return;
        }

        if (!TryGetInspectorUsablePath(out _))
        {
            SuspiciousWatchStatusText = "Full path is unavailable; cannot mark it suspicious.";
            return;
        }

        var settings = _thresholdSettingsStore.Current;
        var watchlist = settings.SuspiciousWatchlist;
        var items = watchlist.Items
            .Where(item => !string.Equals(item.NormalizedPath, target.NormalizedFullPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        items.Insert(0, new SuspiciousWatchItem
        {
            NormalizedPath = target.NormalizedFullPath,
            ExeName = NormalizeExeNameForAssignment(target.ExeName),
            ProductName = target.ProductName,
            CompanyName = target.CompanyName,
            SignerStatus = target.SignerStatus,
            MarkedAt = DateTimeOffset.UtcNow,
            Note = $"Marked from Process Inspector ({target.SourceText})."
        });

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                SuspiciousWatchlist = watchlist with { Items = items }
            },
            cancellationToken);
        RefreshSuspiciousWatchCollections();
        NotifyInspectorTargetProperties();
        SuspiciousWatchStatusText = $"{target.ExeName} marked suspicious. Future launch transitions will be logged.";
    }

    public async Task RemoveInspectorSuspiciousAsync(CancellationToken cancellationToken = default)
    {
        if (_processInspectorTarget is null || !TryGetInspectorUsablePath(out _))
        {
            SuspiciousWatchStatusText = "Open a marked process or watch item in Details first.";
            return;
        }

        await RemoveSuspiciousPathAsync(_processInspectorTarget.NormalizedFullPath, cancellationToken);
        NotifyInspectorTargetProperties();
    }

    public Task BanInspectorTargetAsync(CancellationToken cancellationToken = default) =>
        BanInspectorTargetCoreAsync(killAfterBan: false, cancellationToken);

    public Task BanAndKillInspectorTargetAsync(CancellationToken cancellationToken = default) =>
        BanInspectorTargetCoreAsync(killAfterBan: true, cancellationToken);

    public void OpenRecommendationForInspectorTarget()
    {
        var target = _processInspectorTarget;
        if (target is null)
        {
            ProfileRecommendationStatusText = "Open a process in Details first.";
            return;
        }

        var recommendation = ProfileRecommendations.FirstOrDefault(item =>
            string.Equals(item.ExeName, target.ExeName, StringComparison.OrdinalIgnoreCase));
        if (recommendation is null)
        {
            ProfileRecommendationStatusText = $"No active recommendation for {target.ExeName}.";
            return;
        }

        SelectedProfileRecommendation = recommendation;
        SelectedTabIndex = GlobalWatchTabIndex;
        SelectedGlobalWatchSectionIndex = GlobalWatchRecommendationsSectionIndex;
        ProfileRecommendationStatusText = $"{recommendation.ExeName} recommendation selected.";
    }

    public void SelectExecutable(string path)
    {
        SelectedExecutablePath = path;
        SelectedTargetMode = TargetModeExecutable;
    }

    public async Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (!CanStart)
        {
            RecordingStatusText = TargetReadinessText;
            return;
        }

        try
        {
            var target = await ResolveSelectedRecordingTargetAsync(cancellationToken);
            if (target is null)
            {
                RecordingStatusText = TargetReadinessText;
                return;
            }

            var sampling = CreateSamplingSettings(target);

            _activeHandle = target.LifecycleMode == TargetLifecycleMode.LaunchAndTrack
                ? await _sessionRunner.StartAsync(target, sampling, cancellationToken)
                : await _sessionRunner.AttachAsync(target, sampling, cancellationToken);

            _activeHandle.SampleCollected += OnSampleCollected;
            _activeHandle.EventsDetected += OnEventsDetected;
            _activeHandle.Completed += OnSessionCompleted;
            _ = WatchActiveSessionAsync(_activeHandle);
            _activeTargetName = _activeHandle.Target.DisplayName;
            _activeSessionProfileText = sampling.ThresholdSourceLabel ?? ResolveActiveThresholdSourceText(target);
            _activeStartedAt = DateTimeOffset.UtcNow;
            _liveSampleCount = 0;
            _liveEventCount = 0;
            _liveSpikeCount = 0;
            _liveBreachCount = 0;
            _liveTrackedProcessCount = 0;
            _liveRootProcessId = _activeHandle.Target.ProcessId;
            _lastLiveUiRefresh = DateTimeOffset.MinValue;
            _liveCapabilities = CreateLiveCapabilities(sampling);
            RecordingStatusText = $"Recording {_activeTargetName}";
            LiveWarningText = string.Empty;
            LiveSnapshotText = $"sampling {SelectedSamplingOption.Label}";
            CurrentMetrics.Clear();
            VisibleEvents.Clear();
            _manualStopInProgress = false;
            SetLiveSessionState(LiveSessionUiState.Recording);
            NotifyLivePanelProperties();
            IsRecording = true;
            NotifyTargetSelectionProperties();
            NotifySummaryProperties();
        }
        catch (Exception error)
        {
            RecordingStatusText = $"Start failed: {error.Message}";
            LiveWarningText = $"Start failed: {error.Message}";
            LiveSnapshotText = "not recording";
            CurrentMetrics.Clear();
            VisibleEvents.Clear();
            IsRecording = false;
            SetLiveSessionState(LiveSessionUiState.EndedUnexpectedly);
            NotifyLivePanelProperties();
        }
    }

    public async Task StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (_activeHandle is null)
        {
            return;
        }

        try
        {
            RecordingStatusText = "Stopping and saving session";
            _manualStopInProgress = true;
            await _activeHandle.StopAsync(cancellationToken);
        }
        catch (Exception error)
        {
            _manualStopInProgress = false;
            RecordingStatusText = $"Stop failed: {error.Message}";
        }
    }

    public async Task CaptureRamDiagnosticAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var target = _activeHandle?.Target;
            if (target is null)
            {
                target = await ResolveSelectedAttachTargetAsync(cancellationToken);
                if (target is null)
                {
                    RamDiagnosticText = "Select a running target first.";
                    return;
                }
            }

            var snapshot = await _ramDiagnosticProvider.CaptureAsync(target, cancellationToken);
            RamDiagnosticText = FormatRamDiagnostic(snapshot);
        }
        catch (Exception error)
        {
            RamDiagnosticText = $"RAM diagnostic failed: {error.Message}";
        }
    }

    public async Task CaptureSystemContextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var target = _activeHandle?.Target;
            if (target is null)
            {
                target = await ResolveSelectedAttachTargetAsync(cancellationToken);
                if (target is null)
                {
                    SystemContextText = "Select a running target first.";
                    return;
                }
            }

            var now = DateTimeOffset.UtcNow;
            var manualEvent = new PerformanceEvent
            {
                Id = $"manual_context_{now:yyyyMMdd_HHmmss}",
                SessionId = _activeHandle?.SessionId ?? "manual_context",
                Kind = EventKind.Spike,
                Timestamp = now,
                ElapsedMs = _activeStartedAt is null ? 0 : (long)(now - _activeStartedAt.Value).TotalMilliseconds,
                Severity = EventSeverity.Info,
                Title = "Manual system context snapshot",
                Details = "Manual best-effort snapshot.",
                DetectionProvider = "manual"
            };

            _activeHandle?.SuppressDetectorNoise(
                TimeSpan.FromSeconds(_thresholdSettingsStore.Current.AntiNoise.SnapshotSuppressionSeconds),
                "manual system context snapshot");

            var snapshot = await _spikeContextProvider.CaptureAsync(
                new SpikeContextInput(manualEvent.SessionId, target, manualEvent, LookbackMs: 5000, LookaheadMs: 0),
                cancellationToken);

            SystemContextText = snapshot is null
                ? "System context snapshot unavailable."
                : FormatSystemContext(snapshot);
        }
        catch (Exception error)
        {
            SystemContextText = $"System context snapshot failed: {error.Message}";
        }
    }

    public async Task SaveThresholdSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!TryReadGlobalThresholds(out var globalLimits))
        {
            ThresholdSettingsStatusText = "Use positive numeric values for global CPU, RAM, Disk Read, and Disk Write.";
            return;
        }

        var settings = _thresholdSettingsStore.Current with
        {
            CpuThresholdPercent = globalLimits.CpuThresholdPercent,
            RamThresholdMb = globalLimits.RamThresholdMb,
            DiskReadThresholdMbPerSec = globalLimits.DiskReadThresholdMbPerSec,
            DiskWriteThresholdMbPerSec = globalLimits.DiskWriteThresholdMbPerSec
        };

        await _thresholdSettingsStore.SaveAsync(settings, cancellationToken);
        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current);
        ThresholdSettingsStatusText = "Global fallback saved.";
    }

    public async Task SaveAntiNoiseSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!TryReadAntiNoiseSettings(out var antiNoise))
        {
            ThresholdSettingsStatusText = "Use whole seconds for anti-noise settings.";
            return;
        }

        await _thresholdSettingsStore.SaveAsync(
            _thresholdSettingsStore.Current with { AntiNoise = antiNoise },
            cancellationToken);

        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
        ThresholdSettingsStatusText = "Anti-noise settings saved.";
    }

    public async Task SaveCaptureSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _thresholdSettingsStore.SaveAsync(
            _thresholdSettingsStore.Current with { Capture = ReadCaptureSettingsFromUi() },
            cancellationToken);

        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
        ThresholdSettingsStatusText = "Capture scope saved. New sessions will record only enabled metrics.";
    }

    public async Task SaveAppBehaviorSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _thresholdSettingsStore.SaveAsync(
            _thresholdSettingsStore.Current with
            {
                Behavior = _thresholdSettingsStore.Current.Behavior with
                {
                    MinimizeToTrayOnClose = MinimizeToTrayOnClose
                }
            },
            cancellationToken);

        ThresholdSettingsStatusText = MinimizeToTrayOnClose
            ? "Close button will keep Session Perf Tracker running in the tray."
            : "Close button will exit Session Perf Tracker.";
    }

    public async Task SaveSelectedProfileAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedThresholdProfile is null)
        {
            ThresholdSettingsStatusText = "Select a profile first.";
            return;
        }

        if (!TryReadProfileThresholds(out var profileLimits))
        {
            ThresholdSettingsStatusText = "Use positive numeric values for selected profile thresholds.";
            return;
        }

        var profiles = _thresholdSettingsStore.Current.Profiles
            .Select(profile => string.Equals(profile.Id, SelectedThresholdProfile.Id, StringComparison.OrdinalIgnoreCase)
                ? profile with { Limits = profileLimits }
                : profile)
            .ToList();

        await _thresholdSettingsStore.SaveAsync(
            _thresholdSettingsStore.Current with
            {
                SelectedProfileId = SelectedThresholdProfile.Id,
                Profiles = profiles
            },
            cancellationToken);

        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile.Id);
        ThresholdSettingsStatusText = $"{SelectedThresholdProfile.Name} profile saved.";
    }

    public async Task SaveAppProfileAssignmentAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedAssignmentProfile is null)
        {
            ThresholdSettingsStatusText = "Select a profile for the app assignment.";
            return;
        }

        var exeName = NormalizeExeNameForAssignment(AssignmentExeNameText);
        if (string.IsNullOrWhiteSpace(exeName))
        {
            ThresholdSettingsStatusText = "Enter an exe name, for example opera.exe.";
            return;
        }

        var assignments = new Dictionary<string, string>(_thresholdSettingsStore.Current.AppProfileAssignments, StringComparer.OrdinalIgnoreCase)
        {
            [exeName] = SelectedAssignmentProfile.Id
        };

        await _thresholdSettingsStore.SaveAsync(
            _thresholdSettingsStore.Current with { AppProfileAssignments = assignments },
            cancellationToken);

        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
        AssignmentExeNameText = exeName;
        ThresholdSettingsStatusText = $"{exeName} now uses {SelectedAssignmentProfile.Name}.";
    }

    public async Task AssignCurrentTargetProfileAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedLiveAssignmentProfile is null)
        {
            LiveAssignmentStatusText = "Select a profile for the current target.";
            return;
        }

        try
        {
            var exeName = NormalizeExeNameForAssignment(await ResolveCurrentTargetExeNameAsync(cancellationToken));
            if (string.IsNullOrWhiteSpace(exeName))
            {
                LiveAssignmentStatusText = "Cannot determine exe name for the current target.";
                return;
            }

            var profileId = SelectedLiveAssignmentProfile.Id;
            var profileName = SelectedLiveAssignmentProfile.Name;
            var assignments = new Dictionary<string, string>(_thresholdSettingsStore.Current.AppProfileAssignments, StringComparer.OrdinalIgnoreCase);
            var hadExistingAssignment = assignments.ContainsKey(exeName);
            assignments[exeName] = profileId;

            await _thresholdSettingsStore.SaveAsync(
                _thresholdSettingsStore.Current with { AppProfileAssignments = assignments },
                cancellationToken);

            LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
            SelectedLiveAssignmentProfile = ThresholdProfiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
                ?? ThresholdProfiles.FirstOrDefault();
            AssignmentExeNameText = exeName;
            var statusText = hadExistingAssignment
                ? $"{exeName} assignment updated to {profileName}."
                : $"{exeName} assigned to {profileName}.";
            LiveAssignmentStatusText = statusText;
            ThresholdSettingsStatusText = statusText;
        }
        catch (ArgumentException)
        {
            LiveAssignmentStatusText = "Cannot determine exe name for the current target.";
        }
        catch (Exception error)
        {
            LiveAssignmentStatusText = $"Assignment failed: {error.Message}";
        }
    }

    public async Task RemoveSelectedAppProfileAssignmentAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedAppProfileAssignment is null)
        {
            ThresholdSettingsStatusText = "Select an app assignment to remove.";
            return;
        }

        var exeName = SelectedAppProfileAssignment.ExeName;
        var assignments = new Dictionary<string, string>(_thresholdSettingsStore.Current.AppProfileAssignments, StringComparer.OrdinalIgnoreCase);

        if (!assignments.Remove(exeName))
        {
            ThresholdSettingsStatusText = $"{exeName} is already using global fallback.";
            LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
            return;
        }

        await _thresholdSettingsStore.SaveAsync(
            _thresholdSettingsStore.Current with { AppProfileAssignments = assignments },
            cancellationToken);

        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
        AssignmentExeNameText = exeName;
        ThresholdSettingsStatusText = $"{exeName} assignment removed. It now uses global fallback thresholds.";
    }

    public async Task ResetSelectedProfileAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedThresholdProfile is null)
        {
            ThresholdSettingsStatusText = "Select a profile first.";
            return;
        }

        var defaultProfile = ThresholdProfileDefaults.CreateProfiles()
            .FirstOrDefault(profile => string.Equals(profile.Id, SelectedThresholdProfile.Id, StringComparison.OrdinalIgnoreCase));
        if (defaultProfile is null)
        {
            ThresholdSettingsStatusText = "No default exists for this profile.";
            return;
        }

        var profiles = _thresholdSettingsStore.Current.Profiles
            .Select(profile => string.Equals(profile.Id, defaultProfile.Id, StringComparison.OrdinalIgnoreCase)
                ? defaultProfile
                : profile)
            .ToList();

        await _thresholdSettingsStore.SaveAsync(
            _thresholdSettingsStore.Current with
            {
                SelectedProfileId = defaultProfile.Id,
                Profiles = profiles
            },
            cancellationToken);

        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, defaultProfile.Id);
        ThresholdSettingsStatusText = $"{defaultProfile.Name} reset to default.";
    }

    public async Task ResetAllThresholdSettingsAsync(CancellationToken cancellationToken = default)
    {
        var defaults = ThresholdProfileDefaults.CreateSettings();
        await _thresholdSettingsStore.SaveAsync(
            defaults with
            {
                AntiNoise = _thresholdSettingsStore.Current.AntiNoise,
                Capture = _thresholdSettingsStore.Current.Capture,
                Retention = _thresholdSettingsStore.Current.Retention,
                Export = _thresholdSettingsStore.Current.Export,
                Recommendations = _thresholdSettingsStore.Current.Recommendations,
                WatchJournal = _thresholdSettingsStore.Current.WatchJournal,
                SuspiciousWatchlist = _thresholdSettingsStore.Current.SuspiciousWatchlist,
                ProcessBans = _thresholdSettingsStore.Current.ProcessBans,
                Updates = _thresholdSettingsStore.Current.Updates
            },
            cancellationToken);
        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current);
        ThresholdSettingsStatusText = "All profiles, assignments, and global fallback reset to defaults.";
    }

    public async Task SaveRetentionSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedRetentionOption is null)
        {
            StorageStatusText = "Select a retention policy first.";
            return;
        }

        var retention = new RetentionSettings { RetentionDays = SelectedRetentionOption.Days };
        await _thresholdSettingsStore.SaveAsync(
            _thresholdSettingsStore.Current with { Retention = retention },
            cancellationToken);
        var deleted = await ApplyRetentionPolicyAsync(retention, cancellationToken);
        await ReloadSessionsAsync(cancellationToken: cancellationToken);
        StorageStatusText = deleted > 0
            ? $"Retention saved. Deleted {deleted:N0} old sessions."
            : "Retention saved.";
    }

    public async Task SaveExportSettingsAsync(CancellationToken cancellationToken = default)
    {
        var directory = ExportDirectoryText.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            ExportStatusText = "Choose an export folder first.";
            return;
        }

        try
        {
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            _exportService.SetExportDirectory(directory);
            await _thresholdSettingsStore.SaveAsync(
                _thresholdSettingsStore.Current with
                {
                    Export = new ExportSettings { ExportDirectory = directory }
                },
                cancellationToken);
            ExportDirectoryText = directory;
            ExportStatusText = $"Export folder saved: {directory}";
            await RefreshExportFilesAsync(cancellationToken, updateStatus: false);
        }
        catch (Exception error)
        {
            ExportStatusText = $"Export folder save failed: {error.Message}";
        }
    }

    public async Task ResetExportDirectoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(_defaultExportDirectory);
            _exportService.SetExportDirectory(_defaultExportDirectory);
            await _thresholdSettingsStore.SaveAsync(
                _thresholdSettingsStore.Current with { Export = new ExportSettings() },
                cancellationToken);
            ExportDirectoryText = _defaultExportDirectory;
            ExportStatusText = $"Export folder reset to default: {_defaultExportDirectory}";
            await RefreshExportFilesAsync(cancellationToken, updateStatus: false);
        }
        catch (Exception error)
        {
            ExportStatusText = $"Export folder reset failed: {error.Message}";
        }
    }

    public async Task RefreshExportFilesAsync(CancellationToken cancellationToken = default, bool updateStatus = true)
    {
        try
        {
            var selectedPath = SelectedExportFile?.Path;
            var files = await _exportService.ListExportsAsync(cancellationToken);
            ExportFiles.ReplaceWith(files.Select(file => new ExportFileItemViewModel(file)));
            SelectedExportFile = string.IsNullOrWhiteSpace(selectedPath)
                ? ExportFiles.FirstOrDefault()
                : ExportFiles.FirstOrDefault(file => string.Equals(file.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
                    ?? ExportFiles.FirstOrDefault();
            if (updateStatus)
            {
                ExportStatusText = $"{ExportFiles.Count:N0} export files shown.";
            }
        }
        catch (Exception error)
        {
            ExportStatusText = $"Could not refresh exports: {error.Message}";
        }
    }

    public async Task OpenSelectedExportAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedExportFile is null)
        {
            ExportStatusText = "Select an export file first.";
            return;
        }

        try
        {
            await _exportService.OpenExportAsync(SelectedExportFile.Path, cancellationToken);
            ExportStatusText = $"Opened export: {SelectedExportFile.FileName}";
        }
        catch (Exception error)
        {
            ExportStatusText = $"Could not open export: {error.Message}";
        }
    }

    public async Task OpenExportDirectoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _exportService.OpenExportDirectoryAsync(cancellationToken);
            ExportStatusText = $"Opened export folder: {_exportService.ExportDirectory}";
        }
        catch (Exception error)
        {
            ExportStatusText = $"Could not open export folder: {error.Message}";
        }
    }

    public async Task SaveUpdateSettingsAsync(CancellationToken cancellationToken = default)
    {
        var updates = ReadUpdateSettingsFromUi(_thresholdSettingsStore.Current.Updates.LastCheckedAt);
        await _thresholdSettingsStore.SaveAsync(
            _thresholdSettingsStore.Current with { Updates = updates },
            cancellationToken);
        ApplyUpdateSettingsToUi(_thresholdSettingsStore.Current.Updates);
        UpdateStatusText = "Update settings saved.";
    }

    public Task CheckForUpdatesAsync(CancellationToken cancellationToken = default) =>
        CheckForUpdatesCoreAsync(automatic: false, cancellationToken);

    public async Task SkipAvailableUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_availableUpdateManifest is null)
        {
            UpdateStatusText = "No update version is available to skip.";
            return;
        }

        var skippedVersion = _availableUpdateManifest.Version.Trim().TrimStart('v', 'V');
        await _thresholdSettingsStore.SaveAsync(
            _thresholdSettingsStore.Current with
            {
                Updates = ReadUpdateSettingsFromUi(_thresholdSettingsStore.Current.Updates.LastCheckedAt) with
                {
                    SkippedVersion = skippedVersion
                }
            },
            cancellationToken);

        _availableUpdateManifest = null;
        IsUpdateAvailable = false;
        UpdateStatusText = $"Version {skippedVersion} skipped. Automatic checks will ignore it until a newer version is released.";
    }

    public async Task DownloadAndLaunchUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_availableUpdateManifest is null)
        {
            UpdateStatusText = "No downloadable update is selected. Check for updates first.";
            return;
        }

        try
        {
            IsCheckingForUpdates = true;
            UpdateStatusText = "Downloading update installer...";
            var installerPath = await _updateService.DownloadInstallerAsync(
                _availableUpdateManifest,
                _updateDownloadDirectory,
                cancellationToken);
            DownloadedUpdateInstallerPath = installerPath;
            UpdateStatusText = $"Installer downloaded: {installerPath}. Launching installer...";
            await _updateService.LaunchInstallerAsync(installerPath, cancellationToken);
            _isUpdateRestartRequested = true;
            UpdateStatusText = "Installer launched. Session Perf Tracker will exit now so the update can replace the running files.";
            UpdateInstallerLaunched?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "Update download cancelled.";
        }
        catch (Exception error)
        {
            UpdateStatusText = $"Update install launch failed: {error.Message}";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    public async Task DeleteAllSessionsAsync(CancellationToken cancellationToken = default)
    {
        var deleted = await _sessionStore.DeleteAllSessionsAsync(cancellationToken);
        await ReloadSessionsAsync(cancellationToken: cancellationToken);
        StorageStatusText = $"Deleted {deleted:N0} sessions.";
    }

    public async Task DeleteSessionsOlderThanAsync(int days, CancellationToken cancellationToken = default)
    {
        var deleted = await _sessionStore.DeleteSessionsOlderThanAsync(TimeSpan.FromDays(days), cancellationToken);
        await ReloadSessionsAsync(cancellationToken: cancellationToken);
        StorageStatusText = $"Deleted {deleted:N0} sessions older than {days:N0} day{(days == 1 ? string.Empty : "s")}.";
    }

    public async Task DeleteFilteredSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!HasActiveSessionFilter())
        {
            StorageStatusText = "Apply a Sessions filter before deleting filtered sessions.";
            return;
        }

        var ids = Sessions.Select(session => session.Id).ToArray();
        if (ids.Length == 0)
        {
            StorageStatusText = "No filtered sessions to delete.";
            return;
        }

        var deleted = await _sessionStore.DeleteSessionsAsync(ids, cancellationToken);
        await ReloadSessionsAsync(cancellationToken: cancellationToken);
        StorageStatusText = $"Deleted {deleted:N0} filtered sessions.";
    }

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

    public Task PromoteSelectedRecommendationAsync(CancellationToken cancellationToken = default) =>
        SelectedProfileRecommendation is null
            ? SetRecommendationStatusAsync("Select a recommendation to promote.")
            : PromoteRecommendationsAsync([SelectedProfileRecommendation], cancellationToken);

    public Task PromoteRecommendationsAsync(
        IReadOnlyList<ProfileRecommendationViewModel> recommendations,
        CancellationToken cancellationToken = default) =>
        PromoteRecommendationsCoreAsync(recommendations, cancellationToken);

    public Task DenySelectedRecommendationAsync(CancellationToken cancellationToken = default) =>
        SelectedProfileRecommendation is null
            ? SetRecommendationStatusAsync("Select a recommendation to ignore.")
            : DenyRecommendationsAsync([SelectedProfileRecommendation], cancellationToken);

    public Task DenyRecommendationsAsync(
        IReadOnlyList<ProfileRecommendationViewModel> recommendations,
        CancellationToken cancellationToken = default) =>
        DenyRecommendationsCoreAsync(recommendations, cancellationToken);

    public async Task AssignSelectedGlobalProcessProfileAsync(CancellationToken cancellationToken = default)
    {
        var selectedProcess = GetSelectedGlobalWatchRow();
        if (selectedProcess is null)
        {
            GlobalWatchStatusText = "Select a process or exe group first.";
            return;
        }

        if (SelectedLiveAssignmentProfile is null)
        {
            GlobalWatchStatusText = "Select a profile in the right panel first.";
            return;
        }

        var exeName = NormalizeExeNameForAssignment(selectedProcess.ExeName);
        if (string.IsNullOrWhiteSpace(exeName))
        {
            GlobalWatchStatusText = "Could not determine exe name for assignment.";
            return;
        }

        var settings = _thresholdSettingsStore.Current;
        var assignments = new Dictionary<string, string>(settings.AppProfileAssignments, StringComparer.OrdinalIgnoreCase)
        {
            [exeName] = SelectedLiveAssignmentProfile.Id
        };

        await _thresholdSettingsStore.SaveAsync(
            settings with { AppProfileAssignments = assignments },
            cancellationToken);
        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
        GlobalWatchStatusText = $"{exeName} assigned to {SelectedLiveAssignmentProfile.Name}. Refreshing profile health on next scan.";
    }

    public void OpenRecommendationsForSelectedGlobalProcess()
    {
        var selectedProcess = GetSelectedGlobalWatchRow();
        if (selectedProcess is null)
        {
            ProfileRecommendationStatusText = "Select a process or exe group first.";
            return;
        }

        var recommendation = ProfileRecommendations.FirstOrDefault(item =>
            string.Equals(item.ExeName, selectedProcess.ExeName, StringComparison.OrdinalIgnoreCase));
        if (recommendation is null)
        {
            ProfileRecommendationStatusText = $"No active recommendation for {selectedProcess.ExeName}.";
        }
        else
        {
            SelectedProfileRecommendation = recommendation;
            ProfileRecommendationStatusText = $"{recommendation.ExeName} recommendation selected.";
        }

        SelectedTabIndex = GlobalWatchTabIndex;
        SelectedGlobalWatchSectionIndex = GlobalWatchRecommendationsSectionIndex;
    }

    public bool SelectRecommendationTargetForOverview(ProfileRecommendationViewModel? recommendation = null)
    {
        recommendation ??= SelectedProfileRecommendation;
        if (recommendation is null)
        {
            ProfileRecommendationStatusText = "Select a recommendation first.";
            return false;
        }

        var selected = SelectGlobalWatchTargetForExeOrPid(
            recommendation.ExeName,
            processId: null,
            preferApplications: true,
            statusContext: "recommendation",
            navigateToOverview: true);
        ProfileRecommendationStatusText = selected
            ? $"{recommendation.ExeName} selected in Global Watch Overview."
            : GlobalWatchStatusText;
        return selected;
    }

    public bool SelectRecommendationTargetForInspector(ProfileRecommendationViewModel? recommendation = null)
    {
        recommendation ??= SelectedProfileRecommendation;
        if (recommendation is null)
        {
            ProfileRecommendationStatusText = "Select a recommendation first.";
            return false;
        }

        var activeRow = FindActiveGlobalWatchRowForInspector(
            recommendation.ExeName,
            processId: null,
            normalizedPath: null,
            preferApplications: true);
        SetProcessInspectorTarget(ProcessInspectorTargetViewModel.FromRecommendation(
            recommendation.Recommendation,
            activeRow));
        ProfileRecommendationStatusText = activeRow is null
            ? $"{recommendation.ExeName} loaded in Process Inspector from saved recommendation; it is not running now."
            : $"{recommendation.ExeName} loaded in Process Inspector.";
        return true;
    }

    public bool SelectJournalTargetForOverview(GlobalWatchJournalGroupViewModel? group)
    {
        if (group is null)
        {
            GlobalWatchJournalStatusText = "Select a journal group first.";
            return false;
        }

        var preferApplications = group.ModeText.Contains("Applications", StringComparison.OrdinalIgnoreCase)
            || group.Entries.Count > 1
            || group.ProcessId is null;

        var selected = SelectGlobalWatchTargetForExeOrPid(
            group.ExeName,
            group.ProcessId,
            preferApplications,
            statusContext: "journal entry",
            navigateToOverview: true);
        GlobalWatchJournalStatusText = selected
            ? $"{group.ExeName} selected in Global Watch Overview."
            : GlobalWatchStatusText;
        return selected;
    }

    public bool SelectJournalTargetForInspector(GlobalWatchJournalGroupViewModel? group)
    {
        if (group is null)
        {
            GlobalWatchJournalStatusText = "Select a journal group first.";
            return false;
        }

        var preferApplications = group.ModeText.Contains("Applications", StringComparison.OrdinalIgnoreCase)
            || group.Entries.Count > 1
            || group.ProcessId is null;

        var activeRow = FindActiveGlobalWatchRowForInspector(
            group.ExeName,
            group.ProcessId,
            normalizedPath: null,
            preferApplications);
        SetProcessInspectorTarget(ProcessInspectorTargetViewModel.FromJournalGroup(
            group,
            activeRow));
        GlobalWatchJournalStatusText = activeRow is null
            ? $"{group.ExeName} loaded in Process Inspector from saved journal data; it is not running now."
            : $"{group.ExeName} loaded in Process Inspector.";
        return true;
    }

    public async Task MarkRecommendationTargetSuspiciousAsync(
        ProfileRecommendationViewModel? recommendation = null,
        CancellationToken cancellationToken = default)
    {
        if (!SelectRecommendationTargetForInspector(recommendation))
        {
            return;
        }

        await MarkInspectorSuspiciousAsync(cancellationToken);
    }

    public async Task BanRecommendationTargetAsync(
        ProfileRecommendationViewModel? recommendation = null,
        bool killAfterBan = false,
        CancellationToken cancellationToken = default)
    {
        if (!SelectRecommendationTargetForInspector(recommendation))
        {
            return;
        }

        await BanInspectorTargetCoreAsync(killAfterBan, cancellationToken);
    }

    public async Task MarkJournalTargetSuspiciousAsync(
        GlobalWatchJournalGroupViewModel? group,
        CancellationToken cancellationToken = default)
    {
        if (!SelectJournalTargetForInspector(group))
        {
            return;
        }

        await MarkInspectorSuspiciousAsync(cancellationToken);
    }

    public async Task BanJournalTargetAsync(
        GlobalWatchJournalGroupViewModel? group,
        bool killAfterBan = false,
        CancellationToken cancellationToken = default)
    {
        if (!SelectJournalTargetForInspector(group))
        {
            return;
        }

        await BanInspectorTargetCoreAsync(killAfterBan, cancellationToken);
    }

    public async Task RemoveSelectedRecommendationDenyAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedDeniedProfileRecommendation is null)
        {
            ProfileRecommendationStatusText = "Select an ignored recommendation first.";
            return;
        }

        var denied = SelectedDeniedProfileRecommendation.Denied;
        var settings = _thresholdSettingsStore.Current;
        var recommendationSettings = settings.Recommendations;
        var deniedItems = recommendationSettings.Denied
            .Where(item => !string.Equals(item.ExeName, denied.ExeName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.SuggestedProfileId, denied.SuggestedProfileId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var history = AddRecommendationHistory(
            recommendationSettings.History,
            "recommendation_deny_removed",
            denied.ExeName,
            denied.SuggestedProfileId,
            denied.SuggestedProfileName,
            $"Deny removed for {denied.ExeName} -> {denied.SuggestedProfileName}.");

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                Recommendations = recommendationSettings with
                {
                    Denied = deniedItems,
                    History = history
                }
            },
            cancellationToken);
        RefreshRecommendationCollections();
        ProfileRecommendationStatusText = $"{denied.ExeName} recommendation re-enabled.";
    }

    public async Task MarkSelectedGlobalProcessSuspiciousAsync(CancellationToken cancellationToken = default)
    {
        var selected = GetSelectedGlobalWatchRow();
        if (selected is null)
        {
            SuspiciousWatchStatusText = "Select an application or process first.";
            return;
        }

        if (!TryGetSelectedGlobalProcessUsablePath(out _)
            || string.IsNullOrWhiteSpace(selected.NormalizedFullPath))
        {
            SuspiciousWatchStatusText = "Full path is unavailable for this process; cannot mark it suspicious.";
            return;
        }

        var settings = _thresholdSettingsStore.Current;
        var watchlist = settings.SuspiciousWatchlist;
        var items = watchlist.Items
            .Where(item => !string.Equals(item.NormalizedPath, selected.NormalizedFullPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        items.Insert(0, new SuspiciousWatchItem
        {
            NormalizedPath = selected.NormalizedFullPath,
            ExeName = NormalizeExeNameForAssignment(selected.ExeName),
            ProductName = selected.ProductName,
            CompanyName = selected.CompanyName,
            SignerStatus = selected.SignerStatus,
            MarkedAt = DateTimeOffset.UtcNow
        });

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                SuspiciousWatchlist = watchlist with { Items = items }
            },
            cancellationToken);
        RefreshSuspiciousWatchCollections();
        NotifySelectedGlobalProcessProperties();
        SuspiciousWatchStatusText = $"{selected.ExeName} marked suspicious. Future launch transitions will be logged.";
    }

    public async Task RemoveSelectedGlobalProcessSuspiciousAsync(CancellationToken cancellationToken = default)
    {
        var selected = GetSelectedGlobalWatchRow();
        if (selected is null
            || !TryGetSelectedGlobalProcessUsablePath(out _)
            || string.IsNullOrWhiteSpace(selected.NormalizedFullPath))
        {
            SuspiciousWatchStatusText = "Select a marked application or process first.";
            return;
        }

        await RemoveSuspiciousPathAsync(selected.NormalizedFullPath, cancellationToken);
    }

    public async Task RemoveSelectedSuspiciousWatchItemAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSuspiciousWatchItem is null)
        {
            SuspiciousWatchStatusText = "Select a suspicious watchlist item first.";
            return;
        }

        await RemoveSuspiciousPathAsync(SelectedSuspiciousWatchItem.Item.NormalizedPath, cancellationToken);
    }

    public bool SelectSuspiciousTargetForInspector(SuspiciousWatchItemViewModel? suspiciousItem = null)
    {
        suspiciousItem ??= SelectedSuspiciousWatchItem;
        if (suspiciousItem is null)
        {
            SuspiciousWatchStatusText = "Select a suspicious watchlist item first.";
            return false;
        }

        var activeRow = FindActiveGlobalWatchRowForInspector(
            suspiciousItem.ExeName,
            processId: null,
            suspiciousItem.Item.NormalizedPath,
            preferApplications: true);
        SetProcessInspectorTarget(ProcessInspectorTargetViewModel.FromSuspiciousItem(
            suspiciousItem.Item,
            activeRow));
        SuspiciousWatchStatusText = activeRow is null
            ? $"{suspiciousItem.ExeName} loaded in Process Inspector from watchlist; it is not running now."
            : $"{suspiciousItem.ExeName} loaded in Process Inspector.";
        return true;
    }

    public async Task KillSelectedGlobalProcessAsync(CancellationToken cancellationToken = default)
    {
        var selected = GetSelectedGlobalWatchRow();
        if (selected is null)
        {
            GlobalWatchStatusText = "Select a process or application first.";
            return;
        }

        var result = await _processControlService.KillProcessAsync(
            selected.ProcessId,
            "manual Global Watch hard kill",
            cancellationToken);
        GlobalWatchStatusText = FormatProcessControlResult("Manual hard kill", result);
        await RefreshGlobalWatchAsync(cancellationToken);
    }

    public async Task KillSelectedGlobalProcessTreeOrGroupAsync(CancellationToken cancellationToken = default)
    {
        var selected = GetSelectedGlobalWatchRow();
        if (selected is null)
        {
            GlobalWatchStatusText = "Select a process or application first.";
            return;
        }

        var result = await KillGlobalWatchRowTreeOrGroupAsync(
            selected,
            selected.IsGroup
                ? "manual Global Watch app-group hard kill"
                : "manual Global Watch process-tree hard kill",
            cancellationToken);

        GlobalWatchStatusText = FormatProcessControlResult(
            selected.IsGroup ? "Manual app-group hard kill" : "Manual process-tree hard kill",
            result);
        await RefreshGlobalWatchAsync(cancellationToken);
    }

    public Task BanSelectedGlobalProcessAsync(CancellationToken cancellationToken = default) =>
        BanSelectedGlobalProcessCoreAsync(killAfterBan: false, cancellationToken);

    public Task BanAndKillSelectedGlobalProcessAsync(CancellationToken cancellationToken = default) =>
        BanSelectedGlobalProcessCoreAsync(killAfterBan: true, cancellationToken);

    public async Task RemoveSelectedProcessBanAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedProcessBan is null)
        {
            ProcessBanStatusText = "Select a ban to remove.";
            return;
        }

        var selected = SelectedProcessBan.Rule;
        var settings = _thresholdSettingsStore.Current;
        var processBans = settings.ProcessBans;
        var active = processBans.Active
            .Where(item => !string.Equals(item.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var history = processBans.History.ToList();
        history.Insert(0, new ProcessBanEvent
        {
            Id = $"ban_event_{Guid.NewGuid():N}",
            Timestamp = DateTimeOffset.UtcNow,
            NormalizedPath = selected.NormalizedPath,
            ExeName = selected.ExeName,
            ProductName = selected.ProductName,
            CompanyName = selected.CompanyName,
            SignerStatus = selected.SignerStatus,
            Action = "ban_removed",
            DurationLabel = selected.DurationLabel,
            Details = "Manual allow/remove from Bans tab."
        });

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                ProcessBans = processBans with
                {
                    Active = active,
                    History = history
                }
            },
            cancellationToken);
        _runningBannedPaths.Remove(selected.NormalizedPath);
        RefreshProcessBanCollections();
        ProcessBanStatusText = $"{selected.ExeName} ban removed. Future launches are allowed unless another ban exists.";
    }

    private async Task BanSelectedGlobalProcessCoreAsync(bool killAfterBan, CancellationToken cancellationToken)
    {
        var selected = GetSelectedGlobalWatchRow();
        if (selected is null)
        {
            ProcessBanStatusText = "Select a process or application first.";
            return;
        }

        if (!TryGetSelectedGlobalProcessUsablePath(out _)
            || string.IsNullOrWhiteSpace(selected.NormalizedFullPath))
        {
            ProcessBanStatusText = "Full path is unavailable; cannot create a ban.";
            return;
        }

        var duration = SelectedProcessBanDurationOption ?? ProcessBanDurationOptions.First();
        var now = DateTimeOffset.UtcNow;
        var settings = _thresholdSettingsStore.Current;
        var processBans = settings.ProcessBans;
        var active = processBans.Active
            .Where(item => !string.Equals(item.NormalizedPath, selected.NormalizedFullPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var rule = new ProcessBanRule
        {
            Id = $"ban_{Guid.NewGuid():N}",
            NormalizedPath = selected.NormalizedFullPath,
            ExeName = NormalizeExeNameForAssignment(selected.ExeName),
            ProductName = selected.ProductName,
            CompanyName = selected.CompanyName,
            SignerStatus = selected.SignerStatus,
            CreatedAt = now,
            ExpiresAt = duration.Duration is null ? null : now + duration.Duration.Value,
            DurationLabel = duration.Label,
            Reason = "Manual ban from Global Watch."
        };
        active.Insert(0, rule);

        var history = processBans.History.ToList();
        history.Insert(0, CreateProcessBanEvent(
            rule,
            "ban_created",
            selected,
            terminatedCount: 0,
            details: killAfterBan
                ? "Created from Global Watch; selected target will be killed immediately."
                : "Created from Global Watch."));

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                ProcessBans = processBans with
                {
                    Active = active,
                    History = history
                }
            },
            cancellationToken);

        RefreshProcessBanCollections();
        NotifySelectedGlobalProcessProperties();
        ProcessBanStatusText = $"{rule.ExeName} banned for {rule.DurationLabel}. Enforcement happens on Global Watch scans while this utility is running.";

        if (killAfterBan)
        {
            var result = await KillGlobalWatchRowTreeOrGroupAsync(
                selected,
                "manual ban plus hard kill",
                cancellationToken);
            var latestSettings = _thresholdSettingsStore.Current;
            var latestProcessBans = latestSettings.ProcessBans;
            var latestHistory = latestProcessBans.History.ToList();
            latestHistory.Insert(0, CreateProcessBanEvent(
                rule,
                "ban_kill",
                selected,
                result.TerminatedCount,
                result.Messages.Count == 0
                    ? "Ban + kill executed immediately."
                    : string.Join(" ", result.Messages.Take(3))));
            await _thresholdSettingsStore.SaveAsync(
                latestSettings with
                {
                    ProcessBans = latestProcessBans with
                    {
                        History = latestHistory
                            .OrderByDescending(entry => entry.Timestamp)
                            .Take(latestProcessBans.MaxHistory)
                            .ToList()
                    }
                },
                cancellationToken);
            RefreshProcessBanCollections();
            ProcessBanStatusText = $"{rule.ExeName} banned for {rule.DurationLabel}; {result.TerminatedCount:N0} running process{(result.TerminatedCount == 1 ? string.Empty : "es")} killed now.";
            GlobalWatchStatusText = FormatProcessControlResult("Ban + hard kill", result);
            await RefreshGlobalWatchAsync(cancellationToken);
        }
    }

    private async Task BanInspectorTargetCoreAsync(bool killAfterBan, CancellationToken cancellationToken)
    {
        var target = _processInspectorTarget;
        if (target is null)
        {
            ProcessBanStatusText = "Open a process or saved watch item in Details first.";
            return;
        }

        if (!TryGetInspectorUsablePath(out _))
        {
            ProcessBanStatusText = "Full path is unavailable; cannot create a ban.";
            return;
        }

        var duration = SelectedProcessBanDurationOption ?? ProcessBanDurationOptions.First();
        var now = DateTimeOffset.UtcNow;
        var settings = _thresholdSettingsStore.Current;
        var processBans = settings.ProcessBans;
        var active = processBans.Active
            .Where(item => !string.Equals(item.NormalizedPath, target.NormalizedFullPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var rule = new ProcessBanRule
        {
            Id = $"ban_{Guid.NewGuid():N}",
            NormalizedPath = target.NormalizedFullPath,
            ExeName = NormalizeExeNameForAssignment(target.ExeName),
            ProductName = target.ProductName,
            CompanyName = target.CompanyName,
            SignerStatus = target.SignerStatus,
            CreatedAt = now,
            ExpiresAt = duration.Duration is null ? null : now + duration.Duration.Value,
            DurationLabel = duration.Label,
            Reason = $"Manual ban from Process Inspector ({target.SourceText})."
        };
        active.Insert(0, rule);

        var history = processBans.History.ToList();
        history.Insert(0, CreateProcessBanEvent(
            rule,
            "ban_created",
            target.ActiveRow,
            terminatedCount: 0,
            details: killAfterBan
                ? "Created from Process Inspector; running target will be killed when available."
                : "Created from Process Inspector."));

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                ProcessBans = processBans with
                {
                    Active = active,
                    History = history
                        .OrderByDescending(entry => entry.Timestamp)
                        .Take(processBans.MaxHistory)
                        .ToList()
                }
            },
            cancellationToken);

        RefreshProcessBanCollections();
        NotifyInspectorTargetProperties();
        ProcessBanStatusText = $"{rule.ExeName} banned for {rule.DurationLabel}. Enforcement happens on Global Watch scans while this utility is running.";

        if (!killAfterBan)
        {
            return;
        }

        if (target.ActiveRow is null)
        {
            ProcessBanStatusText = $"{rule.ExeName} banned for {rule.DurationLabel}. It is not running now, so nothing was killed immediately.";
            return;
        }

        var result = await KillGlobalWatchRowTreeOrGroupAsync(
            target.ActiveRow,
            "manual Process Inspector ban plus hard kill",
            cancellationToken);
        var latestSettings = _thresholdSettingsStore.Current;
        var latestProcessBans = latestSettings.ProcessBans;
        var latestHistory = latestProcessBans.History.ToList();
        latestHistory.Insert(0, CreateProcessBanEvent(
            rule,
            "ban_kill",
            target.ActiveRow,
            result.TerminatedCount,
            result.Messages.Count == 0
                ? "Ban + kill executed immediately from Process Inspector."
                : string.Join(" ", result.Messages.Take(3))));
        await _thresholdSettingsStore.SaveAsync(
            latestSettings with
            {
                ProcessBans = latestProcessBans with
                {
                    History = latestHistory
                        .OrderByDescending(entry => entry.Timestamp)
                        .Take(latestProcessBans.MaxHistory)
                        .ToList()
                }
            },
            cancellationToken);
        RefreshProcessBanCollections();
        ProcessBanStatusText = $"{rule.ExeName} banned for {rule.DurationLabel}; {result.TerminatedCount:N0} running process{(result.TerminatedCount == 1 ? string.Empty : "es")} killed now.";
        GlobalWatchStatusText = FormatProcessControlResult("Inspector ban + hard kill", result);
        await RefreshGlobalWatchAsync(cancellationToken);
    }

    private Task<ProcessControlResult> KillGlobalWatchRowTreeOrGroupAsync(
        GlobalProcessRowViewModel selected,
        string reason,
        CancellationToken cancellationToken)
    {
        return selected.IsGroup
            ? _processControlService.KillProcessesAsync(
                selected.IncludedProcesses.Select(process => process.ProcessId).ToArray(),
                reason,
                cancellationToken)
            : _processControlService.KillProcessTreeAsync(
                selected.ProcessId,
                reason,
                cancellationToken);
    }

    private Task SetRecommendationStatusAsync(string status)
    {
        ProfileRecommendationStatusText = status;
        return Task.CompletedTask;
    }

    private async Task PromoteRecommendationsCoreAsync(
        IReadOnlyList<ProfileRecommendationViewModel> recommendations,
        CancellationToken cancellationToken)
    {
        var selected = recommendations
            .Where(item => item is not null)
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (selected.Length == 0)
        {
            ProfileRecommendationStatusText = "Select one or more recommendations to promote.";
            return;
        }

        var settings = _thresholdSettingsStore.Current;
        var recommendationSettings = settings.Recommendations;
        var assignments = new Dictionary<string, string>(settings.AppProfileAssignments, StringComparer.OrdinalIgnoreCase);
        var active = recommendationSettings.Active.ToList();
        var history = recommendationSettings.History.ToList();

        foreach (var item in selected)
        {
            var recommendation = item.Recommendation;
            assignments[recommendation.ExeName] = recommendation.SuggestedProfileId;
            _globalWatchOverLimitWarnings.Remove(recommendation.ExeName);
            active.RemoveAll(activeItem => string.Equals(activeItem.Id, recommendation.Id, StringComparison.OrdinalIgnoreCase));
            history = AddRecommendationHistory(
                history,
                "recommendation_promoted",
                recommendation.ExeName,
                recommendation.SuggestedProfileId,
                recommendation.SuggestedProfileName,
                $"{recommendation.ExeName} promoted to {recommendation.SuggestedProfileName}.");
        }

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                AppProfileAssignments = assignments,
                Recommendations = recommendationSettings with
                {
                    Active = active,
                    History = history
                }
            },
            cancellationToken);

        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
        ApplyGlobalWatchFilterAndSort();
        ProfileRecommendationStatusText = $"Promoted {selected.Length:N0} recommendation{(selected.Length == 1 ? string.Empty : "s")}.";
    }

    private async Task DenyRecommendationsCoreAsync(
        IReadOnlyList<ProfileRecommendationViewModel> recommendations,
        CancellationToken cancellationToken)
    {
        var selected = recommendations
            .Where(item => item is not null)
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (selected.Length == 0)
        {
            ProfileRecommendationStatusText = "Select one or more recommendations to ignore.";
            return;
        }

        var settings = _thresholdSettingsStore.Current;
        var recommendationSettings = settings.Recommendations;
        var active = recommendationSettings.Active.ToList();
        var denied = recommendationSettings.Denied.ToList();
        var history = recommendationSettings.History.ToList();
        var now = DateTimeOffset.UtcNow;

        foreach (var item in selected)
        {
            var recommendation = item.Recommendation;
            _globalWatchOverLimitWarnings.Remove(recommendation.ExeName);
            active.RemoveAll(activeItem => string.Equals(activeItem.Id, recommendation.Id, StringComparison.OrdinalIgnoreCase));
            if (!denied.Any(deniedItem => string.Equals(deniedItem.ExeName, recommendation.ExeName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(deniedItem.SuggestedProfileId, recommendation.SuggestedProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                denied.Add(new DeniedProfileRecommendation
                {
                    ExeName = recommendation.ExeName,
                    SuggestedProfileId = recommendation.SuggestedProfileId,
                    SuggestedProfileName = recommendation.SuggestedProfileName,
                    DeniedAt = now,
                    Reason = recommendation.Reason
                });
            }

            history = AddRecommendationHistory(
                history,
                "recommendation_denied",
                recommendation.ExeName,
                recommendation.SuggestedProfileId,
                recommendation.SuggestedProfileName,
                $"{recommendation.ExeName} recommendation ignored: {recommendation.Reason}");
        }

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                Recommendations = recommendationSettings with
                {
                    Active = active,
                    Denied = denied.OrderByDescending(item => item.DeniedAt).ToList(),
                    History = history
                }
            },
            cancellationToken);

        RefreshRecommendationCollections();
        ProfileRecommendationStatusText = $"Ignored {selected.Length:N0} recommendation{(selected.Length == 1 ? string.Empty : "s")}.";
    }

    private void RefreshRecommendationCollections()
    {
        var selectedId = SelectedProfileRecommendation?.Id;
        var selectedDenied = SelectedDeniedProfileRecommendation?.Denied;
        ProfileRecommendations.ReplaceWith(_thresholdSettingsStore.Current.Recommendations.Active
            .OrderByDescending(item => item.LastSeen)
            .Select(item => new ProfileRecommendationViewModel(item)));
        DeniedProfileRecommendations.ReplaceWith(_thresholdSettingsStore.Current.Recommendations.Denied
            .OrderByDescending(item => item.DeniedAt)
            .Select(item => new DeniedProfileRecommendationViewModel(item)));
        SelectedProfileRecommendation = string.IsNullOrWhiteSpace(selectedId)
            ? ProfileRecommendations.FirstOrDefault()
            : ProfileRecommendations.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                ?? ProfileRecommendations.FirstOrDefault();
        SelectedDeniedProfileRecommendation = selectedDenied is null
            ? DeniedProfileRecommendations.FirstOrDefault()
            : DeniedProfileRecommendations.FirstOrDefault(item =>
                string.Equals(item.ExeName, selectedDenied.ExeName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Denied.SuggestedProfileId, selectedDenied.SuggestedProfileId, StringComparison.OrdinalIgnoreCase))
                ?? DeniedProfileRecommendations.FirstOrDefault();
        NotifySelectedGlobalProcessProperties();
    }

    private void RefreshGlobalWatchJournalCollection()
    {
        var recentEntries = _thresholdSettingsStore.Current.WatchJournal.Entries
            .OrderByDescending(item => item.Timestamp)
            .Take(GlobalWatchJournalUiLimit)
            .ToArray();
        GlobalWatchJournalEntries.ReplaceWith(recentEntries
            .Select(item => new GlobalWatchJournalEntryViewModel(item)));
        GlobalWatchJournalGroups.ReplaceWith(recentEntries
            .GroupBy(CreateGlobalWatchJournalGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GlobalWatchJournalGroupViewModel(group.ToArray()))
            .OrderByDescending(group => group.Entry.Timestamp));
        OnPropertyChanged(nameof(HasGlobalWatchJournalEntries));
        OnPropertyChanged(nameof(HasGlobalWatchJournalGroups));
        GlobalWatchJournalStatusText = GlobalWatchJournalGroups.Count == 0
            ? "Watch Journal is empty. Near, over, critical and recommendation states will appear here."
            : $"{GlobalWatchJournalEntries.Count:N0} recent watch journal entries grouped into {GlobalWatchJournalGroups.Count:N0} branch log{(GlobalWatchJournalGroups.Count == 1 ? string.Empty : "s")}.";
    }

    private static string CreateGlobalWatchJournalGroupKey(GlobalWatchJournalEntry entry)
    {
        var exeName = NormalizeExeNameForAssignment(entry.ExeName);
        var displayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? exeName : entry.DisplayName.Trim();
        var identity = string.IsNullOrWhiteSpace(exeName) ? displayName : exeName;
        return string.Join(
            "|",
            entry.WatchMode.Trim(),
            identity,
            entry.ProfileSource.Trim(),
            entry.HealthState.Trim(),
            entry.Reason.Trim());
    }

    private void RefreshSuspiciousWatchCollections()
    {
        var selectedPath = SelectedSuspiciousWatchItem?.Item.NormalizedPath;
        var watchlist = _thresholdSettingsStore.Current.SuspiciousWatchlist;
        SuspiciousWatchItems.ReplaceWith(watchlist.Items
            .OrderByDescending(item => item.MarkedAt)
            .Select(item => new SuspiciousWatchItemViewModel(item)));
        SuspiciousLaunchEntries.ReplaceWith(watchlist.LaunchHistory
            .OrderByDescending(item => item.Timestamp)
            .Take(watchlist.MaxLaunchHistory)
            .Select(item => new SuspiciousLaunchEntryViewModel(item)));
        SelectedSuspiciousWatchItem = string.IsNullOrWhiteSpace(selectedPath)
            ? SuspiciousWatchItems.FirstOrDefault()
            : SuspiciousWatchItems.FirstOrDefault(item => string.Equals(item.Item.NormalizedPath, selectedPath, StringComparison.OrdinalIgnoreCase))
                ?? SuspiciousWatchItems.FirstOrDefault();
        OnPropertyChanged(nameof(HasSuspiciousWatchItems));
        OnPropertyChanged(nameof(HasSuspiciousLaunchEntries));
        NotifySelectedGlobalProcessProperties();
        SuspiciousWatchStatusText = SuspiciousWatchItems.Count == 0
            ? "No suspicious watchlist items yet. Mark a selected process or application from Global Watch."
            : $"{SuspiciousWatchItems.Count:N0} suspicious item{(SuspiciousWatchItems.Count == 1 ? string.Empty : "s")} watched; {SuspiciousLaunchEntries.Count:N0} launch entries shown.";
    }

    private void RefreshProcessBanCollections()
    {
        var selectedId = SelectedProcessBan?.Rule.Id;
        var processBans = _thresholdSettingsStore.Current.ProcessBans;
        ProcessBans.ReplaceWith(processBans.Active
            .OrderBy(rule => rule.ExpiresAt ?? DateTimeOffset.MaxValue)
            .ThenByDescending(rule => rule.CreatedAt)
            .Select(rule => new ProcessBanRuleViewModel(rule)));
        ProcessBanEvents.ReplaceWith(processBans.History
            .OrderByDescending(entry => entry.Timestamp)
            .Take(processBans.MaxHistory)
            .Select(entry => new ProcessBanEventViewModel(entry)));
        SelectedProcessBan = string.IsNullOrWhiteSpace(selectedId)
            ? ProcessBans.FirstOrDefault()
            : ProcessBans.FirstOrDefault(item => string.Equals(item.Rule.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                ?? ProcessBans.FirstOrDefault();
        OnPropertyChanged(nameof(HasProcessBans));
        OnPropertyChanged(nameof(HasProcessBanEvents));
        OnPropertyChanged(nameof(CanRemoveSelectedProcessBan));
        ProcessBanStatusText = ProcessBans.Count == 0
            ? "No active process bans. Create one from Global Watch Overview or Process Inspector."
            : $"{ProcessBans.Count:N0} active ban{(ProcessBans.Count == 1 ? string.Empty : "s")}; enforcement runs during Global Watch scans.";
    }

    private static bool IsProcessBanActive(ProcessBanRule rule, DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(rule.NormalizedPath)
        && (rule.ExpiresAt is null || rule.ExpiresAt > now);

    private static ProcessBanEvent CreateProcessBanEvent(
        ProcessBanRule rule,
        string action,
        GlobalProcessRowViewModel? row,
        int terminatedCount,
        string? details)
    {
        return new ProcessBanEvent
        {
            Id = $"ban_event_{Guid.NewGuid():N}",
            Timestamp = DateTimeOffset.UtcNow,
            NormalizedPath = rule.NormalizedPath,
            ExeName = rule.ExeName,
            ProductName = string.IsNullOrWhiteSpace(row?.ProductName) ? rule.ProductName : row.ProductName,
            CompanyName = string.IsNullOrWhiteSpace(row?.CompanyName) ? rule.CompanyName : row.CompanyName,
            SignerStatus = string.IsNullOrWhiteSpace(row?.SignerStatus) ? rule.SignerStatus : row.SignerStatus,
            Action = action,
            DurationLabel = rule.DurationLabel,
            ProcessId = row?.IsGroup == true ? null : row?.ProcessId,
            ProcessName = row?.DisplayName,
            TerminatedCount = terminatedCount,
            Details = details
        };
    }

    private static string FormatProcessControlResult(string action, ProcessControlResult result)
    {
        var detail = result.Messages.Count == 0
            ? string.Empty
            : $" {string.Join(" ", result.Messages.Take(2))}";
        return $"{action}: {result.TerminatedCount:N0}/{result.RequestedCount:N0} process{(result.RequestedCount == 1 ? string.Empty : "es")} terminated.{detail}";
    }

    private async Task RemoveSuspiciousPathAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        var settings = _thresholdSettingsStore.Current;
        var watchlist = settings.SuspiciousWatchlist;
        var items = watchlist.Items
            .Where(item => !string.Equals(item.NormalizedPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (items.Count == watchlist.Items.Count)
        {
            SuspiciousWatchStatusText = "Selected item is not marked suspicious.";
            return;
        }

        _runningSuspiciousPaths.Remove(normalizedPath);
        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                SuspiciousWatchlist = watchlist with { Items = items }
            },
            cancellationToken);
        RefreshSuspiciousWatchCollections();
        NotifySelectedGlobalProcessProperties();
        SuspiciousWatchStatusText = "Suspicious mark removed. Existing launch history is kept for review.";
    }

    private static GlobalWatchJournalEntry CreateGlobalWatchJournalEntry(
        GlobalProcessRowViewModel row,
        DateTimeOffset capturedAt,
        string healthState,
        string reason,
        string? recommendationId = null)
    {
        return new GlobalWatchJournalEntry
        {
            Id = $"watch_{Guid.NewGuid():N}",
            Timestamp = capturedAt,
            ExeName = NormalizeExeNameForAssignment(row.ExeName),
            DisplayName = row.DisplayName,
            ProcessId = row.IsGroup ? null : row.ProcessId,
            WatchMode = row.IsGroup ? GlobalModeApplications : GlobalModeProcesses,
            ProfileSource = row.ProfileSourceText,
            HealthState = healthState,
            Reason = reason,
            CpuPercent = row.CpuPercent,
            MemoryMb = row.MemoryMb,
            DiskReadMbPerSec = row.DiskReadMbPerSec,
            DiskWriteMbPerSec = row.DiskWriteMbPerSec,
            RecommendationId = recommendationId
        };
    }

    private static List<GlobalWatchJournalEntry> TrimGlobalWatchJournalEntries(
        IEnumerable<GlobalWatchJournalEntry> entries,
        int maxEntries)
    {
        return entries
            .OrderByDescending(item => item.Timestamp)
            .Take(Math.Clamp(maxEntries, 50, 5000))
            .ToList();
    }

    private static List<ProfileRecommendationHistoryEntry> AddRecommendationHistory(
        IReadOnlyList<ProfileRecommendationHistoryEntry> history,
        string kind,
        string exeName,
        string suggestedProfileId,
        string suggestedProfileName,
        string reason)
    {
        return new[]
            {
                new ProfileRecommendationHistoryEntry
                {
                    Kind = kind,
                    ExeName = NormalizeExeNameForAssignment(exeName),
                    SuggestedProfileId = suggestedProfileId,
                    SuggestedProfileName = suggestedProfileName,
                    Timestamp = DateTimeOffset.UtcNow,
                    Reason = reason
                }
            }
            .Concat(history)
            .Take(500)
            .ToList();
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

    private void OnSampleCollected(object? sender, MetricSample sample)
    {
        var now = DateTimeOffset.UtcNow;
        var liveRefreshMs = 2000;
        _liveTrackedProcessCount = sample.ProcessCount;
        _liveRootProcessId = sample.RootProcessId ?? _liveRootProcessId;
        if ((now - _lastLiveUiRefresh).TotalMilliseconds < liveRefreshMs)
        {
            _liveSampleCount++;
            return;
        }

        _lastLiveUiRefresh = now;
        _liveSampleCount++;

        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (IsLiveMonitorEnabled)
            {
                CurrentMetrics.ReplaceWith(MetricDisplayFactory.CreateMetricRows(sample, _liveCapabilities));
            }

            LiveSnapshotText = $"latest sample +{TimeSpan.FromMilliseconds(sample.ElapsedMs):mm\\:ss}";
            NotifyLivePanelProperties();
            NotifySummaryProperties();
        });
    }

    private void OnEventsDetected(object? sender, IReadOnlyList<PerformanceEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var performanceEvent in events.OrderByDescending(item => item.ElapsedMs))
            {
                VisibleEvents.Insert(0, new EventRowViewModel(performanceEvent));
            }

            while (VisibleEvents.Count > 12)
            {
                VisibleEvents.RemoveAt(VisibleEvents.Count - 1);
            }

            _liveEventCount += events.Count;
            _liveSpikeCount += events.Count(item => HasEventKind(item, EventKind.Spike));
            _liveBreachCount += events.Count(item => HasEventKind(item, EventKind.ThresholdBreach));
            NotifyLivePanelProperties();
            NotifySummaryProperties();
        });
    }

    private void ApplyProcessFilter()
    {
        var query = ProcessFilterText.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allRunningTargets
            : _allRunningTargets
                .Where(target => target.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var previousProcessId = SelectedRunningTarget?.ProcessId;
        RunningTargets.ReplaceWith(filtered);
        SelectedRunningTarget = previousProcessId is null
            ? RunningTargets.FirstOrDefault()
            : RunningTargets.FirstOrDefault(target => target.ProcessId == previousProcessId) ?? RunningTargets.FirstOrDefault();
    }

    private void RefreshAssignedTargets()
    {
        var previousExeName = SelectedAssignedTarget?.ExeName;
        var profileNames = _thresholdSettingsStore.Current.Profiles
            .ToDictionary(profile => profile.Id, profile => profile.Name, StringComparer.OrdinalIgnoreCase);

        AssignedTargets.ReplaceWith(_thresholdSettingsStore.Current.AppProfileAssignments
            .OrderBy(assignment => assignment.Key, StringComparer.OrdinalIgnoreCase)
            .Select(assignment =>
            {
                var exeName = NormalizeExeNameForAssignment(assignment.Key);
                var runningTarget = FindRunningTargetForExe(exeName);
                return new AssignedTargetOptionViewModel(
                    exeName,
                    assignment.Value,
                    profileNames.TryGetValue(assignment.Value, out var profileName) ? profileName : assignment.Value,
                    runningTarget?.ProcessId,
                    runningTarget?.DisplayName);
            }));

        SelectedAssignedTarget = !string.IsNullOrWhiteSpace(previousExeName)
            ? AssignedTargets.FirstOrDefault(target => string.Equals(target.ExeName, previousExeName, StringComparison.OrdinalIgnoreCase))
                ?? AssignedTargets.FirstOrDefault(target => target.IsRunning)
                ?? AssignedTargets.FirstOrDefault()
            : AssignedTargets.FirstOrDefault(target => target.IsRunning) ?? AssignedTargets.FirstOrDefault();
    }

    private TargetOptionViewModel? FindRunningTargetForExe(string exeName)
    {
        var normalizedExeName = NormalizeExeNameForAssignment(exeName);
        return _allRunningTargets.FirstOrDefault(target =>
            string.Equals(
                NormalizeExeNameForAssignment(GetExeNameFromTarget(target.Target)),
                normalizedExeName,
                StringComparison.OrdinalIgnoreCase));
    }

    private void OnSessionCompleted(object? sender, SessionRecord session)
    {
        if (_activeHandle is not null)
        {
            _activeHandle.SampleCollected -= OnSampleCollected;
            _activeHandle.EventsDetected -= OnEventsDetected;
            _activeHandle.Completed -= OnSessionCompleted;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var wasAutoStopped = _autoStopInProgress;
            var wasManualStop = _manualStopInProgress;
            UpdateLastCompletedSession(session);
            var nextState = ResolveCompletedLiveState(session, wasAutoStopped, wasManualStop);
            IsRecording = false;
            RecordingStatusText = wasAutoStopped
                ? _autoStopStatusText
                : $"Saved {session.Target.DisplayName}";
            LiveWarningText = wasAutoStopped
                ? _autoStopStatusText
                : string.Empty;
            LiveSnapshotText = "not recording";
            CurrentMetrics.Clear();
            VisibleEvents.Clear();
            _liveTrackedProcessCount = 0;
            _liveRootProcessId = null;
            _activeHandle = null;
            _activeStartedAt = null;
            _activeSessionProfileText = string.Empty;
            _autoStopInProgress = false;
            _manualStopInProgress = false;
            _autoStopStatusText = "Monitoring stopped. Press Start to apply changes.";
            SetLiveSessionState(nextState);
            NotifyLivePanelProperties();
            NotifyTargetSelectionProperties();
            await ReloadSessionsAsync(session.Id);
        });
    }

    private async Task WatchActiveSessionAsync(IRunningSessionHandle handle)
    {
        try
        {
            await handle.Completion;
        }
        catch (Exception error)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsRecording = false;
                RecordingStatusText = $"Recording failed: {error.Message}";
                LiveWarningText = $"Recording failed: {error.Message}";
                LiveSnapshotText = "failed";
                CurrentMetrics.Clear();
                VisibleEvents.Clear();
                _activeHandle = null;
                _activeStartedAt = null;
                _activeSessionProfileText = string.Empty;
                _autoStopInProgress = false;
                _manualStopInProgress = false;
                _liveEventCount = 0;
                _liveSpikeCount = 0;
                _liveBreachCount = 0;
                _liveTrackedProcessCount = 0;
                _liveRootProcessId = null;
                SetLiveSessionState(LiveSessionUiState.EndedUnexpectedly);
                NotifyLivePanelProperties();
                NotifyTargetSelectionProperties();
                NotifySummaryProperties();
            });
        }
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

    private void LoadThresholdSettingsIntoUi(CpuRamThresholdSettings settings, string? selectedProfileId = null)
    {
        var liveProfileId = SelectedLiveAssignmentProfile?.Id ?? ThresholdProfileDefaults.BrowsersChatsId;

        CpuThresholdPercentText = settings.CpuThresholdPercent.ToString("N0", CultureInfo.CurrentCulture);
        RamThresholdMbText = settings.RamThresholdMb.ToString("N0", CultureInfo.CurrentCulture);
        DiskReadThresholdMbPerSecText = settings.DiskReadThresholdMbPerSec.ToString("N1", CultureInfo.CurrentCulture);
        DiskWriteThresholdMbPerSecText = settings.DiskWriteThresholdMbPerSec.ToString("N1", CultureInfo.CurrentCulture);
        StartupGraceSecondsText = settings.AntiNoise.StartupGraceSeconds.ToString("N0", CultureInfo.CurrentCulture);
        EventCooldownSecondsText = settings.AntiNoise.EventCooldownSeconds.ToString("N0", CultureInfo.CurrentCulture);
        GroupingWindowSecondsText = settings.AntiNoise.GroupingWindowSeconds.ToString("N0", CultureInfo.CurrentCulture);
        SnapshotSuppressionSecondsText = settings.AntiNoise.SnapshotSuppressionSeconds.ToString("N0", CultureInfo.CurrentCulture);
        CaptureCpu = settings.Capture.CaptureCpu;
        CaptureRam = settings.Capture.CaptureRam;
        CaptureDiskRead = settings.Capture.CaptureDiskRead;
        CaptureDiskWrite = settings.Capture.CaptureDiskWrite;
        MinimizeToTrayOnClose = settings.Behavior.MinimizeToTrayOnClose;
        SelectedRetentionOption = RetentionOptions.FirstOrDefault(option => option.Days == settings.Retention.RetentionDays)
            ?? RetentionOptions.FirstOrDefault(option => option.Days == 30)
            ?? RetentionOptions.FirstOrDefault();

        ThresholdProfiles.ReplaceWith(settings.Profiles.Select(profile => new ThresholdProfileOptionViewModel(profile)));
        var preferredProfileId = selectedProfileId ?? settings.SelectedProfileId;
        SelectedThresholdProfile = ThresholdProfiles.FirstOrDefault(profile => string.Equals(profile.Id, preferredProfileId, StringComparison.OrdinalIgnoreCase))
            ?? ThresholdProfiles.FirstOrDefault();
        SelectedAssignmentProfile = ThresholdProfiles.FirstOrDefault(profile => string.Equals(profile.Id, ThresholdProfileDefaults.BrowsersChatsId, StringComparison.OrdinalIgnoreCase))
            ?? ThresholdProfiles.FirstOrDefault();
        SelectedLiveAssignmentProfile = ThresholdProfiles.FirstOrDefault(profile => string.Equals(profile.Id, liveProfileId, StringComparison.OrdinalIgnoreCase))
            ?? ThresholdProfiles.FirstOrDefault();

        var previousSessionProfileId = SelectedSessionProfileOption?.ProfileId;
        var wasAuto = SelectedSessionProfileOption?.IsAuto ?? true;
        SessionProfileOptions.ReplaceWith(new[] { new SessionProfileOptionViewModel() }
            .Concat(settings.Profiles.Select(profile => new SessionProfileOptionViewModel(profile))));
        SelectedSessionProfileOption = wasAuto
            ? SessionProfileOptions.FirstOrDefault()
            : SessionProfileOptions.FirstOrDefault(profile => string.Equals(profile.ProfileId, previousSessionProfileId, StringComparison.OrdinalIgnoreCase))
                ?? SessionProfileOptions.FirstOrDefault();

        var previousSessionFilterId = SelectedSessionProfileFilter?.ProfileId;
        var previousSessionFilterWasGlobal = SelectedSessionProfileFilter?.IsGlobalFallback ?? false;
        SessionProfileFilters.ReplaceWith(new[]
            {
                new SessionProfileFilterOptionViewModel("All profiles"),
                new SessionProfileFilterOptionViewModel("Global fallback", isGlobalFallback: true)
            }
            .Concat(settings.Profiles.Select(profile => new SessionProfileFilterOptionViewModel(profile.Name, profile.Id))));
        SelectedSessionProfileFilter = previousSessionFilterWasGlobal
            ? SessionProfileFilters.FirstOrDefault(filter => filter.IsGlobalFallback)
            : previousSessionFilterId is null
                ? SessionProfileFilters.FirstOrDefault()
                : SessionProfileFilters.FirstOrDefault(filter => string.Equals(filter.ProfileId, previousSessionFilterId, StringComparison.OrdinalIgnoreCase))
                    ?? SessionProfileFilters.FirstOrDefault();

        var profileNames = settings.Profiles.ToDictionary(profile => profile.Id, profile => profile.Name, StringComparer.OrdinalIgnoreCase);
        AppProfileAssignments.ReplaceWith(settings.AppProfileAssignments
            .OrderBy(assignment => assignment.Key, StringComparer.OrdinalIgnoreCase)
            .Select(assignment => new AppProfileAssignmentViewModel(
                assignment.Key,
                profileNames.TryGetValue(assignment.Value, out var profileName) ? profileName : assignment.Value)));
        SelectedAppProfileAssignment = AppProfileAssignments.FirstOrDefault();
        RefreshAssignedTargets();
        RefreshRecommendationCollections();
        RefreshGlobalWatchJournalCollection();
        RefreshSuspiciousWatchCollections();
        NotifyTargetSelectionProperties();
    }

    private void ApplyExportSettingsToUi(ExportSettings exportSettings)
    {
        var directory = string.IsNullOrWhiteSpace(exportSettings.ExportDirectory)
            ? _defaultExportDirectory
            : exportSettings.ExportDirectory;
        try
        {
            ExportDirectoryText = Path.GetFullPath(directory);
            _exportService.SetExportDirectory(ExportDirectoryText);
        }
        catch
        {
            ExportDirectoryText = _defaultExportDirectory;
            _exportService.SetExportDirectory(_defaultExportDirectory);
            ExportStatusText = "Saved export folder was unavailable. Default export folder is active.";
        }
    }

    private void ApplyUpdateSettingsToUi(AppUpdateSettings updateSettings)
    {
        AutomaticallyCheckForUpdates = updateSettings.AutomaticallyCheckForUpdates;
        UpdateManifestUrlText = updateSettings.ManifestUrl ?? string.Empty;
        if (updateSettings.LastCheckedAt is { } lastCheckedAt)
        {
            var skippedText = string.IsNullOrWhiteSpace(updateSettings.SkippedVersion)
                ? string.Empty
                : $" Skipped version: {updateSettings.SkippedVersion}.";
            UpdateStatusText = $"Last update check: {lastCheckedAt.ToLocalTime():MMM dd, HH:mm}.{skippedText}";
        }
    }

    private AppUpdateSettings ReadUpdateSettingsFromUi(DateTimeOffset? lastCheckedAt = null) => new()
    {
        AutomaticallyCheckForUpdates = AutomaticallyCheckForUpdates,
        ManifestUrl = string.IsNullOrWhiteSpace(UpdateManifestUrlText)
            ? null
            : UpdateManifestUrlText.Trim(),
        LastCheckedAt = lastCheckedAt,
        SkippedVersion = _thresholdSettingsStore.Current.Updates.SkippedVersion
    };

    private async Task AutoCheckForUpdatesIfNeededAsync(
        AppUpdateSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.AutomaticallyCheckForUpdates || string.IsNullOrWhiteSpace(settings.ManifestUrl))
        {
            return;
        }

        if (settings.LastCheckedAt is { } lastCheckedAt
            && DateTimeOffset.UtcNow - lastCheckedAt < TimeSpan.FromHours(24))
        {
            return;
        }

        await CheckForUpdatesCoreAsync(automatic: true, cancellationToken);
    }

    private async Task CheckForUpdatesCoreAsync(bool automatic, CancellationToken cancellationToken)
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        try
        {
            IsCheckingForUpdates = true;
            UpdateStatusText = automatic ? "Automatically checking for updates..." : "Checking for updates...";
            DownloadedUpdateInstallerPath = string.Empty;
            var result = await _updateService.CheckAsync(UpdateManifestUrlText.Trim(), cancellationToken);
            _availableUpdateManifest = result.IsUpdateAvailable ? result.Manifest : null;
            IsUpdateAvailable = result.IsUpdateAvailable && result.Manifest is not null;
            UpdateLatestVersionText = result.LatestVersion ?? "Unavailable";
            UpdateReleaseNotesText = string.IsNullOrWhiteSpace(result.Manifest?.ReleaseNotes)
                ? "No release notes in manifest."
                : result.Manifest.ReleaseNotes;
            UpdateStatusText = result.Status;

            if (result.IsUpdateAvailable && result.Manifest is not null)
            {
                var latestVersion = result.Manifest.Version.Trim().TrimStart('v', 'V');
                var skippedVersion = _thresholdSettingsStore.Current.Updates.SkippedVersion?.Trim().TrimStart('v', 'V');
                if (automatic
                    && !string.IsNullOrWhiteSpace(skippedVersion)
                    && string.Equals(latestVersion, skippedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    _availableUpdateManifest = null;
                    IsUpdateAvailable = false;
                    UpdateStatusText = $"Version {latestVersion} is skipped. Automatic checks will wait for a newer version.";
                }
                else if (automatic && UpdateAvailablePromptRequested is not null)
                {
                    var args = new UpdateAvailablePromptEventArgs(result);
                    UpdateAvailablePromptRequested.Invoke(this, args);
                    if (args.Choice == UpdatePromptChoice.UpdateNow)
                    {
                        await DownloadAndLaunchUpdateAsync(cancellationToken);
                    }
                    else if (args.Choice == UpdatePromptChoice.SkipVersion)
                    {
                        await SkipAvailableUpdateAsync(cancellationToken);
                    }
                    else
                    {
                        UpdateStatusText = $"Update {latestVersion} is available. You can install it later from Settings.";
                    }
                }
            }

            await _thresholdSettingsStore.SaveAsync(
                _thresholdSettingsStore.Current with
                {
                    Updates = ReadUpdateSettingsFromUi(DateTimeOffset.UtcNow)
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "Update check cancelled.";
        }
        catch (Exception error)
        {
            UpdateStatusText = $"Update check failed: {error.Message}";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private void LoadSelectedProfileFields()
    {
        if (SelectedThresholdProfile is null)
        {
            return;
        }

        var profile = _thresholdSettingsStore.Current.Profiles
            .FirstOrDefault(item => string.Equals(item.Id, SelectedThresholdProfile.Id, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return;
        }

        ProfileCpuThresholdPercentText = profile.Limits.CpuThresholdPercent.ToString("N0", CultureInfo.CurrentCulture);
        ProfileRamThresholdMbText = profile.Limits.RamThresholdMb.ToString("N0", CultureInfo.CurrentCulture);
        ProfileDiskReadThresholdMbPerSecText = profile.Limits.DiskReadThresholdMbPerSec.ToString("N1", CultureInfo.CurrentCulture);
        ProfileDiskWriteThresholdMbPerSecText = profile.Limits.DiskWriteThresholdMbPerSec.ToString("N1", CultureInfo.CurrentCulture);
    }

    private void StartGlobalWatch()
    {
        if (_globalWatchCts is not null)
        {
            return;
        }

        _globalWatchCts = new CancellationTokenSource();
        _ = RunGlobalWatchAsync(_globalWatchCts.Token);
    }

    private async Task RunGlobalWatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshGlobalWatchCoreAsync(manual: false, cancellationToken);
                await Task.Delay(GlobalWatchIntervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                GlobalWatchStatusText = $"Global Watch failed: {error.Message}";
            });
        }
    }

    private async Task RefreshGlobalWatchCoreAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!await _globalWatchScanLock.WaitAsync(0, cancellationToken))
        {
            if (manual)
            {
                GlobalWatchStatusText = "Global Watch scan is already running.";
            }

            return;
        }

        try
        {
            var scan = await _globalProcessScanner.ScanAsync(cancellationToken);
            var rows = scan.Processes.Select(CreateGlobalProcessRow).ToArray();
            await EnforceProcessBansAsync(rows, scan.CapturedAt, cancellationToken);
            await UpdateSuspiciousLaunchLoggingAsync(rows, scan.CapturedAt, cancellationToken);
            await UpdateProfileRecommendationsFromGlobalWatchAsync(rows, scan.CapturedAt, cancellationToken);
            var journalRows = SelectedGlobalWatchMode == GlobalModeApplications
                ? CreateGlobalWatchGroups(rows).ToArray()
                : rows;
            await UpdateGlobalWatchJournalAsync(journalRows, scan.CapturedAt, cancellationToken);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var selectedKey = _selectedGlobalProcessKey ?? GetGlobalWatchSelectionKey(SelectedGlobalProcess);
                _allGlobalProcessRows.Clear();
                _allGlobalProcessRows.AddRange(rows);
                ApplyGlobalWatchFilterAndSort(selectedKey);
                GlobalWatchLastScanText = scan.ComparedToPreviousScan is null
                    ? $"Last scan: {scan.CapturedAt.ToLocalTime():HH:mm:ss}; CPU/Disk deltas warm up on next scan"
                    : $"Last scan: {scan.CapturedAt.ToLocalTime():HH:mm:ss}; interval {scan.ComparedToPreviousScan.Value.TotalSeconds:N1}s";
                GlobalWatchStatusText = $"Global Watch: profile-aware scan every {GlobalWatchIntervalMs / 1000:N0}s. Unassigned apps use Light background fallback.";
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                GlobalWatchStatusText = $"Global Watch scan failed: {error.Message}";
            });
        }
        finally
        {
            _globalWatchScanLock.Release();
        }
    }

    private void ApplyGlobalWatchFilterAndSort(string? preferredSelectionKey = null)
    {
        var query = GlobalWatchFilterText.Trim();
        IEnumerable<GlobalProcessRowViewModel> filtered = IsGlobalWatchGroupedMode
            ? CreateGlobalWatchGroups(_allGlobalProcessRows)
            : _allGlobalProcessRows;
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(process => process.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || process.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || process.ExeName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || process.ProfileSourceText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || process.ProcessIdText.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (GlobalWatchOnlyOverLimit)
        {
            filtered = filtered.Where(process => process.IsOutOfProfile);
        }

        if (GlobalWatchOnlyCritical)
        {
            filtered = filtered.Where(process => process.IsCritical);
        }

        if (GlobalWatchOnlyUnassigned)
        {
            filtered = filtered.Where(process => process.IsUnassigned);
        }

        if (GlobalWatchOnlyNearLimit)
        {
            filtered = filtered.Where(process => process.IsNearLimit);
        }

        filtered = SelectedGlobalWatchSortMode switch
        {
            GlobalSortRam => filtered
                .OrderByDescending(process => process.MemoryMb ?? 0)
                .ThenByDescending(process => process.CpuPercent ?? 0),
            GlobalSortDisk => filtered
                .OrderByDescending(process => process.DiskTotalMbPerSec)
                .ThenByDescending(process => process.CpuPercent ?? 0),
            GlobalSortName => filtered
                .OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId),
            _ => filtered
                .OrderByDescending(process => process.CpuPercent ?? 0)
                .ThenByDescending(process => process.MemoryMb ?? 0)
        };

        var previousKey = preferredSelectionKey
            ?? _selectedGlobalProcessKey
            ?? GetGlobalWatchSelectionKey(SelectedGlobalProcess);
        var filteredList = filtered.ToList();
        GlobalWatchProcesses.ReplaceWith(filteredList);
        OnPropertyChanged(nameof(HasGlobalWatchRows));
        OnPropertyChanged(nameof(GlobalWatchEmptyStateText));
        var nextSelection = previousKey is null
            ? GlobalWatchProcesses.FirstOrDefault()
            : GlobalWatchProcesses.FirstOrDefault(process => string.Equals(process.SelectionKey, previousKey, StringComparison.OrdinalIgnoreCase))
                ?? GlobalWatchProcesses.FirstOrDefault();
        SelectedGlobalProcess = nextSelection;
        _selectedGlobalProcessKey = nextSelection?.SelectionKey;

        var displayRows = IsGlobalWatchGroupedMode
            ? CreateGlobalWatchGroups(_allGlobalProcessRows).ToArray()
            : _allGlobalProcessRows.ToArray();
        TopCpuOffenders.ReplaceWith(displayRows
            .OrderByDescending(process => process.ProfileSeverityRank)
            .ThenByDescending(process => process.CpuPercent ?? 0)
            .ThenByDescending(process => process.MemoryMb ?? 0)
            .Take(3));
        TopRamOffenders.ReplaceWith(displayRows
            .OrderByDescending(process => process.ProfileSeverityRank)
            .ThenByDescending(process => process.MemoryMb ?? 0)
            .ThenByDescending(process => process.CpuPercent ?? 0)
            .Take(3));
        TopDiskOffenders.ReplaceWith(displayRows
            .OrderByDescending(process => process.ProfileSeverityRank)
            .ThenByDescending(process => process.DiskTotalMbPerSec)
            .ThenByDescending(process => process.MemoryMb ?? 0)
            .Take(3));
    }

    private GlobalProcessRowViewModel? GetSelectedGlobalWatchRow()
    {
        if (SelectedGlobalProcess is not null
            && GlobalWatchProcesses.Any(row => ReferenceEquals(row, SelectedGlobalProcess)))
        {
            return SelectedGlobalProcess;
        }

        return _selectedGlobalProcessKey is null
            ? null
            : GlobalWatchProcesses.FirstOrDefault(row =>
                string.Equals(row.SelectionKey, _selectedGlobalProcessKey, StringComparison.OrdinalIgnoreCase));
    }

    private bool SelectGlobalWatchTargetForExeOrPid(
        string? exeName,
        int? processId,
        bool preferApplications,
        string statusContext,
        bool navigateToOverview)
    {
        var normalizedExeName = NormalizeExeNameForAssignment(exeName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedExeName))
        {
            GlobalWatchStatusText = $"Could not determine exe for {statusContext}.";
            return false;
        }

        if (_allGlobalProcessRows.Count == 0)
        {
            GlobalWatchStatusText = $"No recent Global Watch scan is available for {normalizedExeName}. Refresh and try again.";
            return false;
        }

        GlobalWatchFilterText = string.Empty;
        GlobalWatchOnlyOverLimit = false;
        GlobalWatchOnlyCritical = false;
        GlobalWatchOnlyNearLimit = false;
        GlobalWatchOnlyUnassigned = false;
        if (navigateToOverview)
        {
            SelectedTabIndex = GlobalWatchTabIndex;
            SelectedGlobalWatchSectionIndex = GlobalWatchOverviewSectionIndex;
        }

        var processRow = processId is null
            ? null
            : _allGlobalProcessRows.FirstOrDefault(row => row.ProcessId == processId.Value);
        var hasExeRows = _allGlobalProcessRows.Any(row =>
            string.Equals(row.ExeName, normalizedExeName, StringComparison.OrdinalIgnoreCase));
        if (processRow is null && !hasExeRows)
        {
            GlobalWatchStatusText = $"{normalizedExeName} is not running in the latest Global Watch scan.";
            return false;
        }

        var selectionKey = preferApplications || processRow is null
            ? $"group:{normalizedExeName}"
            : processRow.SelectionKey;
        _selectedGlobalProcessKey = selectionKey;
        SelectedGlobalWatchMode = selectionKey.StartsWith("pid:", StringComparison.OrdinalIgnoreCase)
            ? GlobalModeProcesses
            : GlobalModeApplications;
        ApplyGlobalWatchFilterAndSort(selectionKey);

        if (SelectedGlobalProcess is not null
            && string.Equals(SelectedGlobalProcess.SelectionKey, selectionKey, StringComparison.OrdinalIgnoreCase))
        {
            GlobalWatchStatusText = $"{normalizedExeName} selected from {statusContext}.";
            return true;
        }

        if (!selectionKey.StartsWith("group:", StringComparison.OrdinalIgnoreCase) && hasExeRows)
        {
            var fallbackKey = $"group:{normalizedExeName}";
            _selectedGlobalProcessKey = fallbackKey;
            SelectedGlobalWatchMode = GlobalModeApplications;
            ApplyGlobalWatchFilterAndSort(fallbackKey);
            if (SelectedGlobalProcess is not null
                && string.Equals(SelectedGlobalProcess.SelectionKey, fallbackKey, StringComparison.OrdinalIgnoreCase))
            {
                GlobalWatchStatusText = $"{normalizedExeName} application group selected from {statusContext}.";
                return true;
            }
        }

        GlobalWatchStatusText = $"Could not select a current Global Watch row for {normalizedExeName}. Refresh and try again.";
        return false;
    }

    private bool TryGetSelectedGlobalProcessUsablePath(out string path)
    {
        path = string.Empty;
        var selected = GetSelectedGlobalWatchRow();
        if (selected is null
            || string.IsNullOrWhiteSpace(selected.FullPath)
            || string.Equals(selected.FullPath, "Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(selected.FullPath);
            return Path.IsPathFullyQualified(path);
        }
        catch
        {
            path = selected.FullPath.Trim();
            return Path.IsPathFullyQualified(path);
        }
    }

    private bool TryGetInspectorUsablePath(out string path)
    {
        path = string.Empty;
        var target = _processInspectorTarget;
        if (target is null
            || !target.HasUsablePath
            || string.IsNullOrWhiteSpace(target.FullPath)
            || string.Equals(target.FullPath, "Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(target.FullPath);
            return Path.IsPathFullyQualified(path);
        }
        catch
        {
            path = target.FullPath.Trim();
            return Path.IsPathFullyQualified(path);
        }
    }

    private void SetProcessInspectorTarget(ProcessInspectorTargetViewModel target)
    {
        _processInspectorTarget = target;
        NotifyInspectorTargetProperties();
    }

    private void SelectGlobalWatchRowForAction(GlobalProcessRowViewModel row)
    {
        _selectedGlobalProcessKey = row.SelectionKey;
        SelectedGlobalWatchMode = row.IsGroup ? GlobalModeApplications : GlobalModeProcesses;
        ApplyGlobalWatchFilterAndSort(row.SelectionKey);
    }

    private GlobalProcessRowViewModel? FindActiveGlobalWatchRowForInspector(
        string? exeName,
        int? processId,
        string? normalizedPath,
        bool preferApplications)
    {
        var normalizedExeName = NormalizeExeNameForAssignment(exeName ?? string.Empty);
        var path = NormalizeFullPathForIdentity(normalizedPath);
        var candidates = _allGlobalProcessRows.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates = candidates.Where(row =>
                string.Equals(row.NormalizedFullPath, path, StringComparison.OrdinalIgnoreCase));
        }
        else if (processId is not null)
        {
            candidates = candidates.Where(row => row.ProcessId == processId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(normalizedExeName))
        {
            candidates = candidates.Where(row =>
                string.Equals(row.ExeName, normalizedExeName, StringComparison.OrdinalIgnoreCase));
        }

        var rows = candidates.ToArray();
        if (rows.Length == 0 && !string.IsNullOrWhiteSpace(normalizedExeName))
        {
            rows = _allGlobalProcessRows
                .Where(row => string.Equals(row.ExeName, normalizedExeName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (rows.Length == 0)
        {
            return null;
        }

        if (preferApplications || rows.Length > 1)
        {
            return GlobalProcessRowViewModel.CreateGroup(rows);
        }

        if (processId is not null)
        {
            return rows.FirstOrDefault(row => row.ProcessId == processId.Value) ?? rows[0];
        }

        return rows
            .OrderByDescending(row => row.CpuPercent ?? 0)
            .ThenByDescending(row => row.MemoryMb ?? 0)
            .First();
    }

    private static string NormalizeFullPathForIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || string.Equals(path.Trim(), "Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();
        }
        catch
        {
            return path.Trim().ToLowerInvariant();
        }
    }

    private static string? GetGlobalWatchSelectionKey(GlobalProcessRowViewModel? row) => row?.SelectionKey;

    private static IReadOnlyList<GlobalProcessRowViewModel> CreateGlobalWatchGroups(
        IReadOnlyList<GlobalProcessRowViewModel> rows)
    {
        return rows
            .GroupBy(row => row.ExeName, StringComparer.OrdinalIgnoreCase)
            .Select(group => GlobalProcessRowViewModel.CreateGroup(group.ToArray()))
            .ToArray();
    }

    private GlobalProcessRowViewModel CreateGlobalProcessRow(GlobalProcessSnapshot process)
    {
        var exeName = NormalizeExeNameForAssignment(process.Name);
        var settings = _thresholdSettingsStore.Current;
        var profile = ResolveGlobalWatchProfile(exeName, settings, out var isAssignedProfile);

        return new GlobalProcessRowViewModel(
            process,
            exeName,
            profile.Id,
            profile.Name,
            isAssignedProfile,
            profile.Limits);
    }

    private static ThresholdProfile ResolveGlobalWatchProfile(
        string exeName,
        CpuRamThresholdSettings settings,
        out bool isAssignedProfile)
    {
        isAssignedProfile = false;
        if (!string.IsNullOrWhiteSpace(exeName)
            && settings.AppProfileAssignments.TryGetValue(exeName, out var assignedProfileId)
            && settings.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, assignedProfileId, StringComparison.OrdinalIgnoreCase)) is { } assignedProfile)
        {
            isAssignedProfile = true;
            return assignedProfile;
        }

        return settings.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, ThresholdProfileDefaults.LightBackgroundId, StringComparison.OrdinalIgnoreCase))
            ?? ThresholdProfileDefaults.CreateProfiles().First(profile => profile.Id == ThresholdProfileDefaults.LightBackgroundId);
    }

    private async Task UpdateGlobalWatchJournalAsync(
        IReadOnlyList<GlobalProcessRowViewModel> rows,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var entriesToAdd = new List<GlobalWatchJournalEntry>();
        foreach (var row in rows.Where(row => row.IsNearLimit || row.IsOutOfProfile))
        {
            var key = $"{SelectedGlobalWatchMode}|{row.SelectionKey}|{row.HealthBadgeText}|{row.ProfileReason}";
            if (_globalWatchJournalLastEntryByKey.TryGetValue(key, out var lastEntryAt)
                && capturedAt - lastEntryAt < GlobalWatchJournalCooldown)
            {
                continue;
            }

            _globalWatchJournalLastEntryByKey[key] = capturedAt;
            entriesToAdd.Add(CreateGlobalWatchJournalEntry(row, capturedAt, row.HealthBadgeText, row.ProfileReason));
        }

        if (entriesToAdd.Count == 0)
        {
            return;
        }

        var settings = _thresholdSettingsStore.Current;
        var journal = settings.WatchJournal;
        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                WatchJournal = journal with
                {
                    Entries = TrimGlobalWatchJournalEntries(entriesToAdd.Concat(journal.Entries), journal.MaxEntries)
                }
            },
            cancellationToken);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            RefreshGlobalWatchJournalCollection();
            GlobalWatchJournalStatusText = $"{entriesToAdd.Count:N0} watch journal entries added at {capturedAt.ToLocalTime():HH:mm:ss}.";
        });
    }

    private async Task UpdateSuspiciousLaunchLoggingAsync(
        IReadOnlyList<GlobalProcessRowViewModel> rows,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var settings = _thresholdSettingsStore.Current;
        var watchlist = settings.SuspiciousWatchlist;
        if (watchlist.Items.Count == 0)
        {
            _runningSuspiciousPaths.Clear();
            return;
        }

        var suspiciousByPath = watchlist.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.NormalizedPath))
            .ToDictionary(item => item.NormalizedPath, StringComparer.OrdinalIgnoreCase);
        var runningByPath = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.NormalizedFullPath)
                && suspiciousByPath.ContainsKey(row.NormalizedFullPath))
            .GroupBy(row => row.NormalizedFullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.CpuPercent ?? 0).First(), StringComparer.OrdinalIgnoreCase);

        var entriesToAdd = new List<SuspiciousLaunchEntry>();
        foreach (var item in suspiciousByPath.Values)
        {
            if (!runningByPath.TryGetValue(item.NormalizedPath, out var row))
            {
                _runningSuspiciousPaths.Remove(item.NormalizedPath);
                continue;
            }

            if (!_runningSuspiciousPaths.Add(item.NormalizedPath))
            {
                continue;
            }

            entriesToAdd.Add(new SuspiciousLaunchEntry
            {
                Id = $"suspicious_launch_{Guid.NewGuid():N}",
                Timestamp = capturedAt,
                NormalizedPath = item.NormalizedPath,
                ExeName = item.ExeName,
                ProductName = string.IsNullOrWhiteSpace(row.ProductName) ? item.ProductName : row.ProductName,
                CompanyName = string.IsNullOrWhiteSpace(row.CompanyName) ? item.CompanyName : row.CompanyName,
                SignerStatus = string.IsNullOrWhiteSpace(row.SignerStatus) ? item.SignerStatus : row.SignerStatus,
                WatchMode = SelectedGlobalWatchMode,
                ParentProcessId = row.Process.ParentProcessId,
                ParentProcessName = row.Process.ParentProcessName
            });
        }

        if (entriesToAdd.Count == 0)
        {
            return;
        }

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                SuspiciousWatchlist = watchlist with
                {
                    LaunchHistory = entriesToAdd
                        .Concat(watchlist.LaunchHistory)
                        .OrderByDescending(item => item.Timestamp)
                        .Take(watchlist.MaxLaunchHistory)
                        .ToList()
                }
            },
            cancellationToken);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            RefreshSuspiciousWatchCollections();
            SuspiciousWatchStatusText = $"{entriesToAdd.Count:N0} suspicious launch transition{(entriesToAdd.Count == 1 ? string.Empty : "s")} logged.";
        });
    }

    private async Task EnforceProcessBansAsync(
        IReadOnlyList<GlobalProcessRowViewModel> rows,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var settings = _thresholdSettingsStore.Current;
        var processBans = settings.ProcessBans;
        if (processBans.Active.Count == 0)
        {
            _runningBannedPaths.Clear();
            return;
        }

        var active = processBans.Active
            .Where(rule => IsProcessBanActive(rule, capturedAt))
            .GroupBy(rule => rule.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(rule => rule.CreatedAt)
                .First())
            .ToList();
        var changed = active.Count != processBans.Active.Count;
        var activeByPath = active
            .Where(rule => !string.IsNullOrWhiteSpace(rule.NormalizedPath))
            .ToDictionary(rule => rule.NormalizedPath, StringComparer.OrdinalIgnoreCase);
        var runningByPath = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.NormalizedFullPath)
                && activeByPath.ContainsKey(row.NormalizedFullPath))
            .GroupBy(row => row.NormalizedFullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var history = processBans.History.ToList();
        var totalTerminated = 0;

        foreach (var rule in active)
        {
            if (!runningByPath.TryGetValue(rule.NormalizedPath, out var matchingRows))
            {
                _runningBannedPaths.Remove(rule.NormalizedPath);
                continue;
            }

            var processIds = matchingRows
                .Select(row => row.ProcessId)
                .Where(processId => processId > 0)
                .Distinct()
                .ToArray();
            if (processIds.Length == 0)
            {
                continue;
            }

            var result = await _processControlService.KillProcessesAsync(
                processIds,
                "active process ban",
                cancellationToken);
            totalTerminated += result.TerminatedCount;

            if (_runningBannedPaths.Add(rule.NormalizedPath)
                || result.TerminatedCount > 0)
            {
                var representative = matchingRows
                    .OrderByDescending(row => row.CpuPercent ?? 0)
                    .First();
                history.Insert(0, CreateProcessBanEvent(
                    rule,
                    "ban_enforced",
                    representative,
                    result.TerminatedCount,
                    result.Messages.Count == 0
                        ? "Active process ban matched a running process."
                        : string.Join(" ", result.Messages.Take(3))));
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                ProcessBans = processBans with
                {
                    Active = active,
                    History = history
                        .OrderByDescending(entry => entry.Timestamp)
                        .Take(processBans.MaxHistory)
                        .ToList()
                }
            },
            cancellationToken);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            RefreshProcessBanCollections();
            if (totalTerminated > 0)
            {
                ProcessBanStatusText = $"Process bans enforced at {capturedAt.ToLocalTime():HH:mm:ss}: {totalTerminated:N0} process{(totalTerminated == 1 ? string.Empty : "es")} terminated.";
            }
        });
    }

    private async Task UpdateProfileRecommendationsFromGlobalWatchAsync(
        IReadOnlyList<GlobalProcessRowViewModel> rows,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var settings = _thresholdSettingsStore.Current;
        var recommendationSettings = settings.Recommendations;
        var active = recommendationSettings.Active.ToList();
        var history = recommendationSettings.History.ToList();
        var watchJournal = settings.WatchJournal;
        var journalEntries = watchJournal.Entries.ToList();
        var changed = false;
        var window = TimeSpan.FromMinutes(recommendationSettings.TriggerWindowMinutes);
        var cutoff = capturedAt - window;

        foreach (var group in rows
                     .Where(row => row.IsOutOfProfile && !string.IsNullOrWhiteSpace(row.ExeName))
                     .GroupBy(row => row.ExeName, StringComparer.OrdinalIgnoreCase))
        {
            var row = group
                .OrderByDescending(item => item.IsCritical)
                .ThenByDescending(item => item.IsOverLimit)
                .ThenByDescending(item => item.CpuPercent ?? 0)
                .ThenByDescending(item => item.MemoryMb ?? 0)
                .First();
            var suggestedProfile = ResolveNextProfile(row.ProfileId, settings);
            if (suggestedProfile is null || IsRecommendationDenied(recommendationSettings, row.ExeName, suggestedProfile.Id))
            {
                continue;
            }

            if (!_globalWatchOverLimitWarnings.TryGetValue(row.ExeName, out var warnings))
            {
                warnings = [];
                _globalWatchOverLimitWarnings[row.ExeName] = warnings;
            }

            warnings.RemoveAll(timestamp => timestamp < cutoff);
            warnings.Add(capturedAt);

            if (warnings.Count < recommendationSettings.TriggerWarningCount)
            {
                continue;
            }

            var recommendationId = CreateRecommendationId(row.ExeName, row.ProfileId, suggestedProfile.Id);
            var existingIndex = active.FindIndex(item => string.Equals(item.Id, recommendationId, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                var existing = active[existingIndex];
                if ((capturedAt - existing.LastSeen).TotalSeconds >= 30)
                {
                    active[existingIndex] = existing with
                    {
                        WarningCount = Math.Max(existing.WarningCount, warnings.Count),
                        LastSeen = capturedAt,
                        Reason = row.RecommendationReason
                    };
                    changed = true;
                }

                continue;
            }

            active.Add(new ProfileRecommendationRecord
            {
                Id = recommendationId,
                ExeName = row.ExeName,
                CurrentProfileId = row.ProfileId,
                CurrentProfileName = row.ProfileName,
                CurrentProfileSource = row.IsAssignedProfile ? "Assigned profile" : "Global fallback",
                SuggestedProfileId = suggestedProfile.Id,
                SuggestedProfileName = suggestedProfile.Name,
                WarningCount = warnings.Count,
                Reason = row.RecommendationReason,
                FirstSeen = warnings.Min(),
                LastSeen = capturedAt
            });
            journalEntries.Insert(0, CreateGlobalWatchJournalEntry(
                row,
                capturedAt,
                "Recommendation",
                $"{row.ExeName} repeatedly exceeded {row.ProfileSourceText}; suggested {suggestedProfile.Name}.",
                recommendationId));
            history = AddRecommendationHistory(
                history,
                "recommendation_created",
                row.ExeName,
                suggestedProfile.Id,
                suggestedProfile.Name,
                $"{row.ExeName} repeatedly exceeded {row.ProfileSourceText}; suggested {suggestedProfile.Name}.");
            changed = true;
        }

        foreach (var key in _globalWatchOverLimitWarnings.Keys.ToArray())
        {
            _globalWatchOverLimitWarnings[key].RemoveAll(timestamp => timestamp < cutoff);
            if (_globalWatchOverLimitWarnings[key].Count == 0)
            {
                _globalWatchOverLimitWarnings.Remove(key);
            }
        }

        if (!changed)
        {
            return;
        }

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                Recommendations = recommendationSettings with
                {
                    Active = active
                        .OrderByDescending(item => item.LastSeen)
                        .ToList(),
                    History = history
                },
                WatchJournal = watchJournal with
                {
                    Entries = TrimGlobalWatchJournalEntries(journalEntries, watchJournal.MaxEntries)
                }
            },
            cancellationToken);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            RefreshRecommendationCollections();
            RefreshGlobalWatchJournalCollection();
            ProfileRecommendationStatusText = $"{ProfileRecommendations.Count:N0} active profile recommendations.";
        });
    }

    private static ThresholdProfile? ResolveNextProfile(string profileId, CpuRamThresholdSettings settings)
    {
        var nextProfileId = profileId switch
        {
            ThresholdProfileDefaults.LightBackgroundId => ThresholdProfileDefaults.BrowsersChatsId,
            ThresholdProfileDefaults.BrowsersChatsId => ThresholdProfileDefaults.GamesId,
            ThresholdProfileDefaults.GamesId => ThresholdProfileDefaults.HardcoreId,
            _ => null
        };

        return nextProfileId is null
            ? null
            : settings.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, nextProfileId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRecommendationDenied(
        ProfileRecommendationSettings recommendationSettings,
        string exeName,
        string suggestedProfileId) =>
        recommendationSettings.Denied.Any(item => string.Equals(item.ExeName, exeName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.SuggestedProfileId, suggestedProfileId, StringComparison.OrdinalIgnoreCase));

    private static string CreateRecommendationId(string exeName, string currentProfileId, string suggestedProfileId) =>
        $"{NormalizeExeNameForAssignment(exeName)}|{currentProfileId}|{suggestedProfileId}".ToLowerInvariant();

    private void StartSelfMonitoring()
    {
        if (_selfMonitoringCts is not null)
        {
            return;
        }

        _selfMonitoringCts = new CancellationTokenSource();
        _ = RunSelfMonitoringAsync(_selfMonitoringCts.Token);
    }

    private async Task RunSelfMonitoringAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var sample = await _selfMonitoringProvider.SampleSelfAsync(cancellationToken);
                _selfMonitoringSamples.Add(sample);
                if (_selfMonitoringSamples.Count > MaxSelfMonitoringSamples)
                {
                    _selfMonitoringSamples.RemoveRange(0, _selfMonitoringSamples.Count - MaxSelfMonitoringSamples);
                }

                var summary = await _selfMonitoringProvider.SummarizeAsync(_selfMonitoringSamples, cancellationToken);
                await Application.Current.Dispatcher.InvokeAsync(() => UpdateSelfMonitoringUi(sample, summary));
                await Task.Delay(SelfMonitoringIntervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                SelfMonitoringStatusText = $"self-monitoring failed: {error.Message}";
            });
        }
    }

    private void UpdateSelfMonitoringUi(SelfMonitoringSample sample, SelfOverheadSummary summary)
    {
        SelfMonitoringStatusText = "active";
        SelfCurrentCpuText = FormatPercent(sample.CpuPercent);
        SelfAvgCpuText = FormatPercent(summary.AvgCpuPercent);
        SelfPeakCpuText = FormatPercent(summary.MaxCpuPercent);
        SelfCurrentRamText = FormatMb(sample.MemoryMb);
        SelfAvgRamText = FormatMb(summary.AvgMemoryMb);
        SelfPeakRamText = FormatMb(summary.MaxMemoryMb);
        SelfDiskWriteText = FormatMbPerSec(sample.DiskWriteMbPerSec);
        SelfSampleCountText = summary.SampleCount.ToString("N0");
        SelfCpuBudgetStatusText = FormatBudget(summary.AvgCpuPercent, AvgCpuBudgetPercent);
        SelfRamBudgetStatusText = FormatBudget(summary.MaxMemoryMb, PeakRamBudgetMb);
        SelfWritesBudgetStatusText = "configured";
        SelfSnapshotsBudgetStatusText = "event-only";
    }

    private void NotifySummaryProperties()
    {
        OnPropertyChanged(nameof(TargetName));
        OnPropertyChanged(nameof(StartedText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SampleCountText));
        OnPropertyChanged(nameof(TrackedProcessCountText));
        OnPropertyChanged(nameof(EventCountText));
        OnPropertyChanged(nameof(SpikeCountText));
        OnPropertyChanged(nameof(BreachCountText));
        OnPropertyChanged(nameof(HangCountText));
        OnPropertyChanged(nameof(CurrentMonitoringStripText));
        NotifySessionStateProperties();
    }

    private void NotifyTargetSelectionProperties()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(IsRunningProcessMode));
        OnPropertyChanged(nameof(IsAssignedAppsMode));
        OnPropertyChanged(nameof(IsExecutableMode));
        OnPropertyChanged(nameof(TargetReadinessText));
        OnPropertyChanged(nameof(ActiveThresholdSourceText));
        NotifyLiveConfigProperties();
        NotifySessionStateProperties();
    }

    private void NotifySessionStateProperties()
    {
        OnPropertyChanged(nameof(HasCrossScreenMonitoringStrip));
        OnPropertyChanged(nameof(CrossScreenMonitoringStripText));
        OnPropertyChanged(nameof(LiveSessionStateText));
        OnPropertyChanged(nameof(LiveSessionStateTone));
        OnPropertyChanged(nameof(LiveSessionTargetText));
        OnPropertyChanged(nameof(LiveSessionMetaText));
        OnPropertyChanged(nameof(LiveSessionHintText));
        OnPropertyChanged(nameof(HasLastCompletedSession));
        OnPropertyChanged(nameof(LastCompletedSessionText));
        OnPropertyChanged(nameof(LastCompletedSessionHint));
    }

    private void NotifyLiveConfigProperties()
    {
        OnPropertyChanged(nameof(LiveConfigTargetText));
        OnPropertyChanged(nameof(LiveConfigCollectorsText));
        OnPropertyChanged(nameof(LiveConfigSessionProfileText));
        OnPropertyChanged(nameof(LiveConfigSamplingText));
        OnPropertyChanged(nameof(LiveConfigChildScopeText));
        OnPropertyChanged(nameof(LiveConfigMonitorText));
        OnPropertyChanged(nameof(LiveSnapshotScopeText));
    }

    private void NotifyLivePanelProperties()
    {
        OnPropertyChanged(nameof(HasActiveLiveMetrics));
        OnPropertyChanged(nameof(ShowLiveSnapshotEmptyState));
        OnPropertyChanged(nameof(LiveSnapshotTitle));
        OnPropertyChanged(nameof(LiveSnapshotStatusText));
        OnPropertyChanged(nameof(LiveSnapshotEmptyText));
        OnPropertyChanged(nameof(LiveSnapshotScopeText));
        OnPropertyChanged(nameof(HasLiveEvents));
        OnPropertyChanged(nameof(ShowLiveEventEmptyState));
        OnPropertyChanged(nameof(LiveEventEmptyText));
    }

    private void SetLiveSessionState(LiveSessionUiState state)
    {
        if (_liveSessionState == state)
        {
            return;
        }

        _liveSessionState = state;
        NotifySessionStateProperties();
        NotifySummaryProperties();
    }

    private void MarkReadyForConfigurationChange()
    {
        NotifyLiveConfigProperties();
        if (IsRecording || _liveSessionState == LiveSessionUiState.ChangesPending)
        {
            return;
        }

        SetLiveSessionState(LiveSessionUiState.ReadyToStart);
    }

    private void UpdateLastCompletedSession(SessionRecord session)
    {
        var item = new SessionListItemViewModel(session);
        _lastCompletedSessionText = $"{item.AppName} - {item.StartedFullText} - {item.DurationText} - {item.StatusText}";
        _lastCompletedSessionHint = item.ExitText;
        OnPropertyChanged(nameof(HasLastCompletedSession));
        OnPropertyChanged(nameof(LastCompletedSessionText));
        OnPropertyChanged(nameof(LastCompletedSessionHint));
    }

    private static LiveSessionUiState ResolveCompletedLiveState(
        SessionRecord session,
        bool wasAutoStopped,
        bool wasManualStop)
    {
        if (wasAutoStopped)
        {
            return LiveSessionUiState.ChangesPending;
        }

        if (session.Status is SessionStatus.UnexpectedExit or SessionStatus.CrashLikeExit)
        {
            return LiveSessionUiState.EndedUnexpectedly;
        }

        if (wasManualStop || session.Summary.ExitKind == SessionExitKind.NormalStop)
        {
            return LiveSessionUiState.StoppedManually;
        }

        return LiveSessionUiState.Stopped;
    }

    private void NotifySelectedGlobalProcessProperties()
    {
        OnPropertyChanged(nameof(SelectedGlobalProcessPanelTitle));
        OnPropertyChanged(nameof(SelectedGlobalProcessTitle));
        OnPropertyChanged(nameof(SelectedGlobalProcessHint));
        OnPropertyChanged(nameof(SelectedGlobalProcessExeText));
        OnPropertyChanged(nameof(SelectedGlobalProcessPathText));
        OnPropertyChanged(nameof(SelectedGlobalProcessProductText));
        OnPropertyChanged(nameof(SelectedGlobalProcessDescriptionText));
        OnPropertyChanged(nameof(SelectedGlobalProcessCompanyText));
        OnPropertyChanged(nameof(SelectedGlobalProcessSignerText));
        OnPropertyChanged(nameof(SelectedGlobalProcessVersionText));
        OnPropertyChanged(nameof(SelectedGlobalProcessOriginalFileText));
        OnPropertyChanged(nameof(SelectedGlobalProcessParentText));
        OnPropertyChanged(nameof(SelectedGlobalProcessDescendantsText));
        OnPropertyChanged(nameof(SelectedGlobalProcessPidText));
        OnPropertyChanged(nameof(SelectedGlobalProcessCpuText));
        OnPropertyChanged(nameof(SelectedGlobalProcessRamText));
        OnPropertyChanged(nameof(SelectedGlobalProcessDiskText));
        OnPropertyChanged(nameof(SelectedGlobalProcessStatusText));
        OnPropertyChanged(nameof(SelectedGlobalProcessProfileText));
        OnPropertyChanged(nameof(SelectedGlobalProcessHealthText));
        OnPropertyChanged(nameof(SelectedGlobalProcessReasonText));
        OnPropertyChanged(nameof(SelectedGlobalProcessIncludedText));
        OnPropertyChanged(nameof(SelectedGlobalProcessWhatItDoesText));
        OnPropertyChanged(nameof(SelectedGlobalProcessInspectorModeText));
        OnPropertyChanged(nameof(SelectedGlobalProcessRelationText));
        OnPropertyChanged(nameof(SelectedGlobalProcessAppearsBecauseText));
        OnPropertyChanged(nameof(SelectedGlobalProcessCommandLineText));
        OnPropertyChanged(nameof(SelectedGlobalProcessSuspiciousLastLaunchText));
        OnPropertyChanged(nameof(CanInspectSelectedGlobalProcess));
        OnPropertyChanged(nameof(CanOpenSelectedGlobalProcessFileLocation));
        OnPropertyChanged(nameof(CanCopySelectedGlobalProcessPath));
        OnPropertyChanged(nameof(CanKillSelectedGlobalProcess));
        OnPropertyChanged(nameof(CanBanSelectedGlobalProcess));
        OnPropertyChanged(nameof(SelectedGlobalProcessKillTreeLabel));
        OnPropertyChanged(nameof(SelectedGlobalProcessBanText));
        OnPropertyChanged(nameof(CanAssignSelectedGlobalProcessProfile));
        OnPropertyChanged(nameof(CanMarkSelectedGlobalProcessSuspicious));
        OnPropertyChanged(nameof(CanRemoveSelectedGlobalProcessSuspicious));
        OnPropertyChanged(nameof(IsSelectedGlobalProcessSuspicious));
        OnPropertyChanged(nameof(SelectedGlobalProcessSuspiciousText));
        OnPropertyChanged(nameof(SelectedGlobalProcessHasRecommendation));
        OnPropertyChanged(nameof(SelectedGlobalProcessRecommendationText));
    }

    private void NotifyInspectorTargetProperties()
    {
        OnPropertyChanged(nameof(HasInspectorTarget));
        OnPropertyChanged(nameof(InspectorTitle));
        OnPropertyChanged(nameof(InspectorModeText));
        OnPropertyChanged(nameof(InspectorSourceText));
        OnPropertyChanged(nameof(InspectorExeText));
        OnPropertyChanged(nameof(InspectorPathText));
        OnPropertyChanged(nameof(InspectorProductText));
        OnPropertyChanged(nameof(InspectorDescriptionText));
        OnPropertyChanged(nameof(InspectorCompanyText));
        OnPropertyChanged(nameof(InspectorSignerText));
        OnPropertyChanged(nameof(InspectorVersionText));
        OnPropertyChanged(nameof(InspectorOriginalFileText));
        OnPropertyChanged(nameof(InspectorParentText));
        OnPropertyChanged(nameof(InspectorDescendantsText));
        OnPropertyChanged(nameof(InspectorPidText));
        OnPropertyChanged(nameof(InspectorCpuText));
        OnPropertyChanged(nameof(InspectorRamText));
        OnPropertyChanged(nameof(InspectorDiskText));
        OnPropertyChanged(nameof(InspectorStatusText));
        OnPropertyChanged(nameof(InspectorProfileText));
        OnPropertyChanged(nameof(InspectorHealthText));
        OnPropertyChanged(nameof(InspectorReasonText));
        OnPropertyChanged(nameof(InspectorIncludedText));
        OnPropertyChanged(nameof(InspectorRelationText));
        OnPropertyChanged(nameof(InspectorAppearsBecauseText));
        OnPropertyChanged(nameof(InspectorCommandLineText));
        OnPropertyChanged(nameof(InspectorIsGroup));
        OnPropertyChanged(nameof(InspectorIncludedProcessRows));
        OnPropertyChanged(nameof(InspectorWhatItDoesText));
        OnPropertyChanged(nameof(InspectorSuspiciousLastLaunchText));
        OnPropertyChanged(nameof(CanMonitorInspectorTarget));
        OnPropertyChanged(nameof(CanOpenInspectorFileLocation));
        OnPropertyChanged(nameof(CanCopyInspectorPath));
        OnPropertyChanged(nameof(CanKillInspectorTarget));
        OnPropertyChanged(nameof(CanBanInspectorTarget));
        OnPropertyChanged(nameof(CanMarkInspectorSuspicious));
        OnPropertyChanged(nameof(CanRemoveInspectorSuspicious));
        OnPropertyChanged(nameof(IsInspectorTargetSuspicious));
        OnPropertyChanged(nameof(InspectorKillTreeLabel));
        OnPropertyChanged(nameof(InspectorSuspiciousText));
        OnPropertyChanged(nameof(InspectorBanText));
        OnPropertyChanged(nameof(InspectorHasRecommendation));
        OnPropertyChanged(nameof(InspectorRecommendationText));
    }

    private void NotifySessionDetailProperties()
    {
        OnPropertyChanged(nameof(HasSessionDetails));
        OnPropertyChanged(nameof(HasSessionDetailEvents));
        OnPropertyChanged(nameof(HasSessionDetailMetrics));
        OnPropertyChanged(nameof(SessionDetailTitle));
        OnPropertyChanged(nameof(SessionDetailSubtitle));
    }

    private void StopForParameterChange(string reason)
    {
        if (!IsRecording || _activeHandle is null || _autoStopInProgress)
        {
            return;
        }

        _autoStopInProgress = true;
        _manualStopInProgress = false;
        _autoStopStatusText = "Monitoring stopped. Press Start to apply changes.";
        RecordingStatusText = _autoStopStatusText;
        LiveWarningText = _autoStopStatusText;
        LiveSnapshotText = $"stopping: {reason}";
        SetLiveSessionState(LiveSessionUiState.ChangesPending);
        _ = StopAfterParameterChangeAsync(_activeHandle);
    }

    private void StopForCaptureChange(string reason)
    {
        if (!IsRecording || _activeHandle is null || _autoStopInProgress)
        {
            return;
        }

        _autoStopInProgress = true;
        _manualStopInProgress = false;
        _autoStopStatusText = "Monitoring stopped. Press Start to apply capture changes.";
        RecordingStatusText = _autoStopStatusText;
        LiveWarningText = _autoStopStatusText;
        LiveSnapshotText = $"stopping: {reason}";
        SetLiveSessionState(LiveSessionUiState.ChangesPending);
        _ = StopAfterParameterChangeAsync(_activeHandle);
    }

    private static async Task StopAfterParameterChangeAsync(IRunningSessionHandle handle)
    {
        try
        {
            await handle.StopAsync();
        }
        catch
        {
        }
    }

    private async Task<TargetDescriptor?> ResolveSelectedRecordingTargetAsync(CancellationToken cancellationToken)
    {
        return SelectedTargetMode switch
        {
            TargetModeExecutable => await _targetResolver.ResolveExecutableAsync(
                SelectedExecutablePath,
                IncludeChildProcesses,
                cancellationToken),
            TargetModeAssignedApps when SelectedAssignedTarget?.RunningProcessId is int processId => await _targetResolver.ResolveProcessAsync(
                processId,
                IncludeChildProcesses,
                cancellationToken),
            TargetModeRunningProcess when SelectedRunningTarget is not null => await _targetResolver.ResolveProcessAsync(
                SelectedRunningTarget.ProcessId,
                IncludeChildProcesses,
                cancellationToken),
            _ => null
        };
    }

    private SamplingSettings CreateSamplingSettings(TargetDescriptor target)
    {
        var exeName = GetExeNameFromTarget(target);
        var assignedProfile = ResolveAssignedProfile(exeName);
        var manualSessionProfile = SelectedSessionProfileOption?.IsAuto == false
            ? _thresholdSettingsStore.Current.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, SelectedSessionProfileOption.ProfileId, StringComparison.OrdinalIgnoreCase))
            : null;
        var sourceLabel = ResolveActiveThresholdSourceText(target);
        var sampling = new SamplingSettings
        {
            IntervalMs = SelectedSamplingOption.IntervalMs,
            LiveUiRefreshMs = 2000,
            StorageBatchSize = 10,
            CaptureCpu = CaptureCpu,
            CaptureRam = CaptureRam,
            CaptureDiskRead = CaptureDiskRead,
            CaptureDiskWrite = CaptureDiskWrite,
            SessionProfileMode = manualSessionProfile is not null
                ? "Manual"
                : assignedProfile is not null ? "ProcessAssignment" : "Auto",
            ThresholdSourceLabel = sourceLabel
        };

        if (manualSessionProfile is not null)
        {
            sampling = sampling with
            {
                SessionProfileId = manualSessionProfile.Id,
                SessionProfileName = manualSessionProfile.Name,
                SessionThresholds = manualSessionProfile.Limits
            };
        }
        else if (assignedProfile is not null)
        {
            sampling = sampling with
            {
                SessionProfileId = assignedProfile.Id,
                SessionProfileName = assignedProfile.Name,
                SessionThresholds = assignedProfile.Limits
            };
        }

        return sampling;
    }

    private async Task<TargetDescriptor?> ResolveSelectedAttachTargetAsync(CancellationToken cancellationToken)
    {
        return SelectedTargetMode switch
        {
            TargetModeAssignedApps when SelectedAssignedTarget?.RunningProcessId is int processId => await _targetResolver.ResolveProcessAsync(
                processId,
                IncludeChildProcesses,
                cancellationToken),
            TargetModeRunningProcess when SelectedRunningTarget is not null => await _targetResolver.ResolveProcessAsync(
                SelectedRunningTarget.ProcessId,
                IncludeChildProcesses,
                cancellationToken),
            _ => null
        };
    }

    private string ResolveActiveThresholdSourceText()
    {
        if (IsRecording && !string.IsNullOrWhiteSpace(_activeSessionProfileText))
        {
            return _activeSessionProfileText;
        }

        return ResolveActiveThresholdSourceText(GetSelectedTargetDescriptorForThresholdSource());
    }

    private string ResolveActiveThresholdSourceText(TargetDescriptor? target)
    {
        var exeName = target is null ? GetSelectedTargetExeNameForThresholdSource() : GetExeNameFromTarget(target);
        if (SelectedSessionProfileOption?.IsAuto == false)
        {
            return $"Session profile: {SelectedSessionProfileOption.ProfileName}";
        }

        var assignedProfile = ResolveAssignedProfile(exeName);
        if (assignedProfile is not null)
        {
            return $"Session profile: Auto -> {assignedProfile.Name}";
        }

        return "Session profile: Auto -> Global fallback";
    }

    private TargetDescriptor? GetSelectedTargetDescriptorForThresholdSource() => SelectedTargetMode switch
    {
        TargetModeAssignedApps => null,
        TargetModeExecutable => string.IsNullOrWhiteSpace(SelectedExecutablePath)
            ? null
            : new TargetDescriptor
            {
                Id = "selected_executable",
                DisplayName = Path.GetFileName(SelectedExecutablePath),
                ExecutablePath = SelectedExecutablePath,
                Kind = TargetSelectionKind.Executable,
                LifecycleMode = TargetLifecycleMode.LaunchAndTrack
            },
        _ => SelectedRunningTarget?.Target
    };

    private string? GetSelectedTargetExeNameForThresholdSource() => SelectedTargetMode switch
    {
        TargetModeAssignedApps => SelectedAssignedTarget?.ExeName,
        TargetModeExecutable => Path.GetFileName(SelectedExecutablePath),
        _ => SelectedRunningTarget is null ? null : GetExeNameFromTarget(SelectedRunningTarget.Target)
    };

    private ThresholdProfile? ResolveAssignedProfile(string? exeName)
    {
        var normalizedExeName = NormalizeExeNameForAssignment(exeName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(normalizedExeName)
            && _thresholdSettingsStore.Current.AppProfileAssignments.TryGetValue(normalizedExeName, out var profileId)
            && _thresholdSettingsStore.Current.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)) is { } profile)
        {
            return profile;
        }

        return null;
    }

    private static MetricCapabilities CreateLiveCapabilities(SamplingSettings sampling) => new()
    {
        CpuPercent = sampling.CaptureCpu ? MetricReliability.Stable : MetricReliability.Unavailable,
        MemoryMb = sampling.CaptureRam ? MetricReliability.Stable : MetricReliability.Unavailable,
        DiskReadMbPerSec = sampling.CaptureDiskRead ? MetricReliability.BestEffort : MetricReliability.Unavailable,
        DiskWriteMbPerSec = sampling.CaptureDiskWrite ? MetricReliability.BestEffort : MetricReliability.Unavailable,
        GpuPercent = MetricReliability.Unavailable,
        TemperatureC = MetricReliability.Unavailable
    };

    private string FormatThresholdSource(string? exeName)
    {
        return ResolveActiveThresholdSourceText(new TargetDescriptor
        {
            Id = "selected_exe",
            DisplayName = exeName ?? string.Empty,
            Kind = TargetSelectionKind.Process,
            LifecycleMode = TargetLifecycleMode.AttachToRunning
        });
    }

    private static bool SameSessionProfile(SessionProfileOptionViewModel? left, SessionProfileOptionViewModel? right) =>
        left?.IsAuto == right?.IsAuto
        && string.Equals(left?.ProfileId, right?.ProfileId, StringComparison.OrdinalIgnoreCase);

    private static bool SameAssignedTarget(AssignedTargetOptionViewModel? left, AssignedTargetOptionViewModel? right) =>
        string.Equals(left?.ExeName, right?.ExeName, StringComparison.OrdinalIgnoreCase)
        && left?.RunningProcessId == right?.RunningProcessId
        && string.Equals(left?.ProfileId, right?.ProfileId, StringComparison.OrdinalIgnoreCase);

    private static bool HasEventKind(PerformanceEvent performanceEvent, EventKind kind) =>
        performanceEvent.Kind == kind || performanceEvent.GroupedKinds.Contains(kind);

    private static string FormatDuration(TimeSpan duration) =>
        $"{(int)duration.TotalMinutes}m {duration.Seconds:00}s";

    private static string FormatPercent(double? value) =>
        value is null ? "n/a" : $"{value.Value:N2}%";

    private static string FormatMb(double? value) =>
        value is null ? "n/a" : $"{value.Value:N1} MB";

    private static string FormatMbPerSec(double? value) =>
        value is null ? "n/a" : $"{value.Value:N2} MB/s";

    private static string FormatBudget(double? value, double budget) =>
        value is null
            ? "unknown"
            : value.Value <= budget
                ? "within budget"
                : "above budget";

    private bool TryReadGlobalThresholds(out ThresholdLimitValues limits) =>
        TryReadThresholds(
            CpuThresholdPercentText,
            RamThresholdMbText,
            DiskReadThresholdMbPerSecText,
            DiskWriteThresholdMbPerSecText,
            out limits);

    private CaptureSettings ReadCaptureSettingsFromUi() => new()
    {
        CaptureCpu = CaptureCpu,
        CaptureRam = CaptureRam,
        CaptureDiskRead = CaptureDiskRead,
        CaptureDiskWrite = CaptureDiskWrite
    };

    private bool TryReadProfileThresholds(out ThresholdLimitValues limits) =>
        TryReadThresholds(
            ProfileCpuThresholdPercentText,
            ProfileRamThresholdMbText,
            ProfileDiskReadThresholdMbPerSecText,
            ProfileDiskWriteThresholdMbPerSecText,
            out limits);

    private bool TryReadAntiNoiseSettings(out AntiNoiseSettings antiNoise)
    {
        antiNoise = new AntiNoiseSettings();
        if (!TryParseWholeSeconds(StartupGraceSecondsText, out var startupGraceSeconds)
            || !TryParseWholeSeconds(EventCooldownSecondsText, out var eventCooldownSeconds)
            || !TryParseWholeSeconds(GroupingWindowSecondsText, out var groupingWindowSeconds)
            || !TryParseWholeSeconds(SnapshotSuppressionSecondsText, out var snapshotSuppressionSeconds))
        {
            return false;
        }

        antiNoise = new AntiNoiseSettings
        {
            StartupGraceSeconds = Math.Clamp(startupGraceSeconds, 0, 300),
            EventCooldownSeconds = Math.Clamp(eventCooldownSeconds, 0, 3600),
            GroupingWindowSeconds = Math.Clamp(groupingWindowSeconds, 0, 60),
            SnapshotSuppressionSeconds = Math.Clamp(snapshotSuppressionSeconds, 0, 60)
        };
        return true;
    }

    private static bool TryReadThresholds(
        string cpuText,
        string ramText,
        string diskReadText,
        string diskWriteText,
        out ThresholdLimitValues limits)
    {
        limits = new ThresholdLimitValues();
        if (!TryParsePositive(cpuText, out var cpuThreshold)
            || !TryParsePositive(ramText, out var ramThreshold)
            || !TryParsePositive(diskReadText, out var diskReadThreshold)
            || !TryParsePositive(diskWriteText, out var diskWriteThreshold))
        {
            return false;
        }

        limits = new ThresholdLimitValues
        {
            CpuThresholdPercent = Math.Clamp(cpuThreshold, 1, 100),
            RamThresholdMb = Math.Clamp(ramThreshold, 1, 1_048_576),
            DiskReadThresholdMbPerSec = Math.Clamp(diskReadThreshold, 0.1, 1_048_576),
            DiskWriteThresholdMbPerSec = Math.Clamp(diskWriteThreshold, 0.1, 1_048_576)
        };
        return true;
    }

    private async Task<string> ResolveCurrentTargetExeNameAsync(CancellationToken cancellationToken)
    {
        if (_activeHandle?.Target is not null)
        {
            return GetExeNameFromTarget(_activeHandle.Target);
        }

        if (SelectedTargetMode == TargetModeExecutable)
        {
            return Path.GetFileName(SelectedExecutablePath);
        }

        if (SelectedTargetMode == TargetModeAssignedApps && SelectedAssignedTarget is not null)
        {
            return SelectedAssignedTarget.ExeName;
        }

        if (SelectedRunningTarget is null)
        {
            return string.Empty;
        }

        var target = await _targetResolver.ResolveProcessAsync(
            SelectedRunningTarget.ProcessId,
            IncludeChildProcesses,
            cancellationToken);

        return GetExeNameFromTarget(target);
    }

    private static string GetExeNameFromTarget(TargetDescriptor target)
    {
        if (!string.IsNullOrWhiteSpace(target.ExecutablePath))
        {
            return Path.GetFileName(target.ExecutablePath);
        }

        var displayName = target.DisplayName;
        var pidMarkerIndex = displayName.IndexOf(" (", StringComparison.Ordinal);
        if (pidMarkerIndex > 0)
        {
            displayName = displayName[..pidMarkerIndex];
        }

        return displayName;
    }

    private static string NormalizeExeNameForAssignment(string exeName)
    {
        exeName = Path.GetFileName(exeName.Trim());
        if (string.IsNullOrWhiteSpace(exeName))
        {
            return string.Empty;
        }

        return exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exeName.ToLowerInvariant()
            : $"{exeName}.exe".ToLowerInvariant();
    }

    private static bool TryParseWholeSeconds(string text, out int seconds)
    {
        seconds = 0;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out seconds)
            || int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
    }

    private static bool TryParsePositive(string text, out double value)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        return value > 0;
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

    private static IEnumerable<SessionDetailFactViewModel> CreateSessionDetailFacts(SessionRecord session)
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

    private static string FormatSessionStatus(SessionStatus status) => status switch
    {
        SessionStatus.Completed => "Completed",
        SessionStatus.Running => "Recording",
        SessionStatus.Stopped => "Stopped by user",
        SessionStatus.ExternalExit => "Closed externally",
        SessionStatus.UnexpectedExit => "Unexpected exit",
        SessionStatus.CrashLikeExit => "Crash-like exit",
        SessionStatus.Planned => "Not recorded",
        _ => status.ToString()
    };

    private static string FormatStability(SessionSummary summary) =>
        string.IsNullOrWhiteSpace(summary.StabilityReason)
            ? summary.StabilityStatus.ToString()
            : $"{summary.StabilityStatus}: {summary.StabilityReason}";

    private static string FormatExit(SessionSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.ExitReason))
        {
            return summary.ExitReason;
        }

        return summary.ExitKind switch
        {
            SessionExitKind.NormalStop => "Normal stop in utility",
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

internal static class ObservableCollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }
}
