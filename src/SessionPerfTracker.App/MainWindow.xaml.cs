using System.Windows;
using System.ComponentModel;
using System.IO;
using SessionPerfTracker.App.ViewModels;
using SessionPerfTracker.Domain.Services;
using SessionPerfTracker.Infrastructure.Context;
using SessionPerfTracker.Infrastructure.Collectors;
using SessionPerfTracker.Infrastructure.Events;
using SessionPerfTracker.Infrastructure.Export;
using SessionPerfTracker.Infrastructure.GlobalWatch;
using SessionPerfTracker.Infrastructure.Runner;
using SessionPerfTracker.Infrastructure.Settings;
using SessionPerfTracker.Infrastructure.SelfMonitoring;
using SessionPerfTracker.Infrastructure.Storage;
using SessionPerfTracker.Infrastructure.Targeting;
using SessionPerfTracker.Infrastructure.Updates;
using Drawing = System.Drawing;
using MessageBox = System.Windows.MessageBox;
using WinForms = System.Windows.Forms;

namespace SessionPerfTracker.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly string _storagePath;
    private WinForms.NotifyIcon? _notifyIcon;
    private bool _isExitRequested;
    private bool _trayNotificationShown;

    public MainWindow()
    {
        InitializeComponent();

        var summaryService = new SessionSummaryService();
        var comparisonEngine = new SessionComparisonEngine();
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SessionPerfTracker");
        Directory.CreateDirectory(appDataPath);
        var exportsPath = Path.Combine(appDataPath, "exports");
        Directory.CreateDirectory(exportsPath);
        var updatesPath = Path.Combine(appDataPath, "updates");
        Directory.CreateDirectory(updatesPath);
        _storagePath = Path.Combine(appDataPath, "sessionperftracker.db");
        var legacySessionsPath = Path.Combine(appDataPath, "sessions.json");
        var legacySettingsPath = Path.Combine(appDataPath, "settings.json");

        var sessionStore = new SqliteSessionStore(_storagePath, summaryService, legacySessionsPath);
        var thresholdSettingsStore = new SqliteThresholdSettingsStore(_storagePath, legacySettingsPath);
        var metricCollector = new ProcessCpuMemoryCollector();
        var spikeContextProvider = new LightweightSystemContextProvider();
        var selfMonitoringProvider = new SelfMonitoringProvider();
        var globalProcessScanner = new LightweightGlobalProcessScanner();
        var processControlService = new WindowsProcessControlService();
        var exportService = new HtmlCsvExportService(exportsPath);
        var updateService = new HttpUpdateService();
        var targetResolver = new ProcessTargetResolver();
        var detectors = new[] { new CpuMemoryThresholdSpikeDetector(thresholdSettingsStore) };
        var eventNoiseFilter = new AntiNoiseEventFilter(thresholdSettingsStore);
        var sessionRunner = new ProcessSessionRunner(
            metricCollector,
            sessionStore,
            summaryService,
            detectors,
            spikeContextProvider,
            eventNoiseFilter,
            thresholdSettingsStore);

        _viewModel = new MainWindowViewModel(
            sessionStore,
            comparisonEngine,
            targetResolver,
            sessionRunner,
            metricCollector,
            thresholdSettingsStore,
            spikeContextProvider,
            selfMonitoringProvider,
            globalProcessScanner,
            processControlService,
            exportService,
            updateService,
            exportsPath,
            updatesPath);
        _viewModel.UpdateAvailablePromptRequested += OnUpdateAvailablePromptRequested;
        _viewModel.UpdateInstallerLaunched += OnUpdateInstallerLaunched;
        DataContext = _viewModel;
        InitializeTrayIcon();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync(_storagePath);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.UpdateAvailablePromptRequested -= OnUpdateAvailablePromptRequested;
        _viewModel.UpdateInstallerLaunched -= OnUpdateInstallerLaunched;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        _viewModel.Shutdown();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExitRequested || !_viewModel.MinimizeToTrayOnClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = true;
            if (!_trayNotificationShown)
            {
                _notifyIcon.ShowBalloonTip(
                    2500,
                    "Session Perf Tracker is still running",
                    "Use the tray icon to reopen it or choose Exit to quit.",
                    WinForms.ToolTipIcon.Info);
                _trayNotificationShown = true;
            }
        }
    }

    private void InitializeTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitFromTray));

        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "Session Perf Tracker",
            Icon = LoadTrayIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            try
            {
                return new Drawing.Icon(iconPath);
            }
            catch
            {
            }
        }

        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                return Drawing.Icon.ExtractAssociatedIcon(exePath) ?? Drawing.SystemIcons.Application;
            }
        }
        catch
        {
        }

        return Drawing.SystemIcons.Application;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Focus();
    }

    private void ExitFromTray()
    {
        _isExitRequested = true;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        Close();
    }

    private void BrowseExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            Title = "Select target executable"
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.SelectExecutable(dialog.FileName);
        }
    }

    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshRunningProcessesAsync();
    }

    private async void RefreshGlobalWatch_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshGlobalWatchAsync();
    }

    private async void MonitorGlobalProcess_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.MonitorSelectedGlobalProcessAsync();
    }

    private async void KillGlobalProcess_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.KillSelectedGlobalProcessAsync();
    }

    private async void KillGlobalProcessTree_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.KillSelectedGlobalProcessTreeOrGroupAsync();
    }

    private async void BanGlobalProcess_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.BanSelectedGlobalProcessAsync();
    }

    private async void BanAndKillGlobalProcess_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.BanAndKillSelectedGlobalProcessAsync();
    }

    private void OpenGlobalProcessInspector_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.PrepareInspectorFromSelectedGlobalProcess())
        {
            return;
        }

        ShowProcessInspector();
    }

    private void ShowProcessInspector()
    {
        try
        {
            var inspector = new ProcessInspectorWindow(_viewModel)
            {
                Owner = this
            };
            inspector.ShowDialog();
        }
        catch (Exception error)
        {
            MessageBox.Show(
                this,
                $"Could not open Process Inspector:\n{error.Message}",
                "Process Inspector",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void StartRecording_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.StartRecordingAsync();
    }

    private async void StopRecording_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.StopRecordingAsync();
    }

    private async void RefreshSessions_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ReloadSessionsAsync();
    }

    private async void CaptureRamDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.CaptureRamDiagnosticAsync();
    }

    private async void CaptureSystemContext_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.CaptureSystemContextAsync();
    }

    private async void SaveThresholdSettings_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveThresholdSettingsAsync();
    }

    private async void SaveAntiNoiseSettings_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveAntiNoiseSettingsAsync();
    }

    private async void SaveCaptureSettings_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveCaptureSettingsAsync();
    }

    private async void SaveAppBehaviorSettings_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveAppBehaviorSettingsAsync();
    }

    private async void SaveSelectedProfile_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveSelectedProfileAsync();
    }

    private async void SaveAppProfileAssignment_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveAppProfileAssignmentAsync();
    }

    private async void AssignCurrentTargetProfile_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.AssignCurrentTargetProfileAsync();
    }

    private async void RemoveAppProfileAssignment_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RemoveSelectedAppProfileAssignmentAsync();
    }

    private async void ResetSelectedProfile_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ResetSelectedProfileAsync();
    }

    private async void ResetAllThresholdSettings_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ResetAllThresholdSettingsAsync();
    }

    private async void SaveRetentionSettings_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveRetentionSettingsAsync();
    }

    private async void DeleteAllSessions_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.DeleteAllSessionsAsync();
    }

    private async void DeleteSessionsOlderThan1Day_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.DeleteSessionsOlderThanAsync(1);
    }

    private async void DeleteSessionsOlderThan7Days_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.DeleteSessionsOlderThanAsync(7);
    }

    private async void DeleteSessionsOlderThan30Days_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.DeleteSessionsOlderThanAsync(30);
    }

    private async void DeleteFilteredSessions_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.DeleteFilteredSessionsAsync();
    }

    private void BrowseExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select export folder",
            InitialDirectory = Directory.Exists(_viewModel.ExportDirectoryText)
                ? _viewModel.ExportDirectoryText
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.ExportDirectoryText = dialog.FolderName;
        }
    }

    private async void SaveExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveExportSettingsAsync();
    }

    private async void ResetExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ResetExportDirectoryAsync();
    }

    private async void OpenExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.OpenExportDirectoryAsync();
    }

    private async void RefreshExports_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshExportFilesAsync();
    }

    private async void OpenSelectedExport_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.OpenSelectedExportAsync();
    }

    private async void SaveUpdateSettings_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveUpdateSettingsAsync();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.CheckForUpdatesAsync();
    }

    private async void DownloadAndLaunchUpdate_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.DownloadAndLaunchUpdateAsync();
        ExitForUpdateIfRequested();
    }

    private async void SkipUpdate_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SkipAvailableUpdateAsync();
    }

    private async void ExportSelectedSessionHtml_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ExportSelectedSessionHtmlAsync();
    }

    private async void ExportSelectedSessionCsv_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ExportSelectedSessionCsvAsync();
    }

    private async void ExportCurrentCompareHtml_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ExportCurrentCompareHtmlAsync();
    }

    private async void ExportCurrentCompareCsv_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ExportCurrentCompareCsvAsync();
    }

    private void OpenSessionDetails_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenSelectedSessionDetails();
    }

    private void BackToSessions_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.BackToSessions();
    }

    private void OpenLive_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenLive();
    }

    private async void PromoteRecommendation_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.PromoteSelectedRecommendationAsync();
    }

    private async void PromoteSelectedRecommendations_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProfileRecommendationsList.SelectedItems
            .OfType<ProfileRecommendationViewModel>()
            .ToArray();
        await _viewModel.PromoteRecommendationsAsync(selected);
    }

    private async void DenySelectedRecommendations_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProfileRecommendationsList.SelectedItems
            .OfType<ProfileRecommendationViewModel>()
            .ToArray();
        await _viewModel.DenyRecommendationsAsync(selected);
    }

    private async void RemoveRecommendationDeny_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RemoveSelectedRecommendationDenyAsync();
    }

    private void SelectRecommendationTarget_Click(object sender, RoutedEventArgs e)
    {
        var recommendation = (sender as FrameworkElement)?.DataContext as ProfileRecommendationViewModel
            ?? _viewModel.SelectedProfileRecommendation;
        _viewModel.SelectRecommendationTargetForOverview(recommendation);
    }

    private void InspectRecommendationTarget_Click(object sender, RoutedEventArgs e)
    {
        var recommendation = (sender as FrameworkElement)?.DataContext as ProfileRecommendationViewModel
            ?? _viewModel.SelectedProfileRecommendation;
        if (_viewModel.SelectRecommendationTargetForInspector(recommendation))
        {
            ShowProcessInspector();
        }
    }

    private async void MarkRecommendationSuspicious_Click(object sender, RoutedEventArgs e)
    {
        var recommendation = (sender as FrameworkElement)?.DataContext as ProfileRecommendationViewModel
            ?? _viewModel.SelectedProfileRecommendation;
        await _viewModel.MarkRecommendationTargetSuspiciousAsync(recommendation);
    }

    private async void BanRecommendationTarget_Click(object sender, RoutedEventArgs e)
    {
        var recommendation = (sender as FrameworkElement)?.DataContext as ProfileRecommendationViewModel
            ?? _viewModel.SelectedProfileRecommendation;
        await _viewModel.BanRecommendationTargetAsync(recommendation);
    }

    private async void BanAndKillRecommendationTarget_Click(object sender, RoutedEventArgs e)
    {
        var recommendation = (sender as FrameworkElement)?.DataContext as ProfileRecommendationViewModel
            ?? _viewModel.SelectedProfileRecommendation;
        await _viewModel.BanRecommendationTargetAsync(recommendation, killAfterBan: true);
    }

    private void SelectJournalTarget_Click(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as GlobalWatchJournalGroupViewModel;
        _viewModel.SelectJournalTargetForOverview(group);
    }

    private void InspectJournalTarget_Click(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as GlobalWatchJournalGroupViewModel;
        if (_viewModel.SelectJournalTargetForInspector(group))
        {
            ShowProcessInspector();
        }
    }

    private async void MarkJournalSuspicious_Click(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as GlobalWatchJournalGroupViewModel;
        await _viewModel.MarkJournalTargetSuspiciousAsync(group);
    }

    private async void BanJournalTarget_Click(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as GlobalWatchJournalGroupViewModel;
        await _viewModel.BanJournalTargetAsync(group);
    }

    private async void BanAndKillJournalTarget_Click(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as GlobalWatchJournalGroupViewModel;
        await _viewModel.BanJournalTargetAsync(group, killAfterBan: true);
    }

    private async void AssignGlobalWatchProfile_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.AssignSelectedGlobalProcessProfileAsync();
    }

    private void OpenRecommendationsForGlobalWatch_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenRecommendationsForSelectedGlobalProcess();
    }

    private async void MarkGlobalWatchSuspicious_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.MarkSelectedGlobalProcessSuspiciousAsync();
    }

    private async void RemoveGlobalWatchSuspicious_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RemoveSelectedGlobalProcessSuspiciousAsync();
    }

    private async void RemoveSelectedSuspiciousWatchItem_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RemoveSelectedSuspiciousWatchItemAsync();
    }

    private void InspectSuspiciousWatchItem_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext as SuspiciousWatchItemViewModel
            ?? _viewModel.SelectedSuspiciousWatchItem;
        if (_viewModel.SelectSuspiciousTargetForInspector(item))
        {
            ShowProcessInspector();
        }
    }

    private async void RemoveSelectedProcessBan_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RemoveSelectedProcessBanAsync();
    }

    private void OnUpdateAvailablePromptRequested(object? sender, UpdateAvailablePromptEventArgs e)
    {
        var latestVersion = e.Result.LatestVersion ?? e.Result.Manifest?.Version ?? "new";
        var notes = string.IsNullOrWhiteSpace(e.Result.Manifest?.ReleaseNotes)
            ? "No release notes were provided."
            : e.Result.Manifest.ReleaseNotes;

        var updateNow = MessageBox.Show(
            this,
            $"Session Perf Tracker {latestVersion} is available.\n\nCurrent version: {_viewModel.CurrentVersionText}\n\n{notes}\n\nInstall now?\n\nYes = update now\nNo = later\nCancel = skip this version",
            "Update available",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Information);

        e.Choice = updateNow switch
        {
            MessageBoxResult.Yes => UpdatePromptChoice.UpdateNow,
            MessageBoxResult.Cancel => UpdatePromptChoice.SkipVersion,
            _ => UpdatePromptChoice.Later
        };
    }

    private void OnUpdateInstallerLaunched(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(ExitForUpdateIfRequested);
    }

    private void ExitForUpdateIfRequested()
    {
        if (!_viewModel.IsUpdateRestartRequested)
        {
            return;
        }

        _isExitRequested = true;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        System.Windows.Application.Current.Shutdown();
    }
}
