using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Domain.Services;

public enum StartupUpdateDecisionKind
{
    CheckOnly,
    NoUpdate,
    Skipped,
    Install
}

public sealed record StartupUpdateDecision(
    StartupUpdateDecisionKind Kind,
    string? LatestVersion = null);

public static class AppUpdatePolicy
{
    public static StartupUpdateDecision DecideStartupUpdate(
        AppUpdateSettings settings,
        UpdateCheckResult result)
    {
        if (!settings.AutomaticallyCheckForUpdates
            || !settings.AutomaticallyInstallUpdatesOnStartup
            || string.IsNullOrWhiteSpace(settings.ManifestUrl))
        {
            return new StartupUpdateDecision(StartupUpdateDecisionKind.CheckOnly);
        }

        if (!result.IsUpdateAvailable || result.Manifest is null)
        {
            return new StartupUpdateDecision(StartupUpdateDecisionKind.NoUpdate);
        }

        var latestVersion = NormalizeVersion(result.Manifest.Version);
        return IsSkippedVersion(latestVersion, settings.SkippedVersion)
            ? new StartupUpdateDecision(StartupUpdateDecisionKind.Skipped, latestVersion)
            : new StartupUpdateDecision(StartupUpdateDecisionKind.Install, latestVersion);
    }

    public static bool IsSkippedVersion(string? latestVersion, string? skippedVersion)
    {
        var latest = NormalizeVersion(latestVersion);
        var skipped = NormalizeVersion(skippedVersion);
        return !string.IsNullOrWhiteSpace(latest)
            && !string.IsNullOrWhiteSpace(skipped)
            && string.Equals(latest, skipped, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersion(string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? string.Empty
            : version.Trim().TrimStart('v', 'V');
}
