using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using SessionPerfTracker.Domain.Metrics;
using SessionPerfTracker.App.Localization;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace SessionPerfTracker.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string FeedbackNtfyTopic = "spt-grollex-feedback-8f3d7c2a51b44e6ca9d0";
    private const string StartupRegistryValueName = "Session Perf Tracker";
    private static readonly Uri FeedbackNtfyUri = new($"https://ntfy.sh/{FeedbackNtfyTopic}");
    private static readonly HttpClient FeedbackHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

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
                    MinimizeToTrayOnClose = MinimizeToTrayOnClose,
                    StartWithWindows = StartWithWindows,
                    StartMinimizedToTray = StartMinimizedToTray
                }
            },
            cancellationToken);

        ApplyStartupRegistration(_thresholdSettingsStore.Current.Behavior);
        ThresholdSettingsStatusText = MinimizeToTrayOnClose
            ? "Close button will keep Session Perf Tracker running in the tray."
            : "Close button will exit Session Perf Tracker.";
    }

    public async Task DismissTrustExplainerAsync(CancellationToken cancellationToken = default)
    {
        await _thresholdSettingsStore.SaveAsync(
            _thresholdSettingsStore.Current with
            {
                Behavior = _thresholdSettingsStore.Current.Behavior with
                {
                    TrustExplainerDismissed = true
                }
            },
            cancellationToken);

        TrustExplainerDismissed = true;
    }

    public async Task SaveLanguageSettingsAsync(CancellationToken cancellationToken = default)
    {
        var languageCode = SelectedLanguageOption?.LanguageCode ?? AppLanguageSettings.DefaultLanguageCode;
        languageCode = LocalizationManager.NormalizeLanguageCode(languageCode);
        var previousLanguageCode = LocalizationManager.NormalizeLanguageCode(_thresholdSettingsStore.Current.Language.LanguageCode);
        await _thresholdSettingsStore.SaveAsync(
            _thresholdSettingsStore.Current with
            {
                Language = _thresholdSettingsStore.Current.Language with { LanguageCode = languageCode }
            },
            cancellationToken);

        ApplyLanguageSettingsToUi(_thresholdSettingsStore.Current.Language);
        if (languageCode.Equals(previousLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            LanguageSettingsStatusText = GetText("Ui_LanguageAlreadySelected");
            return;
        }

        LanguageSettingsStatusText = GetText("Ui_LanguageSavedRestarting");
        LanguageRestartRequested?.Invoke(this, EventArgs.Empty);
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
                Updates = _thresholdSettingsStore.Current.Updates,
                Behavior = _thresholdSettingsStore.Current.Behavior
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

    public async Task SaveBugReportAsync(
        string? screenshotPath = null,
        CancellationToken cancellationToken = default)
    {
        var feedbackText = BugReportText.Trim();
        var path = await SaveFeedbackAsync("bug-report", BugReportText, screenshotPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
        {
            FeedbackStatusText = "Describe the bug before saving a report.";
            return;
        }

        var delivery = await TrySendFeedbackAsync(
            "Bug report",
            "warning,bug",
            "bug-report",
            feedbackText,
            screenshotPath,
            cancellationToken);

        AddFeedbackDeliveryHistory("Bug report", delivery.Sent, path, delivery.Error);
        BugReportText = string.Empty;
        FeedbackStatusText = delivery.Sent
            ? $"Bug report saved and sent: {path}"
            : $"Bug report saved locally, but could not be sent: {path}";
    }

    public async Task SaveFeatureFeedbackAsync(
        string? screenshotPath = null,
        CancellationToken cancellationToken = default)
    {
        var feedbackText = FeatureFeedbackText.Trim();
        var path = await SaveFeedbackAsync("feature-idea", FeatureFeedbackText, screenshotPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
        {
            FeedbackStatusText = "Write an idea or suggestion before saving feedback.";
            return;
        }

        var delivery = await TrySendFeedbackAsync(
            "Feature idea",
            "bulb,sparkles",
            "feature-idea",
            feedbackText,
            screenshotPath,
            cancellationToken);

        AddFeedbackDeliveryHistory("Feature idea", delivery.Sent, path, delivery.Error);
        FeatureFeedbackText = string.Empty;
        FeedbackStatusText = delivery.Sent
            ? $"Feature idea saved and sent: {path}"
            : $"Feature idea saved locally, but could not be sent: {path}";
    }

    public Task OpenFeedbackDirectoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = GetFeedbackDirectory();
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
        FeedbackStatusText = $"Opened feedback folder: {directory}";
        return Task.CompletedTask;
    }

    public Task OpenLatestFeedbackReportAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var latest = FindLatestFeedbackReport();
        if (latest is null)
        {
            FeedbackStatusText = "No saved feedback report found.";
            return Task.CompletedTask;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = latest.FullName,
            UseShellExecute = true
        });
        FeedbackStatusText = $"Opened latest feedback report: {latest.FullName}";
        return Task.CompletedTask;
    }

    public async Task CopyLatestFeedbackReportAsync(CancellationToken cancellationToken = default)
    {
        var latest = FindLatestFeedbackReport();
        if (latest is null)
        {
            FeedbackStatusText = "No saved feedback report found.";
            return;
        }

        var text = await File.ReadAllTextAsync(latest.FullName, cancellationToken);
        Clipboard.SetText(text);
        FeedbackStatusText = $"Copied latest feedback report: {latest.FullName}";
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
        StartWithWindows = settings.Behavior.StartWithWindows;
        StartMinimizedToTray = settings.Behavior.StartMinimizedToTray;
        TrustExplainerDismissed = settings.Behavior.TrustExplainerDismissed;
        ApplyLanguageSettingsToUi(settings.Language);
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
                profileNames.TryGetValue(assignment.Value, out var profileName)
                    ? UiText.ProfileName(assignment.Value, profileName)
                    : assignment.Value)));
        SelectedAppProfileAssignment = AppProfileAssignments.FirstOrDefault();
        RefreshAssignedTargets();
        RefreshRecommendationCollections();
        RefreshGlobalWatchJournalCollection();
        RefreshSuspiciousWatchCollections();
        NotifyTargetSelectionProperties();
    }

    private void ApplyLanguageSettingsToUi(AppLanguageSettings settings)
    {
        var languageCode = LocalizationManager.NormalizeLanguageCode(settings.LanguageCode);
        SelectedLanguageOption = LanguageOptions.FirstOrDefault(option =>
            option.LanguageCode.Equals(languageCode, StringComparison.OrdinalIgnoreCase))
            ?? LanguageOptions.FirstOrDefault();
        LanguageSettingsStatusText = FormatText(
            "Ui_ApplicationLanguageStatus",
            SelectedLanguageOption?.DisplayName ?? languageCode);
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
        AutomaticallyInstallUpdatesOnStartup = updateSettings.AutomaticallyInstallUpdatesOnStartup;
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
        AutomaticallyInstallUpdatesOnStartup = AutomaticallyInstallUpdatesOnStartup,
        ManifestUrl = string.IsNullOrWhiteSpace(UpdateManifestUrlText)
            ? null
            : UpdateManifestUrlText.Trim(),
        LastCheckedAt = lastCheckedAt,
        SkippedVersion = _thresholdSettingsStore.Current.Updates.SkippedVersion
    };

    private void ApplyStartupRegistration(AppBehaviorSettings behavior)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                writable: true);
            if (key is null)
            {
                ThresholdSettingsStatusText = "Could not open Windows startup registry key.";
                return;
            }

            if (!behavior.StartWithWindows)
            {
                key.DeleteValue(StartupRegistryValueName, throwOnMissingValue: false);
                return;
            }

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                ThresholdSettingsStatusText = "Could not resolve executable path for Windows startup.";
                return;
            }

            var args = behavior.StartMinimizedToTray ? " --background" : string.Empty;
            key.SetValue(StartupRegistryValueName, $"\"{executablePath}\"{args}", RegistryValueKind.String);
        }
        catch (Exception error)
        {
            ThresholdSettingsStatusText = $"Windows startup registration failed: {error.Message}";
        }
    }

    private async Task<string?> SaveFeedbackAsync(
        string kind,
        string text,
        string? screenshotPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var directory = GetFeedbackDirectory();
        Directory.CreateDirectory(directory);
        var timestamp = DateTimeOffset.Now;
        var fileName = $"{timestamp:yyyyMMdd_HHmmss}_{kind}.md";
        var path = Path.Combine(directory, fileName);
        var content = BuildFeedbackReport(
            kind,
            text.Trim(),
            timestamp,
            includePrivateSystemInfo: true,
            screenshotPath: screenshotPath,
            includeLatestSession: IncludeLatestSessionInFeedback);
        await File.WriteAllTextAsync(path, content, cancellationToken);
        return path;
    }

    private async Task<FeedbackDeliveryResult> TrySendFeedbackAsync(
        string title,
        string tags,
        string kind,
        string text,
        string? screenshotPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var report = BuildFeedbackReport(
                kind,
                text,
                DateTimeOffset.Now,
                includePrivateSystemInfo: false,
                screenshotPath: screenshotPath is null ? null : Path.GetFileName(screenshotPath),
                includeLatestSession: IncludeLatestSessionInFeedback);
            using var request = new HttpRequestMessage(HttpMethod.Post, FeedbackNtfyUri)
            {
                Content = new StringContent(report, Encoding.UTF8, "text/plain")
            };
            request.Headers.TryAddWithoutValidation("Title", $"Session Perf Tracker: {title}");
            request.Headers.TryAddWithoutValidation("Tags", tags);
            request.Headers.TryAddWithoutValidation("Priority", kind == "bug-report" ? "4" : "3");
            request.Headers.TryAddWithoutValidation("Markdown", "yes");

            using var response = await FeedbackHttpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new FeedbackDeliveryResult(false, $"Remote service returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            if (string.IsNullOrWhiteSpace(screenshotPath))
            {
                return new FeedbackDeliveryResult(true, null);
            }

            var attachment = await TrySendFeedbackAttachmentAsync(screenshotPath, kind, cancellationToken);
            return attachment.Sent
                ? new FeedbackDeliveryResult(true, null)
                : attachment;
        }
        catch (Exception error)
        {
            return new FeedbackDeliveryResult(false, error.Message);
        }
    }

    private static async Task<FeedbackDeliveryResult> TrySendFeedbackAttachmentAsync(
        string screenshotPath,
        string kind,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(screenshotPath))
            {
                return new FeedbackDeliveryResult(false, "Screenshot attachment file was not found.");
            }

            await using var stream = File.OpenRead(screenshotPath);
            using var request = new HttpRequestMessage(HttpMethod.Put, FeedbackNtfyUri)
            {
                Content = new StreamContent(stream)
            };
            request.Headers.TryAddWithoutValidation("Title", $"Session Perf Tracker: {kind} screenshot");
            request.Headers.TryAddWithoutValidation("Tags", "camera");
            request.Headers.TryAddWithoutValidation("Priority", "3");
            request.Headers.TryAddWithoutValidation("Filename", Path.GetFileName(screenshotPath));
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

            using var response = await FeedbackHttpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? new FeedbackDeliveryResult(true, null)
                : new FeedbackDeliveryResult(false, $"Screenshot upload returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (Exception error)
        {
            return new FeedbackDeliveryResult(false, error.Message);
        }
    }

    private void AddFeedbackDeliveryHistory(string kind, bool sent, string localPath, string? error)
    {
        FeedbackDeliveryHistory.Insert(
            0,
            new FeedbackDeliveryHistoryItemViewModel(DateTimeOffset.Now, kind, sent, localPath, error));

        while (FeedbackDeliveryHistory.Count > 8)
        {
            FeedbackDeliveryHistory.RemoveAt(FeedbackDeliveryHistory.Count - 1);
        }

        OnPropertyChanged(nameof(HasFeedbackDeliveryHistory));
    }

    private string BuildFeedbackReport(
        string kind,
        string text,
        DateTimeOffset timestamp,
        bool includePrivateSystemInfo,
        string? screenshotPath,
        bool includeLatestSession)
    {
        var lines = new List<string>
        {
            $"# Session Perf Tracker {kind}",
            "",
            $"Created: {timestamp:yyyy-MM-dd HH:mm:ss zzz}",
            $"Version: {CurrentVersionText}",
            $"OS: {Environment.OSVersion}",
            $"Storage: {StoragePath}",
            $"Selected tab: {SelectedTabIndex}",
            $"Recording: {IsRecording}",
            $"Live target: {LiveSessionTargetText}",
            $"Live status: {LiveSessionStateText}",
            $"Capture: {LiveConfigCollectorsText}",
            $"Session profile: {ActiveThresholdSourceText}",
            $"Storage status: {StorageStatusText}",
            $"Export status: {ExportStatusText}",
            $"Update status: {UpdateStatusText}",
            "",
            "## User text",
            "",
            text,
            "",
            "## Recent live warning",
            "",
            string.IsNullOrWhiteSpace(LiveWarningText) ? "None" : LiveWarningText
        };

        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            lines.Add("");
            lines.Add("## Screenshot");
            lines.Add("");
            lines.Add(screenshotPath);
        }

        if (includeLatestSession && _allSessionItems.FirstOrDefault() is { } latestSession)
        {
            lines.Add("");
            lines.Add("## Latest session summary");
            lines.Add("");
            AddLatestSessionSummary(lines, latestSession.Session);
        }

        if (includePrivateSystemInfo)
        {
            lines.Insert(6, $"User: {Environment.UserName}");
            lines.Insert(6, $"Machine: {Environment.MachineName}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AddLatestSessionSummary(List<string> lines, SessionRecord session)
    {
        lines.Add($"Target: {session.Target.DisplayName}");
        lines.Add($"Started: {session.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        lines.Add($"Duration: {(int)session.Summary.Duration.TotalMinutes}m {session.Summary.Duration.Seconds:00}s");
        lines.Add($"Status: {session.Status}");
        lines.Add($"Events: {session.Summary.EventCount}");
        lines.Add($"Spikes: {session.Summary.SpikeCount}");
        lines.Add($"Breaches: {session.Summary.ThresholdBreachCount}");
        lines.Add($"Stability: {session.Summary.StabilityStatus}");
        if (!string.IsNullOrWhiteSpace(session.Summary.StabilityReason))
        {
            lines.Add($"Stability reason: {session.Summary.StabilityReason}");
        }

        foreach (var metric in session.Summary.Metrics.Take(6))
        {
            lines.Add($"{metric.Label}: avg {metric.Avg:N1} {metric.Unit}, max {metric.Max:N1} {metric.Unit}, breaches {metric.ThresholdBreaches}");
        }
    }

    private static string GetFeedbackDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SessionPerfTracker",
            "feedback");

    private static FileInfo? FindLatestFeedbackReport()
    {
        var directory = GetFeedbackDirectory();
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return new DirectoryInfo(directory)
            .EnumerateFiles("*.md", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
    }

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

    private async Task AutoInstallUpdatesOnStartupAsync(
        AppUpdateSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.AutomaticallyCheckForUpdates
            || !settings.AutomaticallyInstallUpdatesOnStartup
            || string.IsNullOrWhiteSpace(settings.ManifestUrl))
        {
            await AutoCheckForUpdatesIfNeededAsync(settings, cancellationToken);
            return;
        }

        if (IsCheckingForUpdates)
        {
            return;
        }

        try
        {
            IsCheckingForUpdates = true;
            DownloadedUpdateInstallerPath = string.Empty;
            UpdateStatusText = "Checking for startup update...";
            var result = await _updateService.CheckAsync(settings.ManifestUrl.Trim(), cancellationToken);
            _availableUpdateManifest = result.IsUpdateAvailable ? result.Manifest : null;
            IsUpdateAvailable = result.IsUpdateAvailable && result.Manifest is not null;
            UpdateLatestVersionText = result.LatestVersion ?? "Unavailable";
            UpdateReleaseNotesText = string.IsNullOrWhiteSpace(result.Manifest?.ReleaseNotes)
                ? "No release notes in manifest."
                : result.Manifest.ReleaseNotes;
            UpdateStatusText = result.Status;

            await _thresholdSettingsStore.SaveAsync(
                _thresholdSettingsStore.Current with
                {
                    Updates = ReadUpdateSettingsFromUi(DateTimeOffset.UtcNow)
                },
                cancellationToken);

            if (!result.IsUpdateAvailable || result.Manifest is null)
            {
                return;
            }

            var latestVersion = result.Manifest.Version.Trim().TrimStart('v', 'V');
            var skippedVersion = _thresholdSettingsStore.Current.Updates.SkippedVersion?.Trim().TrimStart('v', 'V');
            if (!string.IsNullOrWhiteSpace(skippedVersion)
                && string.Equals(latestVersion, skippedVersion, StringComparison.OrdinalIgnoreCase))
            {
                _availableUpdateManifest = null;
                IsUpdateAvailable = false;
                UpdateStatusText = $"Version {latestVersion} is skipped. Startup auto-install will wait for a newer version.";
                return;
            }

            IsCheckingForUpdates = false;
            await DownloadAndLaunchUpdateAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "Startup update check cancelled.";
        }
        catch (Exception error)
        {
            UpdateStatusText = $"Startup update failed: {error.Message}";
        }
        finally
        {
            if (!IsUpdateRestartRequested)
            {
                IsCheckingForUpdates = false;
            }
        }
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

    private sealed record FeedbackDeliveryResult(bool Sent, string? Error);
}
