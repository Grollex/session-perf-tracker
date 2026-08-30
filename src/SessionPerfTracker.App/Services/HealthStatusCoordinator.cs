using System.IO;
using Microsoft.Win32;

namespace SessionPerfTracker.App.Services;

public sealed class HealthStatusCoordinator
{
    private readonly string _startupRegistryValueName;
    private readonly Func<string, string> _getText;
    private readonly Func<string, object?[], string> _formatText;

    public HealthStatusCoordinator(
        string startupRegistryValueName,
        Func<string, string> getText,
        Func<string, object?[], string> formatText)
    {
        _startupRegistryValueName = startupRegistryValueName;
        _getText = getText;
        _formatText = formatText;
    }

    public string FormatAutostartStatus(bool startWithWindows)
    {
        if (!startWithWindows)
        {
            return _getText("Ui_HealthAutostartDisabled");
        }

        var registered = TryGetStartupRegistryValue(out var value);
        return registered
            ? Format("Ui_HealthAutostartEnabled", value)
            : _getText("Ui_HealthAutostartMismatch");
    }

    public string FormatUpdateStatus(
        bool automaticallyCheckForUpdates,
        bool automaticallyInstallUpdatesOnStartup,
        string updateStatusText)
    {
        var mode = automaticallyCheckForUpdates
            ? automaticallyInstallUpdatesOnStartup
                ? _getText("Ui_HealthUpdateAutoInstall")
                : _getText("Ui_HealthUpdateAutoCheck")
            : _getText("Ui_HealthUpdateManual");
        return $"{mode} {updateStatusText}";
    }

    public string FormatFeedbackStatus(string fallbackStatus, FeedbackDeliverySnapshot? lastDelivery) =>
        lastDelivery is null
            ? fallbackStatus
            : Format("Ui_HealthFeedbackLastDelivery", lastDelivery.TimestampText, lastDelivery.Status);

    public string FormatStorageStatus(string storagePath)
    {
        var directory = ResolveStorageDirectory(storagePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return _getText("Ui_HealthStorageUnknown");
        }

        try
        {
            Directory.CreateDirectory(directory);
            return Format("Ui_HealthStorageAvailable", directory);
        }
        catch (Exception error)
        {
            return Format("Ui_HealthStorageUnavailable", error.Message);
        }
    }

    public string FormatVersionStatus(string currentVersionText) =>
        Format("Ui_HealthVersionStatus", currentVersionText);

    public string FormatLastGlobalWatchScan(DateTimeOffset? lastSuccessfulScanAt) =>
        lastSuccessfulScanAt is null
            ? _getText("Ui_HealthNoGlobalWatchScan")
            : Format("Ui_HealthLastGlobalWatchScan", lastSuccessfulScanAt.Value.ToLocalTime());

    public static string ResolveStorageDirectory(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return string.Empty;
        }

        try
        {
            var path = Path.GetFullPath(storagePath);
            return Path.HasExtension(path)
                ? Path.GetDirectoryName(path) ?? string.Empty
                : path;
        }
        catch
        {
            return string.Empty;
        }
    }

    private bool TryGetStartupRegistryValue(out string value)
    {
        value = string.Empty;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            value = key?.GetValue(_startupRegistryValueName) as string ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    private string Format(string key, params object?[] args) => _formatText(key, args);
}

public sealed record FeedbackDeliverySnapshot(string TimestampText, string Status);
