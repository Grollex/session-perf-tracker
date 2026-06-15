using System.Windows;
using System.Threading;
using System.Text.Json;
using System.IO;
using Microsoft.Data.Sqlite;
using SessionPerfTracker.App.Localization;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\SessionPerfTracker.SingleInstance";
    private const string ShowWindowEventName = "Local\\SessionPerfTracker.ShowMainWindow";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowWaitHandle;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _ownsSingleInstanceMutex = isFirstInstance;

        if (!isFirstInstance)
        {
            try
            {
                _showWindowEvent.Set();
            }
            catch
            {
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
