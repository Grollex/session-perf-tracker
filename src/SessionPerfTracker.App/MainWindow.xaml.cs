using System.Windows;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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

    private string GetText(string key) => TryFindResource(key) as string ?? key;

    private string FormatText(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, GetText(key), args);

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
        _viewModel.DestructiveProcessActionConfirmationRequested += OnDestructiveProcessActionConfirmationRequested;
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
        if (App.StartMinimizedToTray && _viewModel.MinimizeToTrayOnClose)
        {
            Hide();
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = true;
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.UpdateAvailablePromptRequested -= OnUpdateAvailablePromptRequested;
        _viewModel.DestructiveProcessActionConfirmationRequested -= OnDestructiveProcessActionConfirmationRequested;
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
                    FormatText("Ui_TrayRunningTitle", _viewModel.AppWindowTitle),
                    GetText("Ui_TrayRunningMessage"),
                    WinForms.ToolTipIcon.Info);
                _trayNotificationShown = true;
            }
        }
    }

    private void InitializeTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(FormatText("Ui_TrayOpenApp", _viewModel.AppWindowTitle), null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add(GetText("Ui_TrayExit"), null, (_, _) => Dispatcher.Invoke(ExitFromTray));

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

    private void ReportBug_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenFeedback();
    }

    private async void DismissTrustExplainer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.DismissTrustExplainerAsync();
        }
        catch (Exception error)
        {
            System.Windows.MessageBox.Show(
                this,
                error.Message,
                _viewModel.AppWindowTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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
        var latestVersion = e.Result.LatestVersion ?? e.Result.Manifest?.Version ?? GetText("Ui_NewVersionFallback");
        var notes = string.IsNullOrWhiteSpace(e.Result.Manifest?.ReleaseNotes)
            ? GetText("Ui_NoReleaseNotesProvided")
            : e.Result.Manifest.ReleaseNotes;

        var updateNow = MessageBox.Show(
            this,
            FormatText("Ui_UpdateAvailableMessage", latestVersion, _viewModel.CurrentVersionText, notes),
            GetText("Ui_UpdateAvailableTitle"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Information);

        e.Choice = updateNow switch
        {
            MessageBoxResult.Yes => UpdatePromptChoice.UpdateNow,
            MessageBoxResult.Cancel => UpdatePromptChoice.SkipVersion,
            _ => UpdatePromptChoice.Later
        };
    }

    private void OnDestructiveProcessActionConfirmationRequested(
        object? sender,
        DestructiveProcessActionConfirmationEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            e.Message,
            e.Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        e.IsConfirmed = result == MessageBoxResult.Yes;
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
