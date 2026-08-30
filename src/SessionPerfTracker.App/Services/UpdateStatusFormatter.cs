using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.App.Services;

public sealed class UpdateStatusFormatter
{
    private readonly Func<string, string> _getText;
    private readonly Func<string, object?[], string> _formatText;

    public UpdateStatusFormatter(
        Func<string, string> getText,
        Func<string, object?[], string> formatText)
    {
        _getText = getText;
        _formatText = formatText;
    }

    public string FormatCheckStatus(UpdateCheckResult result)
    {
        if (result.IsUpdateAvailable && result.Manifest is not null)
        {
            var shaStatus = HasSha256(result.Manifest)
                ? _getText("Ui_UpdateShaAvailable")
                : _getText("Ui_UpdateShaMissingDownloadBlocked");
            return Format(
                "Ui_UpdateAvailableStatus",
                result.CurrentVersion,
                result.Manifest.Version,
                shaStatus);
        }

        var serviceStatus = FormatServiceStatus(result.Status);
        if (!string.Equals(serviceStatus, result.Status, StringComparison.Ordinal))
        {
            return serviceStatus;
        }

        if (!string.IsNullOrWhiteSpace(result.LatestVersion))
        {
            return Format("Ui_UpdateUpToDateStatus", result.CurrentVersion);
        }

        return serviceStatus;
    }

    public string FormatServiceStatus(string status)
    {
        if (string.Equals(status, "Update manifest URL is not configured.", StringComparison.Ordinal))
        {
            return _getText("Ui_UpdateManifestUrlNotConfigured");
        }

        if (string.Equals(status, "Update manifest is missing version or installerUrl.", StringComparison.Ordinal))
        {
            return _getText("Ui_UpdateManifestMissingFields");
        }

        if (status.StartsWith("Latest version ", StringComparison.OrdinalIgnoreCase)
            && status.EndsWith(" found, but version comparison was not possible.", StringComparison.OrdinalIgnoreCase))
        {
            return Format("Ui_UpdateVersionComparisonFailed", status);
        }

        if (status.StartsWith("Update check failed: ", StringComparison.OrdinalIgnoreCase))
        {
            return Format("Ui_UpdateCheckFailed", FormatError(status["Update check failed: ".Length..]));
        }

        return status;
    }

    public string FormatError(Exception error) => FormatError(error.Message);

    public string FormatError(string message)
    {
        if (message.Contains("must include a SHA256 hash", StringComparison.OrdinalIgnoreCase))
        {
            return _getText("Ui_UpdateErrorMissingSha256");
        }

        if (message.Contains("invalid SHA256 hash", StringComparison.OrdinalIgnoreCase))
        {
            return _getText("Ui_UpdateErrorInvalidSha256");
        }

        if (message.Contains("failed SHA256 verification", StringComparison.OrdinalIgnoreCase))
        {
            return _getText("Ui_UpdateErrorSha256Mismatch");
        }

        if (message.Contains("installer URL", StringComparison.OrdinalIgnoreCase))
        {
            return _getText("Ui_UpdateErrorInstallerUrlMissing");
        }

        return message;
    }

    public static bool HasSha256(UpdateManifest manifest) =>
        !string.IsNullOrWhiteSpace(manifest.Sha256);

    private string Format(string key, params object?[] args) => _formatText(key, args);
}
