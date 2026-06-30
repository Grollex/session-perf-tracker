using System.Windows;
using System.Threading;
using System.Text.Json;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using SessionPerfTracker.App.Localization;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\SessionPerfTracker.SingleInstance";
    private const string ShowWindowEventName = "Local\\SessionPerfTracker.ShowMainWindow";
    private const string StartupRegistryValueName = "Session Perf Tracker";

    internal static bool StartMinimizedToTray { get; private set; }

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowWaitHandle;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (HasArg(e.Args, "--register-startup-and-exit"))
        {
            SetStartupRegistration(enabled: true);
            Shutdown();
            return;
        }

        if (HasArg(e.Args, "--unregister-startup-and-exit"))
        {
            SetStartupRegistration(enabled: false);
            Shutdown();
            return;
        }

        StartMinimizedToTray =
            HasArg(e.Args, "--background")
            || HasArg(e.Args, "--minimized")
            || HasArg(e.Args, "/background")
            || HasArg(e.Args, "/minimized");

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _ownsSingleInstanceMutex = isFirstInstance;

        if (!isFirstInstance)
        {
            if (!StartMinimizedToTray)
            {
                try
                {
                    _showWindowEvent.Set();
                }
                catch
                {
                }
            }

            Shutdown();
            return;
        }

        LocalizationManager.ApplyLanguage(ResolveStartupLanguage());

        _showWindowWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            _showWindowEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowExistingMainWindow),
            null,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWindowWaitHandle?.Unregister(null);
        _showWindowEvent?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static bool HasArg(string[] args, string expected) =>
        args.Any(argument => argument.Equals(expected, StringComparison.OrdinalIgnoreCase));

    private static void SetStartupRegistration(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                writable: true);
            if (key is null)
            {
                return;
            }

            if (!enabled)
            {
                key.DeleteValue(StartupRegistryValueName, throwOnMissingValue: false);
                return;
            }

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return;
            }

            key.SetValue(StartupRegistryValueName, $"\"{executablePath}\" --background", RegistryValueKind.String);
        }
        catch
        {
        }
    }

    private static string ResolveStartupLanguage()
    {
        try
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SessionPerfTracker");
            var dbPath = Path.Combine(appDataPath, "sessionperftracker.db");
            if (File.Exists(dbPath))
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString();
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value_json FROM settings WHERE key = 'language' LIMIT 1;";
                if (command.ExecuteScalar() is string languageJson)
                {
                    return ReadLanguageCode(languageJson);
                }
            }

            var legacySettingsPath = Path.Combine(appDataPath, "settings.json");
            if (File.Exists(legacySettingsPath))
            {
                return ReadLegacyLanguageCode(File.ReadAllText(legacySettingsPath));
            }
        }
        catch
        {
        }

        return AppLanguageSettings.DefaultLanguageCode;
    }

    private static string ReadLanguageCode(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(nameof(AppLanguageSettings.LanguageCode), out var languageCode)
            ? LocalizationManager.NormalizeLanguageCode(languageCode.GetString())
            : AppLanguageSettings.DefaultLanguageCode;
    }

    private static string ReadLegacyLanguageCode(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty(nameof(CpuRamThresholdSettings.Language), out var language)
            && language.TryGetProperty(nameof(AppLanguageSettings.LanguageCode), out var languageCode))
        {
            return LocalizationManager.NormalizeLanguageCode(languageCode.GetString());
        }

        return AppLanguageSettings.DefaultLanguageCode;
    }

    private void ShowExistingMainWindow()
    {
        if (MainWindow is not { } window)
        {
            return;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Focus();
    }
}
