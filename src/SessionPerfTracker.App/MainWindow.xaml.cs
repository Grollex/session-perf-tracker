using System.Windows;
using System.ComponentModel;
using System.Diagnostics;
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
        _viewModel.LanguageRestartRequested += OnLanguageRestartRequested;
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
        _viewModel.LanguageRestartRequested -= OnLanguageRestartRequested;
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
                    $"{_viewModel.AppWindowTitle} is still running",
                    "Use the tray icon to reopen it or choose Exit to quit.",
                    WinForms.ToolTipIcon.Info);
                _trayNotificationShown = true;
            }
        }
    }

    private void InitializeTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add($"Open {_viewModel.AppWindowTitle}", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitFromTray));

        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = _viewModel.AppWindowTitle,
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

    private void OpenLive_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenLive();
    }

    private void OnLanguageRestartRequested(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(RestartApplication);
    }

    private void RestartApplication()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return;
        }

        try
        {
            _isExitRequested = true;
            _notifyIcon?.Visible = false;
            var escapedPath = executablePath.Replace("\"", "\"\"");
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 1 /nobreak >nul & start \"\" \"{escapedPath}\"",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false
            });
            Close();
        }
        catch
        {
        }
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
