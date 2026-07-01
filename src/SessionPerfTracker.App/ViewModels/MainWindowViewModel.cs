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

public sealed class DestructiveProcessActionConfirmationEventArgs : EventArgs
{
    public DestructiveProcessActionConfirmationEventArgs(
        string title,
        string message)
    {
        Title = title;
        Message = message;
    }

    public string Title { get; }
    public string Message { get; }
    public bool IsConfirmed { get; set; }
}

public sealed partial class MainWindowViewModel : ObservableObject
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
    private const int SettingsTabIndex = 5;
    private const int SettingsFeedbackTabIndex = 3;
    private const int GlobalWatchOverviewSectionIndex = 0;
    private const int GlobalWatchJournalSectionIndex = 1;
    private const int GlobalWatchRecommendationsSectionIndex = 2;
    private const int GlobalWatchSuspiciousSectionIndex = 3;
    private const int GlobalWatchJournalUiLimit = 120;
    private static readonly TimeSpan GlobalWatchJournalCooldown = TimeSpan.FromSeconds(60);
    private string TargetModeRunningProcess => GetText("Ui_RunningProcess");
    private string TargetModeAssignedApps => GetText("Ui_AssignedAppsProfiles");
    private string TargetModeExecutable => GetText("Ui_ManualExecutableLaunch");
    private string GlobalSortCpu => "CPU";
    private string GlobalSortRam => "RAM";
    private string GlobalSortDisk => GetText("Ui_Disk");
    private string GlobalSortName => GetText("Ui_Process");
    private string GlobalSortPid => GetText("Ui_PidOrCount");
    private string GlobalSortProfile => GetText("Ui_Profile");
    private string GlobalSortHealth => GetText("Ui_Health");
    private string GlobalModeApplications => GetText("Ui_AssignedAppsProfiles");
    private string GlobalModeProcesses => GetText("Ui_IncludedProcesses");

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
    private string GetText(string key) => Application.Current.TryFindResource(key) as string ?? key;
    private string FormatText(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, GetText(key), args);
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
    private LanguageOptionViewModel? _selectedLanguageOption;
    private TargetOptionViewModel? _selectedRunningTarget;
    private AssignedTargetOptionViewModel? _selectedAssignedTarget;
    private GlobalProcessRowViewModel? _selectedGlobalProcess;
    private ProcessInspectorTargetViewModel? _processInspectorTarget;
    private SamplingOptionViewModel _selectedSamplingOption;
    private readonly List<TargetOptionViewModel> _allRunningTargets = [];
    private readonly List<SessionListItemViewModel> _allSessionItems = [];
    private string _storagePath = string.Empty;
    private string _selectedExecutablePath = string.Empty;
    private string _selectedTargetMode = string.Empty;
    private string _processFilterText = string.Empty;
    private string _sessionSearchText = string.Empty;
    private string _recordingStatusText = "waiting";
    private string _storageStatusText = "storage ready";
    private string _liveSnapshotText = "waiting";
    private string _ramDiagnosticText = "no snapshot";
    private string _systemContextText = "no snapshot";
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
    private string _thresholdSettingsStatusText = "loaded";
    private string _exportStatusText = "exports saved to localappdata";
    private string _exportDirectoryText = string.Empty;
    private string _updateManifestUrlText = string.Empty;
    private string _updateStatusText = "update check manual";
    private string _updateLatestVersionText = "not checked";
    private string _updateReleaseNotesText = "no updates checked yet";
    private string _downloadedUpdateInstallerPath = string.Empty;
    private string _bugReportText = string.Empty;
    private string _featureFeedbackText = string.Empty;
    private string _feedbackStatusText = "No feedback saved yet.";
    private bool _includeLatestSessionInFeedback = true;
    private bool _includeScreenshotInFeedback;
    private string _liveAssignmentStatusText = "select target";
    private string _liveWarningText = string.Empty;
    private string _selfMonitoringStatusText = "warmup";
    private string _selfCurrentCpuText = "n/a";
    private string _selfAvgCpuText = "n/a";
    private string _selfPeakCpuText = "n/a";
    private string _selfCurrentRamText = "n/a";
    private string _selfAvgRamText = "n/a";
    private string _selfPeakRamText = "n/a";
    private string _selfDiskWriteText = "n/a";
    private string _selfSampleCountText = "0";
    private string _selfCpuBudgetStatusText = string.Empty;
    private string _selfRamBudgetStatusText = string.Empty;
    private string _selfWritesBudgetStatusText = string.Empty;
    private string _selfSnapshotsBudgetStatusText = string.Empty;
    private string _globalWatchFilterText = string.Empty;
    private string _globalWatchStatusText = string.Empty;
    private string _globalWatchLastScanText = string.Empty;
    private string _globalWatchJournalStatusText = string.Empty;
    private string _profileRecommendationStatusText = string.Empty;
    private string _suspiciousWatchStatusText = string.Empty;
    private string _processBanStatusText = string.Empty;
    private string _languageSettingsStatusText = string.Empty;
    private string _selectedGlobalWatchSortMode = string.Empty;
    private string _selectedGlobalWatchMode = string.Empty;
    private string? _selectedGlobalProcessKey;
    private bool _globalWatchSortDescending;
    private string _activeTargetName = string.Empty;
    private string _activeSessionProfileText = string.Empty;
    private string _lastCompletedSessionText = string.Empty;
    private string _lastCompletedSessionHint = string.Empty;
    private int _selectedTabIndex;
    private int _selectedSettingsTabIndex;
    private int _selectedGlobalWatchSectionIndex;
    private DateTimeOffset? _activeStartedAt;
    private int _liveSampleCount;
    private int _liveEventCount;
    private int _liveSpikeCount;
    private int _liveBreachCount;
    private int _liveHangCount;
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
    private bool _automaticallyInstallUpdatesOnStartup = true;
    private bool _isCheckingForUpdates;
    private bool _isUpdateAvailable;
    private bool _isUpdateRestartRequested;
    private bool _minimizeToTrayOnClose = true;
    private bool _startWithWindows = true;
    private bool _startMinimizedToTray = true;
    private bool _trustExplainerDismissed;
    private bool _globalWatchOnlyOverLimit;
    private bool _globalWatchOnlyCritical;
    private bool _globalWatchOnlyUnassigned;
    private bool _globalWatchOnlyNearLimit;
    private bool _autoStopInProgress;
    private bool _manualStopInProgress;
    private LiveSessionUiState _liveSessionState = LiveSessionUiState.ReadyToStart;
    private string _autoStopStatusText = string.Empty;
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
    public event EventHandler<DestructiveProcessActionConfirmationEventArgs>? DestructiveProcessActionConfirmationRequested;
    public event EventHandler? UpdateInstallerLaunched;
    public event EventHandler? LanguageRestartRequested;

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
        InitializeLocalizedDefaults();
        SamplingOptions =
        [
            new SamplingOptionViewModel(1000, $"1000 ms ({GetText("Ui_Recommended")})"),
            new SamplingOptionViewModel(500)
        ];
        RetentionOptions =
        [
            new RetentionOptionViewModel(1, $"1 {GetText("Ui_Day")}"),
            new RetentionOptionViewModel(7, $"7 {GetText("Ui_Days")}"),
            new RetentionOptionViewModel(30, $"30 {GetText("Ui_Days")}"),
            new RetentionOptionViewModel(90, $"90 {GetText("Ui_Days")}"),
            new RetentionOptionViewModel(null, GetText("Ui_RetentionForever"))
        ];
        _selectedSamplingOption = SamplingOptions[0];
        ProcessBanDurationOptions =
        [
            new ProcessBanDurationOptionViewModel($"5 {GetText("Ui_Seconds")}", TimeSpan.FromSeconds(5)),
            new ProcessBanDurationOptionViewModel($"30 {GetText("Ui_Seconds")}", TimeSpan.FromSeconds(30)),
            new ProcessBanDurationOptionViewModel($"1 {GetText("Ui_Minute")}", TimeSpan.FromMinutes(1)),
            new ProcessBanDurationOptionViewModel(GetText("Ui_Forever"), null)
        ];
        _selectedProcessBanDurationOption = ProcessBanDurationOptions[1];
        LanguageOptions =
        [
            new LanguageOptionViewModel(LocalizationManager.Russian, GetText("Ui_LanguageRussian")),
            new LanguageOptionViewModel(LocalizationManager.English, GetText("Ui_LanguageEnglish"))
        ];
        _selectedLanguageOption = LanguageOptions[0];
    }

    private void InitializeLocalizedDefaults()
    {
        TargetModes = [TargetModeRunningProcess, TargetModeAssignedApps, TargetModeExecutable];
        GlobalWatchSortModes = [GlobalSortCpu, GlobalSortRam, GlobalSortDisk, GlobalSortName, GlobalSortPid, GlobalSortProfile, GlobalSortHealth];
        GlobalWatchModes = [GlobalModeApplications, GlobalModeProcesses];
        _selectedTargetMode = TargetModeRunningProcess;
        _selectedGlobalWatchSortMode = GlobalSortName;
        _selectedGlobalWatchMode = GlobalModeApplications;

        _recordingStatusText = GetText("Ui_Waiting");
        _storageStatusText = GetText("Ui_StorageReady");
        _liveSnapshotText = GetText("Ui_Waiting");
        _ramDiagnosticText = GetText("Ui_NoSnapshot");
        _systemContextText = GetText("Ui_NoSnapshot");
        _thresholdSettingsStatusText = GetText("Ui_ThresholdsLoaded");
        _exportStatusText = GetText("Ui_ExportStatusDefault");
        _updateStatusText = GetText("Ui_UpdateStatusDefault");
        _updateLatestVersionText = GetText("Ui_NotChecked");
        _updateReleaseNotesText = GetText("Ui_NoUpdateInfo");
        _liveAssignmentStatusText = GetText("Ui_LiveAssignmentStatusDefault");
        _selfMonitoringStatusText = GetText("Ui_Warmup");
        _globalWatchStatusText = GetText("Ui_GlobalWatchOverviewHint");
        _globalWatchLastScanText = GetText("Ui_WaitingFirstScan");
        _globalWatchJournalStatusText = GetText("Ui_JournalWritingStatus");
        _profileRecommendationStatusText = GetText("Ui_RecommendationsHintStatus");
        _suspiciousWatchStatusText = GetText("Ui_SuspiciousWatchStatusDefault");
        _processBanStatusText = GetText("Ui_ProcessBanStatusDefault");
        _languageSettingsStatusText = GetText("Ui_LanguageSettingsLoaded");
        _selfCpuBudgetStatusText = GetText("Ui_StatusUnknown");
        _selfRamBudgetStatusText = GetText("Ui_StatusUnknown");
        _selfWritesBudgetStatusText = GetText("Ui_StatusConfigured");
        _selfSnapshotsBudgetStatusText = GetText("Ui_OnlyOnEvent");
    }

    public ObservableCollection<SessionListItemViewModel> Sessions { get; } = [];
    public ObservableCollection<TargetOptionViewModel> RunningTargets { get; } = [];
    public ObservableCollection<MetricValueViewModel> CurrentMetrics { get; } = [];
    public ObservableCollection<MetricSummaryRowViewModel> SelectedMetricSummaries { get; } = [];
    public ObservableCollection<EventRowViewModel> SelectedEvents { get; } = [];
    public ObservableCollection<SessionDetailFactViewModel> SessionDetailFacts { get; } = [];
    public ObservableCollection<SessionRecommendationViewModel> SessionDetailRecommendations { get; } = [];
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
    public ObservableCollection<FeedbackDeliveryHistoryItemViewModel> FeedbackDeliveryHistory { get; } = [];
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
    public ObservableCollection<LanguageOptionViewModel> LanguageOptions { get; }
    public ObservableCollection<SessionProfileFilterOptionViewModel> SessionProfileFilters { get; } = [];
    public IReadOnlyList<string> TargetModes { get; private set; } = [];
    public IReadOnlyList<string> GlobalWatchSortModes { get; private set; } = [];
    public IReadOnlyList<string> GlobalWatchModes { get; private set; } = [];
    public string GlobalWatchProcessHeaderText => FormatGlobalWatchSortHeader(GetText("Ui_ApplicationProcess"), GlobalSortName);
    public string GlobalWatchPidHeaderText => FormatGlobalWatchSortHeader(GetText("Ui_PidOrCount"), GlobalSortPid);
    public string GlobalWatchCpuHeaderText => FormatGlobalWatchSortHeader(GetText("Ui_CPU"), GlobalSortCpu);
    public string GlobalWatchRamHeaderText => FormatGlobalWatchSortHeader(GetText("Ui_RAM"), GlobalSortRam);
    public string GlobalWatchDiskHeaderText => FormatGlobalWatchSortHeader(GetText("Ui_Disk"), GlobalSortDisk);
    public string GlobalWatchProfileHeaderText => FormatGlobalWatchSortHeader(GetText("Ui_Profile"), GlobalSortProfile);
    public string GlobalWatchHealthHeaderText => FormatGlobalWatchSortHeader(GetText("Ui_Health"), GlobalSortHealth);

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

    public LanguageOptionViewModel? SelectedLanguageOption
    {
        get => _selectedLanguageOption;
        set => SetProperty(ref _selectedLanguageOption, value);
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

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool StartMinimizedToTray
    {
        get => _startMinimizedToTray;
        set => SetProperty(ref _startMinimizedToTray, value);
    }

    public bool TrustExplainerDismissed
    {
        get => _trustExplainerDismissed;
        private set
        {
            if (SetProperty(ref _trustExplainerDismissed, value))
            {
                OnPropertyChanged(nameof(ShowTrustExplainer));
            }
        }
    }

    public bool ShowTrustExplainer => !TrustExplainerDismissed;

    public string LanguageSettingsStatusText
    {
        get => _languageSettingsStatusText;
        private set => SetProperty(ref _languageSettingsStatusText, value);
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
    public string AppVersionBadgeText => $"v{FormatDisplayVersion(_updateService.CurrentVersion)}";
    public string AppWindowTitle => $"Session Perf Tracker {AppVersionBadgeText}";
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

    public bool AutomaticallyInstallUpdatesOnStartup
    {
        get => _automaticallyInstallUpdatesOnStartup;
        set => SetProperty(ref _automaticallyInstallUpdatesOnStartup, value);
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

    public string BugReportText
    {
        get => _bugReportText;
        set => SetProperty(ref _bugReportText, value);
    }

    public string FeatureFeedbackText
    {
        get => _featureFeedbackText;
        set => SetProperty(ref _featureFeedbackText, value);
    }

    public string FeedbackStatusText
    {
        get => _feedbackStatusText;
        private set => SetProperty(ref _feedbackStatusText, value);
    }

    public string FeedbackDirectoryText => GetFeedbackDirectory();

    public bool IncludeLatestSessionInFeedback
    {
        get => _includeLatestSessionInFeedback;
        set => SetProperty(ref _includeLatestSessionInFeedback, value);
    }

    public bool IncludeScreenshotInFeedback
    {
        get => _includeScreenshotInFeedback;
        set => SetProperty(ref _includeScreenshotInFeedback, value);
    }

    public bool HasFeedbackDeliveryHistory => FeedbackDeliveryHistory.Count > 0;

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

    public int SelectedSettingsTabIndex
    {
        get => _selectedSettingsTabIndex;
        set => SetProperty(ref _selectedSettingsTabIndex, value);
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
                NotifyGlobalWatchSortHeaderProperties();
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

    public string LiveSessionTargetText
    {
        get
        {
            if (IsRecording) return _activeTargetName;
            var mode = SelectedTargetMode;
            if (mode == TargetModeRunningProcess && SelectedRunningTarget is not null) return SelectedRunningTarget.DisplayName;
            if (mode == TargetModeAssignedApps && SelectedAssignedTarget is not null) return SelectedAssignedTarget.DisplayText;
            if (mode == TargetModeExecutable && !string.IsNullOrWhiteSpace(SelectedExecutablePath)) return Path.GetFileName(SelectedExecutablePath);
            return "No target selected";
        }
    }

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
    public string SelectedGlobalProcessReasonText => SelectedGlobalProcess?.ProfileReason ?? GetText("Ui_SelectProcessProfileHealth");
    public string SelectedGlobalProcessIncludedText => SelectedGlobalProcess?.IncludedProcessSummaryText ?? "n/a";
    public string SelectedGlobalProcessWhatItDoesText
    {
        get
        {
            var selected = SelectedGlobalProcess;
            if (selected is null)
            {
                return GetText("Ui_SelectToInspect");
            }

            var product = string.IsNullOrWhiteSpace(selected.ProductName) || selected.ProductName == "Unavailable"
                ? selected.AppName
                : selected.ProductName;
            var description = string.IsNullOrWhiteSpace(selected.FileDescription) || selected.FileDescription == "Unavailable"
                ? GetText("Ui_NoDescriptionExp")
                : selected.FileDescription;
            var company = string.IsNullOrWhiteSpace(selected.CompanyName) || selected.CompanyName == "Unavailable"
                ? GetText("Ui_PublisherUnknown")
                : selected.CompanyName;
            var role = selected.IsGroup
                ? string.Format(GetText("Ui_AppGroupRelation"), selected.ExeName, selected.InstanceCount)
                : selected.Process.ParentProcessId is not null
                    ? string.Format(GetText("Ui_SubprocessRelation"), selected.ParentProcessText)
                    : selected.Process.DescendantProcessCount > 0
                        ? string.Format(GetText("Ui_RootCandidateRelation"), selected.Process.DescendantProcessCount)
                        : GetText("Ui_StandaloneRelation");

            return $"{product}: {description} {GetText("Ui_Company")}: {company}. {role}";
        }
    }
    public string SelectedGlobalProcessInspectorModeText => IsGlobalWatchGroupedMode
        ? GetText("Ui_ModeAppGroup")
        : GetText("Ui_ModeIndividual");
    public string SelectedGlobalProcessRelationText => SelectedGlobalProcess is null
        ? GetText("Ui_NoRelationData")
        : SelectedGlobalProcess.IsGroup
            ? string.Format(GetText("Ui_AppGroupRelation"), SelectedGlobalProcess.ExeName, SelectedGlobalProcess.InstanceCount)
            : SelectedGlobalProcess.Process.ParentProcessId is not null
                ? string.Format(GetText("Ui_SubprocessRelation"), SelectedGlobalProcess.ParentProcessText)
                : SelectedGlobalProcess.Process.DescendantProcessCount > 0
                    ? string.Format(GetText("Ui_RootCandidateRelation"), SelectedGlobalProcess.Process.DescendantProcessCount)
                    : GetText("Ui_StandaloneRelation");
    public string SelectedGlobalProcessAppearsBecauseText => SelectedGlobalProcess is null
        ? GetText("Ui_NoTargetSelected")
        : SelectedGlobalProcess.IsGroup
            ? GetText("Ui_AppModeReason")
            : GetText("Ui_PidScanReason");
    public string SelectedGlobalProcessCommandLineText => GetText("Ui_NotCaptured");
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

            if (launch is null) return GetText("Ui_NoSuspiciousLaunch");

            var time = launch.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var parent = launch.ParentProcessId is null
                ? string.Empty
                : $" {GetText("Ui_From")} {(string.IsNullOrWhiteSpace(launch.ParentProcessName) ? "Unknown" : launch.ParentProcessName)} ({launch.ParentProcessId})";

            return string.Format(GetText("Ui_LastLaunch"), time) + parent;
        }
    }
    public string GlobalWatchEmptyStateText => IsGlobalWatchGroupedMode
        ? GetText("Ui_NoAppsMatch")
        : GetText("Ui_NoProcessesMatch");
    public bool HasGlobalWatchRows => GlobalWatchProcesses.Count > 0;
    public bool CanInspectSelectedGlobalProcess => SelectedGlobalProcess is not null;
    public bool CanOpenSelectedGlobalProcessFileLocation => TryGetSelectedGlobalProcessUsablePath(out var path)
        && File.Exists(path);
    public bool CanCopySelectedGlobalProcessPath => TryGetSelectedGlobalProcessUsablePath(out _);
    public bool CanKillSelectedGlobalProcess => IsGlobalWatchRowActionAllowed(SelectedGlobalProcess, requiresPath: false);
    public bool CanBanSelectedGlobalProcess => SelectedGlobalProcess is not null
        && TryGetSelectedGlobalProcessUsablePath(out _)
        && IsGlobalWatchRowActionAllowed(SelectedGlobalProcess, requiresPath: true);
    public string SelectedGlobalProcessKillTreeLabel => SelectedGlobalProcess?.IsGroup == true
        ? GetText("Ui_KillAppGroup")
        : GetText("Ui_KillProcessTree");
    public string SelectedGlobalProcessBanText => SelectedGlobalProcess is null
        ? GetText("Ui_SelectRowCreateBan")
        : !TryGetSelectedGlobalProcessUsablePath(out _)
            ? GetText("Ui_FullPathUnavailableCannotBan")
            : !AssessGlobalWatchRowForAction(SelectedGlobalProcess, requiresPath: true).IsAllowed
                ? FormatText("Ui_ProtectedTargetReason", AssessGlobalWatchRowForAction(SelectedGlobalProcess, requiresPath: true).Reason)
            : FormatText("Ui_BanTargetByFullPath", SelectedGlobalProcess.ExeName);
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
    public string InspectorStatusText => _processInspectorTarget?.StatusText ?? GetText("Ui_NotRunning");
    public string InspectorProfileText => _processInspectorTarget?.ProfileText ?? "n/a";
    public string InspectorHealthText => _processInspectorTarget?.HealthText ?? "n/a";
    public string InspectorReasonText => _processInspectorTarget?.ReasonText ?? GetText("Ui_NoTargetSelected");
    public string InspectorIncludedText => _processInspectorTarget?.IncludedText ?? "n/a";
    public string InspectorRelationText => _processInspectorTarget?.RelationText ?? GetText("Ui_NoRelationData");
    public string InspectorAppearsBecauseText => _processInspectorTarget?.AppearsBecauseText ?? GetText("Ui_NoTargetSelected");
    public string InspectorCommandLineText => _processInspectorTarget?.CommandLineText ?? GetText("Ui_NotCaptured");
    public bool InspectorIsGroup => _processInspectorTarget?.IsGroup ?? false;
    public IReadOnlyList<GlobalProcessMemberViewModel> InspectorIncludedProcessRows => _processInspectorTarget?.IncludedProcessRows ?? [];
    public string InspectorWhatItDoesText
    {
        get
        {
            var target = _processInspectorTarget;
            if (target is null)
            {
                return GetText("Ui_NoInspectorTarget");
            }

            var product = string.IsNullOrWhiteSpace(target.ProductName) || target.ProductName.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                ? target.ExeName
                : target.ProductName;
            var description = string.IsNullOrWhiteSpace(target.FileDescription) || target.FileDescription == "Unavailable"
                ? GetText("Ui_NoDescription")
                : target.FileDescription;
            var company = string.IsNullOrWhiteSpace(target.CompanyName) || target.CompanyName.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                ? GetText("Ui_PublisherUnknown")
                : target.CompanyName;
            var running = target.IsRunning
                ? GetText("Ui_InspectorRunningHint")
                : GetText("Ui_InspectorNotRunningHint");

            return $"{product}: {description} {GetText("Ui_Company")}: {company}. {running}";
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
    public bool CanKillInspectorTarget => IsInspectorActionAllowed(requiresRunningTarget: true, requiresPath: false);
    public bool CanBanInspectorTarget => TryGetInspectorUsablePath(out _)
        && IsInspectorActionAllowed(requiresRunningTarget: false, requiresPath: true);
    public bool CanMarkInspectorSuspicious => TryGetInspectorUsablePath(out _)
        && !IsInspectorTargetSuspicious;
    public bool CanRemoveInspectorSuspicious => IsInspectorTargetSuspicious;
    public bool IsInspectorTargetSuspicious => _processInspectorTarget is not null
        && !string.IsNullOrWhiteSpace(_processInspectorTarget.NormalizedFullPath)
        && _thresholdSettingsStore.Current.SuspiciousWatchlist.Items.Any(item =>
            string.Equals(item.NormalizedPath, _processInspectorTarget.NormalizedFullPath, StringComparison.OrdinalIgnoreCase));
    public string InspectorKillTreeLabel => _processInspectorTarget?.IsGroup == true
        ? GetText("Ui_KillAppGroup")
        : GetText("Ui_KillProcessTree");
    public string InspectorSuspiciousText => _processInspectorTarget is null
        ? GetText("Ui_NoInspectorTarget")
        : !TryGetInspectorUsablePath(out _)
            ? GetText("Ui_FullPathUnavailableCannotWatch")
            : IsInspectorTargetSuspicious
                ? GetText("Ui_MarkedSuspiciousLaunchesLogged")
                : GetText("Ui_NotMarkedSuspicious");
    public string InspectorBanText => _processInspectorTarget is null
        ? GetText("Ui_NoInspectorTarget")
        : !TryGetInspectorUsablePath(out _)
            ? GetText("Ui_FullPathUnavailableCannotBan")
            : !AssessInspectorAction(requiresRunningTarget: false, requiresPath: true).IsAllowed
                ? FormatText("Ui_ProtectedTargetReason", AssessInspectorAction(requiresRunningTarget: false, requiresPath: true).Reason)
            : FormatText("Ui_BanTargetByFullPath", _processInspectorTarget.ExeName);
    public bool InspectorHasRecommendation => _processInspectorTarget is not null
        && ProfileRecommendations.Any(recommendation => string.Equals(recommendation.ExeName, _processInspectorTarget.ExeName, StringComparison.OrdinalIgnoreCase));
    public string InspectorRecommendationText => InspectorHasRecommendation
        ? GetText("Ui_ProfileRecommendationReady")
        : GetText("Ui_NoActiveRecommendationForExe");
    public bool HasSessionDetails => SelectedSession is not null;
    public bool HasSessionDetailEvents => SessionDetailEvents.Count > 0;
    public bool HasSessionDetailMetrics => SessionDetailMetricSummaries.Count > 0;
    public bool HasSessionDetailRecommendations => SessionDetailRecommendations.Count > 0;
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
            var mode when mode == TargetModeExecutable => !string.IsNullOrWhiteSpace(SelectedExecutablePath),
            var mode when mode == TargetModeAssignedApps => SelectedAssignedTarget?.RunningProcessId is not null,
            _ => SelectedRunningTarget is not null
        });

    public bool CanStop => IsRecording;

    public bool CanMonitorSelectedGlobalProcess => SelectedGlobalProcess is not null;

    public string TargetReadinessText
    {
        get
        {
            if (SelectedTargetMode == TargetModeExecutable)
            {
                return string.IsNullOrWhiteSpace(SelectedExecutablePath)
                    ? "Choose an executable in the manual launch section."
                    : $"Ready to launch and track {Path.GetFileName(SelectedExecutablePath)}.";
            }
            if (SelectedTargetMode == TargetModeAssignedApps)
            {
                return SelectedAssignedTarget is null
                    ? "No assigned app selected."
                    : SelectedAssignedTarget.RunningProcessId is null
                        ? $"{SelectedAssignedTarget.ExeName} is not running. Start it manually, then refresh."
                        : $"Ready to attach to {SelectedAssignedTarget.ExeName} ({SelectedAssignedTarget.RunningStatus}).";
            }
            return SelectedRunningTarget is null
                ? "Select a running process."
                : $"Ready to attach to {SelectedRunningTarget.DisplayName}.";
        }
    }

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
    public string HangCountText => IsRecording ? _liveHangCount.ToString("N0") : "0";

    public async Task InitializeAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        StoragePath = storagePath;
        var thresholds = await _thresholdSettingsStore.LoadAsync(cancellationToken);
        ApplyLanguageSettingsToUi(thresholds.Language);
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
        ApplyStartupRegistration(thresholds.Behavior);
        await RefreshRunningProcessesAsync(cancellationToken);
        await ReloadSessionsAsync(selectSessionId: null, cancellationToken);
        await RefreshExportFilesAsync(cancellationToken);
        StartSelfMonitoring();
        StartGlobalWatch();
        _ = AutoInstallUpdatesOnStartupAsync(thresholds.Updates, cancellationToken);
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

    private void NotifySessionDetailProperties()
    {
        OnPropertyChanged(nameof(HasSessionDetails));
        OnPropertyChanged(nameof(HasSessionDetailEvents));
        OnPropertyChanged(nameof(HasSessionDetailMetrics));
        OnPropertyChanged(nameof(HasSessionDetailRecommendations));
        OnPropertyChanged(nameof(SessionDetailTitle));
        OnPropertyChanged(nameof(SessionDetailSubtitle));
    }

    private static string FormatDisplayVersion(string version)
    {
        var normalized = version.Trim();
        return Version.TryParse(normalized, out var parsed) && parsed.Build == -1 && parsed.Revision == -1 && parsed.Minor >= 0 && parsed.Major >= 0
            ? parsed.Minor == 0 ? parsed.Major.ToString(CultureInfo.InvariantCulture) : $"{parsed.Major}.{parsed.Minor}"
            : normalized.EndsWith(".0", StringComparison.Ordinal) && normalized.Count(character => character == '.') == 2
                ? normalized[..^2]
                : normalized;
    }

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
