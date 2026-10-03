using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SessionPerfTracker.App.Services;
using SessionPerfTracker.Domain.Metrics;
using SessionPerfTracker.App.Localization;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Domain.Services;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace SessionPerfTracker.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string StartupRegistryValueName = "Session Perf Tracker";

    public async Task SaveThresholdSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!TryReadGlobalThresholds(out var globalLimits))
        {
            ThresholdSettingsStatusText = GetText("Ui_StGlobalLimitsInvalid");
            return;
        }

        await _thresholdSettingsStore.UpdateAsync(settings => settings with
        {
            CpuThresholdPercent = globalLimits.CpuThresholdPercent,
            RamThresholdMb = globalLimits.RamThresholdMb,
            DiskReadThresholdMbPerSec = globalLimits.DiskReadThresholdMbPerSec,
            DiskWriteThresholdMbPerSec = globalLimits.DiskWriteThresholdMbPerSec,
            GpuThresholdPercent = globalLimits.EffectiveGpuThresholdPercent
        }, cancellationToken);
        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current);
        ThresholdSettingsStatusText = GetText("Ui_StGlobalFallbackSaved");
    }

    public string HealthAutostartStatusText =>
        _healthStatusCoordinator.FormatAutostartStatus(StartWithWindows);

    public string HealthUpdateStatusText =>
        _healthStatusCoordinator.FormatUpdateStatus(
            AutomaticallyCheckForUpdates,
            AutomaticallyInstallUpdatesOnStartup,
            UpdateStatusText);

    public string HealthFeedbackStatusText
    {
        get
        {
            var delivery = FeedbackDeliveryHistory.FirstOrDefault();
            return _healthStatusCoordinator.FormatFeedbackStatus(
                FeedbackStatusText,
                delivery is null ? null : new FeedbackDeliverySnapshot(delivery.TimestampText, delivery.Status));
        }
    }

    public string HealthStorageStatusText =>
        _healthStatusCoordinator.FormatStorageStatus(StoragePath);

    public string HealthVersionStatusText =>
        _healthStatusCoordinator.FormatVersionStatus(CurrentVersionText);

    public string HealthLastGlobalWatchScanText =>
        _healthStatusCoordinator.FormatLastGlobalWatchScan(_lastSuccessfulGlobalWatchScanAt);

    private void NotifyHealthCheckProperties()
    {
        OnPropertyChanged(nameof(HealthAutostartStatusText));
        OnPropertyChanged(nameof(HealthUpdateStatusText));
        OnPropertyChanged(nameof(HealthFeedbackStatusText));
        OnPropertyChanged(nameof(HealthStorageStatusText));
        OnPropertyChanged(nameof(HealthVersionStatusText));
        OnPropertyChanged(nameof(HealthLastGlobalWatchScanText));
    }

    public async Task SaveAntiNoiseSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!TryReadAntiNoiseSettings(out var antiNoise))
        {
            ThresholdSettingsStatusText = GetText("Ui_StAntiNoiseInvalid");
            return;
        }

        await _thresholdSettingsStore.UpdateAsync(
            settings => settings with { AntiNoise = antiNoise },
            cancellationToken);

        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
        ThresholdSettingsStatusText = GetText("Ui_StAntiNoiseSaved");
    }

    public async Task SaveCaptureSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _thresholdSettingsStore.UpdateAsync(
            settings => settings with { Capture = ReadCaptureSettingsFromUi() },
            cancellationToken);

        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
        ThresholdSettingsStatusText = GetText("Ui_StCaptureScopeSaved");
    }

    public async Task SaveAppBehaviorSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _thresholdSettingsStore.UpdateAsync(
            settings => settings with
            {
                Behavior = settings.Behavior with
                {
                    MinimizeToTrayOnClose = MinimizeToTrayOnClose,
                    StartWithWindows = StartWithWindows,
                    StartMinimizedToTray = StartMinimizedToTray
                }
            },
            cancellationToken);

        ApplyStartupRegistration(_thresholdSettingsStore.Current.Behavior);
        ThresholdSettingsStatusText = MinimizeToTrayOnClose
            ? GetText("Ui_StCloseToTray")
            : GetText("Ui_StCloseExits");
    }

    public async Task DismissTrustExplainerAsync(CancellationToken cancellationToken = default)
    {
        await _thresholdSettingsStore.UpdateAsync(
            settings => settings with
            {
                Behavior = settings.Behavior with
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
        await _thresholdSettingsStore.UpdateAsync(
            settings => settings with
            {
                Language = settings.Language with { LanguageCode = languageCode }
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

    public Task RequestSelfUninstallAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uninstaller = _selfUninstallService.FindUninstaller();
        if (uninstaller is null)
        {
            SelfUninstallStatusText = GetText("Ui_SelfUninstallUnavailable");
            return Task.CompletedTask;
        }

        var handler = SelfUninstallConfirmationRequested;
        if (handler is null)
        {
            SelfUninstallStatusText = GetText("Ui_SelfUninstallUnavailable");
            return Task.CompletedTask;
        }

        var confirmation = new SelfUninstallConfirmationEventArgs(
            GetText("Ui_SelfUninstallConfirmTitle"),
            FormatText("Ui_SelfUninstallConfirmMessage", uninstaller.FullName));
        handler(this, confirmation);
        if (!confirmation.IsConfirmed)
        {
            SelfUninstallStatusText = GetText("Ui_SelfUninstallCancelled");
            return Task.CompletedTask;
        }

        try
        {
            _selfUninstallService.LaunchUninstaller(uninstaller);
            SelfUninstallStatusText = GetText("Ui_SelfUninstallLaunched");
            SelfUninstallLaunched?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            SelfUninstallStatusText = FormatText("Ui_SelfUninstallFailed", error.Message);
        }

        return Task.CompletedTask;
    }

    public async Task SaveSelectedProfileAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedThresholdProfile is null)
        {
            ThresholdSettingsStatusText = GetText("Ui_StSelectProfileFirst");
            return;
        }

        if (!TryReadProfileThresholds(out var profileLimits))
        {
            ThresholdSettingsStatusText = GetText("Ui_StProfileLimitsInvalid");
            return;
        }

        var selectedProfileId = SelectedThresholdProfile.Id;

        await _thresholdSettingsStore.UpdateAsync(
            settings => settings with
            {
                SelectedProfileId = selectedProfileId,
                Profiles = settings.Profiles
                    .Select(profile => string.Equals(profile.Id, selectedProfileId, StringComparison.OrdinalIgnoreCase)
                        ? profile with { Limits = profileLimits }
                        : profile)
                    .ToList()
            },
            cancellationToken);

        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, selectedProfileId);
        ThresholdSettingsStatusText = $"{SelectedThresholdProfile.Name} profile saved.";
    }

    public async Task SaveAppProfileAssignmentAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedAssignmentProfile is null)
        {
            ThresholdSettingsStatusText = GetText("Ui_StSelectProfileForAssignment");
            return;
        }

        var exeName = NormalizeExeNameForAssignment(AssignmentExeNameText);
        if (string.IsNullOrWhiteSpace(exeName))
        {
            ThresholdSettingsStatusText = GetText("Ui_StEnterExeName");
            return;
        }

        var profileId = SelectedAssignmentProfile.Id;
        await _thresholdSettingsStore.UpdateAsync(
            settings =>
            {
                var assignments = new Dictionary<string, string>(settings.AppProfileAssignments, StringComparer.OrdinalIgnoreCase)
                {
                    [exeName] = profileId
                };
                return settings with { AppProfileAssignments = assignments };
            },
            cancellationToken);

        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
        AssignmentExeNameText = exeName;
        ThresholdSettingsStatusText = $"{exeName} now uses {SelectedAssignmentProfile.Name}.";
    }

    public async Task AssignCurrentTargetProfileAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedLiveAssignmentProfile is null)
        {
            LiveAssignmentStatusText = GetText("Ui_StSelectProfileForTarget");
            return;
        }

        try
        {
            var exeName = NormalizeExeNameForAssignment(await ResolveCurrentTargetExeNameAsync(cancellationToken));
            if (string.IsNullOrWhiteSpace(exeName))
            {
                LiveAssignmentStatusText = GetText("Ui_StTargetExeUnknown");
                return;
            }

            var profileId = SelectedLiveAssignmentProfile.Id;
            var profileName = SelectedLiveAssignmentProfile.Name;
            var hadExistingAssignment = false;
            await _thresholdSettingsStore.UpdateAsync(
                settings =>
                {
                    var assignments = new Dictionary<string, string>(settings.AppProfileAssignments, StringComparer.OrdinalIgnoreCase);
                    hadExistingAssignment = assignments.ContainsKey(exeName);
                    assignments[exeName] = profileId;
                    return settings with { AppProfileAssignments = assignments };
                },
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
            LiveAssignmentStatusText = GetText("Ui_StTargetExeUnknown");
        }
        catch (Exception error)
        {
            LiveAssignmentStatusText = FormatText("Ui_StAssignmentFailed", error.Message);
        }
    }

    public async Task RemoveSelectedAppProfileAssignmentAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedAppProfileAssignment is null)
        {
            ThresholdSettingsStatusText = GetText("Ui_StSelectAssignmentToRemove");
            return;
        }

        var exeName = SelectedAppProfileAssignment.ExeName;
        var removed = false;
        await _thresholdSettingsStore.UpdateAsync(
            settings =>
            {
                var assignments = new Dictionary<string, string>(settings.AppProfileAssignments, StringComparer.OrdinalIgnoreCase);
                removed = assignments.Remove(exeName);
                return removed
                    ? settings with { AppProfileAssignments = assignments }
                    : settings;
            },
            cancellationToken);

        if (!removed)
        {
            ThresholdSettingsStatusText = $"{exeName} is already using global fallback.";
            LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
            return;
        }

        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
        AssignmentExeNameText = exeName;
        ThresholdSettingsStatusText = $"{exeName} assignment removed. It now uses global fallback thresholds.";
    }

    public async Task ResetSelectedProfileAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedThresholdProfile is null)
        {
            ThresholdSettingsStatusText = GetText("Ui_StSelectProfileFirst");
            return;
        }

        var defaultProfile = ThresholdProfileDefaults.CreateProfiles()
            .FirstOrDefault(profile => string.Equals(profile.Id, SelectedThresholdProfile.Id, StringComparison.OrdinalIgnoreCase));
        if (defaultProfile is null)
        {
            ThresholdSettingsStatusText = GetText("Ui_StProfileNoDefault");
            return;
        }

        await _thresholdSettingsStore.UpdateAsync(
            settings => settings with
            {
                SelectedProfileId = defaultProfile.Id,
                Profiles = settings.Profiles
                    .Select(profile => string.Equals(profile.Id, defaultProfile.Id, StringComparison.OrdinalIgnoreCase)
                        ? defaultProfile
                        : profile)
                    .ToList()
            },
            cancellationToken);

        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, defaultProfile.Id);
        ThresholdSettingsStatusText = $"{defaultProfile.Name} reset to default.";
    }

    public async Task ResetAllThresholdSettingsAsync(CancellationToken cancellationToken = default)
    {
        var defaults = ThresholdProfileDefaults.CreateSettings();
        await _thresholdSettingsStore.UpdateAsync(
            settings => defaults with
            {
                AntiNoise = settings.AntiNoise,
                Capture = settings.Capture,
                Retention = settings.Retention,
                Export = settings.Export,
                Recommendations = settings.Recommendations,
                WatchJournal = settings.WatchJournal,
                SuspiciousWatchlist = settings.SuspiciousWatchlist,
                ProcessBans = settings.ProcessBans,
                Updates = settings.Updates,
                Behavior = settings.Behavior,
                Language = settings.Language,
                Feedback = settings.Feedback
            },
            cancellationToken);
        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current);
        ThresholdSettingsStatusText = GetText("Ui_StAllProfilesReset");
    }

    public async Task SaveRetentionSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedRetentionOption is null)
        {
            StorageStatusText = GetText("Ui_StSelectRetentionFirst");
            return;
        }

        var retention = new RetentionSettings { RetentionDays = SelectedRetentionOption.Days };
        await _thresholdSettingsStore.UpdateAsync(
            settings => settings with { Retention = retention },
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
            ExportStatusText = GetText("Ui_StChooseExportFolderFirst");
            return;
        }

        try
        {
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            _exportService.SetExportDirectory(directory);
            await _thresholdSettingsStore.UpdateAsync(
                settings => settings with
                {
                    Export = new ExportSettings { ExportDirectory = directory }
                },
                cancellationToken);
            ExportDirectoryText = directory;
            ExportStatusText = FormatText("Ui_StExportFolderSaved", directory);
            await RefreshExportFilesAsync(cancellationToken, updateStatus: false);
        }
        catch (Exception error)
        {
            ExportStatusText = FormatText("Ui_StExportFolderSaveFailed", error.Message);
        }
    }

    public async Task ResetExportDirectoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(_defaultExportDirectory);
            _exportService.SetExportDirectory(_defaultExportDirectory);
            await _thresholdSettingsStore.UpdateAsync(
                settings => settings with { Export = new ExportSettings() },
                cancellationToken);
            ExportDirectoryText = _defaultExportDirectory;
            ExportStatusText = FormatText("Ui_StExportFolderReset", _defaultExportDirectory);
            await RefreshExportFilesAsync(cancellationToken, updateStatus: false);
        }
        catch (Exception error)
        {
            ExportStatusText = FormatText("Ui_StExportFolderResetFailed", error.Message);
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
                ExportStatusText = FormatText("Ui_ExportFilesShown", ExportFiles.Count);
            }
        }
        catch (Exception error)
        {
            ExportStatusText = FormatText("Ui_StExportsRefreshFailed", error.Message);
        }
    }

    public async Task OpenSelectedExportAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedExportFile is null)
        {
            ExportStatusText = GetText("Ui_StSelectExportFileFirst");
            return;
        }

        try
        {
            await _exportService.OpenExportAsync(SelectedExportFile.Path, cancellationToken);
            ExportStatusText = FormatText("Ui_StOpenedExport", SelectedExportFile.FileName);
        }
        catch (Exception error)
        {
            ExportStatusText = FormatText("Ui_StOpenExportFailed", error.Message);
        }
    }

    public async Task OpenExportDirectoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _exportService.OpenExportDirectoryAsync(cancellationToken);
            ExportStatusText = FormatText("Ui_StOpenedExportFolder", _exportService.ExportDirectory);
        }
        catch (Exception error)
        {
            ExportStatusText = FormatText("Ui_StOpenExportFolderFailed", error.Message);
        }
    }

    public async Task SaveUpdateSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _thresholdSettingsStore.UpdateAsync(
            settings => settings with
            {
                Updates = ReadUpdateSettingsFromUi(settings.Updates.LastCheckedAt, settings.Updates.SkippedVersion)
            },
            cancellationToken);
        ApplyUpdateSettingsToUi(_thresholdSettingsStore.Current.Updates);
        UpdateStatusText = GetText("Ui_UpdateSettingsSaved");
    }

    public async Task SaveBugReportAsync(
        string? screenshotPath = null,
        CancellationToken cancellationToken = default)
    {
        var feedbackText = BugReportText.Trim();
        var context = CreateFeedbackReportContext();
        var feedbackSettings = await SaveFeedbackSettingsCoreAsync(cancellationToken);
        var path = await _feedbackReportService.SaveAsync(
            "bug-report",
            BugReportText,
            screenshotPath,
            context,
            IncludeLatestSessionInFeedback,
            feedbackSettings,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
        {
            FeedbackStatusText = GetText("Ui_BugReportEmpty");
            return;
        }

        var delivery = feedbackSettings.SendRemoteFeedback
            ? await _feedbackReportService.TrySendAsync(
                "Bug report",
                "warning,bug",
                "bug-report",
                feedbackText,
                screenshotPath,
                context,
                IncludeLatestSessionInFeedback,
                cancellationToken)
            : new FeedbackDeliveryResult(false, GetText("Ui_FeedbackRemoteOff"));

        AddFeedbackDeliveryHistory(GetText("Ui_FeedbackKindBugReport"), delivery.Sent, path, delivery.Error);
        BugReportText = string.Empty;
        FeedbackStatusText = delivery.Sent
            ? GetText("Ui_FeedbackThanksSent")
            : feedbackSettings.SendRemoteFeedback
                ? FormatText("Ui_FeedbackThanksSavedOnly", delivery.Error ?? GetText("Ui_StatusUnknown"))
                : GetText("Ui_FeedbackThanksSavedLocalOnly");
    }

    public async Task SaveFeatureFeedbackAsync(
        string? screenshotPath = null,
        CancellationToken cancellationToken = default)
    {
        var feedbackText = FeatureFeedbackText.Trim();
        var context = CreateFeedbackReportContext();
        var feedbackSettings = await SaveFeedbackSettingsCoreAsync(cancellationToken);
        var path = await _feedbackReportService.SaveAsync(
            "feature-idea",
            FeatureFeedbackText,
            screenshotPath,
            context,
            IncludeLatestSessionInFeedback,
            feedbackSettings,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
        {
            FeedbackStatusText = GetText("Ui_FeatureFeedbackEmpty");
            return;
        }

        var delivery = feedbackSettings.SendRemoteFeedback
            ? await _feedbackReportService.TrySendAsync(
                "Feature idea",
                "bulb,sparkles",
                "feature-idea",
                feedbackText,
                screenshotPath,
                context,
                IncludeLatestSessionInFeedback,
                cancellationToken)
            : new FeedbackDeliveryResult(false, GetText("Ui_FeedbackRemoteOff"));

        AddFeedbackDeliveryHistory(GetText("Ui_FeedbackKindFeatureIdea"), delivery.Sent, path, delivery.Error);
        FeatureFeedbackText = string.Empty;
        FeedbackStatusText = delivery.Sent
            ? GetText("Ui_FeedbackThanksSent")
            : feedbackSettings.SendRemoteFeedback
                ? FormatText("Ui_FeedbackThanksSavedOnly", delivery.Error ?? GetText("Ui_StatusUnknown"))
                : GetText("Ui_FeedbackThanksSavedLocalOnly");
    }

    public async Task SaveFeedbackSettingsAsync(CancellationToken cancellationToken = default)
    {
        await SaveFeedbackSettingsCoreAsync(cancellationToken);
        FeedbackStatusText = GetText("Ui_FeedbackSettingsSaved");
    }

    public Task OpenFeedbackDirectoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = _feedbackReportService.GetFeedbackDirectory();
        _feedbackReportService.OpenFeedbackDirectory();
        FeedbackStatusText = FormatText("Ui_FeedbackFolderOpened", directory);
        return Task.CompletedTask;
    }

    public Task OpenLatestFeedbackReportAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var latest = _feedbackReportService.FindLatestReport();
        if (latest is null)
        {
            FeedbackStatusText = GetText("Ui_FeedbackNoSavedReport");
            return Task.CompletedTask;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = latest.FullName,
            UseShellExecute = true
        });
        FeedbackStatusText = FormatText("Ui_FeedbackLatestOpened", latest.FullName);
        return Task.CompletedTask;
    }

    public async Task CopyLatestFeedbackReportAsync(CancellationToken cancellationToken = default)
    {
        var latest = _feedbackReportService.FindLatestReport();
        if (latest is null)
        {
            FeedbackStatusText = GetText("Ui_FeedbackNoSavedReport");
            return;
        }

        var text = await File.ReadAllTextAsync(latest.FullName, cancellationToken);
        Clipboard.SetText(text);
        FeedbackStatusText = FormatText("Ui_FeedbackLatestCopied", latest.FullName);
    }

    public Task CheckForUpdatesAsync(CancellationToken cancellationToken = default) =>
        CheckForUpdatesCoreAsync(automatic: false, cancellationToken);

    public async Task SkipAvailableUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_availableUpdateManifest is null)
        {
            UpdateStatusText = GetText("Ui_UpdateNoVersionToSkip");
            return;
        }

        var skippedVersion = _availableUpdateManifest.Version.Trim().TrimStart('v', 'V');
        await _thresholdSettingsStore.UpdateAsync(
            settings => settings with
            {
                Updates = ReadUpdateSettingsFromUi(settings.Updates.LastCheckedAt, settings.Updates.SkippedVersion) with
                {
                    SkippedVersion = skippedVersion
                }
            },
            cancellationToken);

        _availableUpdateManifest = null;
        IsUpdateAvailable = false;
        UpdateStatusText = FormatText("Ui_UpdateVersionSkipped", skippedVersion);
    }

    public async Task DownloadAndLaunchUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_availableUpdateManifest is null)
        {
            UpdateStatusText = GetText("Ui_UpdateNoDownloadSelected");
            return;
        }

        try
        {
            IsCheckingForUpdates = true;
            UpdateStatusText = GetText("Ui_DownloadingUpdateInstaller");
            var installerPath = await _updateService.DownloadInstallerAsync(
                _availableUpdateManifest,
                _updateDownloadDirectory,
                cancellationToken);
            DownloadedUpdateInstallerPath = installerPath;
            UpdateStatusText = UpdateStatusFormatter.HasSha256(_availableUpdateManifest)
                ? FormatText("Ui_UpdateInstallerDownloadedShaVerified", installerPath)
                : FormatText("Ui_UpdateInstallerDownloadedNoShaLocal", installerPath);
            await _updateService.LaunchInstallerAsync(installerPath, cancellationToken);
            _isUpdateRestartRequested = true;
            UpdateStatusText = GetText("Ui_UpdateInstallerLaunchedExit");
            UpdateInstallerLaunched?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = GetText("Ui_UpdateDownloadCancelled");
        }
        catch (Exception error)
        {
            UpdateStatusText = FormatText("Ui_UpdateInstallLaunchFailed", _updateStatusFormatter.FormatError(error));
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
        StorageStatusText = FormatText("Ui_StDeletedSessions", deleted);
    }

    public async Task DeleteSessionsOlderThanAsync(int days, CancellationToken cancellationToken = default)
    {
        var deleted = await _sessionStore.DeleteSessionsOlderThanAsync(TimeSpan.FromDays(days), cancellationToken);
        await ReloadSessionsAsync(cancellationToken: cancellationToken);
        StorageStatusText = FormatText("Ui_StDeletedOlderSessions", deleted, days);
    }

    public async Task DeleteFilteredSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!HasActiveSessionFilter())
        {
            StorageStatusText = GetText("Ui_StApplyFilterFirst");
            return;
        }

        var ids = Sessions.Select(session => session.Id).ToArray();
        if (ids.Length == 0)
        {
            StorageStatusText = GetText("Ui_StNoFilteredSessions");
            return;
        }

        var deleted = await _sessionStore.DeleteSessionsAsync(ids, cancellationToken);
        await ReloadSessionsAsync(cancellationToken: cancellationToken);
        StorageStatusText = FormatText("Ui_StDeletedFilteredSessions", deleted);
    }

    private void LoadThresholdSettingsIntoUi(CpuRamThresholdSettings settings, string? selectedProfileId = null)
    {
        var liveProfileId = SelectedLiveAssignmentProfile?.Id ?? ThresholdProfileDefaults.BrowsersChatsId;

        CpuThresholdPercentText = settings.CpuThresholdPercent.ToString("N0", CultureInfo.CurrentCulture);
        RamThresholdMbText = settings.RamThresholdMb.ToString("N0", CultureInfo.CurrentCulture);
        DiskReadThresholdMbPerSecText = settings.DiskReadThresholdMbPerSec.ToString("N1", CultureInfo.CurrentCulture);
        DiskWriteThresholdMbPerSecText = settings.DiskWriteThresholdMbPerSec.ToString("N1", CultureInfo.CurrentCulture);
        GpuThresholdPercentText = settings.GpuThresholdPercent.ToString("N0", CultureInfo.CurrentCulture);
        StartupGraceSecondsText = settings.AntiNoise.StartupGraceSeconds.ToString("N0", CultureInfo.CurrentCulture);
        EventCooldownSecondsText = settings.AntiNoise.EventCooldownSeconds.ToString("N0", CultureInfo.CurrentCulture);
        GroupingWindowSecondsText = settings.AntiNoise.GroupingWindowSeconds.ToString("N0", CultureInfo.CurrentCulture);
        SnapshotSuppressionSecondsText = settings.AntiNoise.SnapshotSuppressionSeconds.ToString("N0", CultureInfo.CurrentCulture);
        CaptureCpu = settings.Capture.CaptureCpu;
        CaptureRam = settings.Capture.CaptureRam;
        CaptureDiskRead = settings.Capture.CaptureDiskRead;
        CaptureDiskWrite = settings.Capture.CaptureDiskWrite;
        CaptureGpu = settings.Capture.CaptureGpu;
        MinimizeToTrayOnClose = settings.Behavior.MinimizeToTrayOnClose;
        StartWithWindows = settings.Behavior.StartWithWindows;
        StartMinimizedToTray = settings.Behavior.StartMinimizedToTray;
        TrustExplainerDismissed = settings.Behavior.TrustExplainerDismissed;
        ApplyLanguageSettingsToUi(settings.Language);
        SendRemoteFeedback = settings.Feedback.SendRemoteFeedback;
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
            ExportStatusText = GetText("Ui_StExportFolderFallback");
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
                : " " + FormatText("Ui_UpdateSkippedVersionSuffix", updateSettings.SkippedVersion);
            UpdateStatusText = FormatText("Ui_UpdateLastCheck", lastCheckedAt.ToLocalTime(), skippedText);
        }
    }

    private AppUpdateSettings ReadUpdateSettingsFromUi(
        DateTimeOffset? lastCheckedAt = null,
        string? skippedVersion = null) => new()
    {
        AutomaticallyCheckForUpdates = AutomaticallyCheckForUpdates,
        AutomaticallyInstallUpdatesOnStartup = AutomaticallyInstallUpdatesOnStartup,
        ManifestUrl = string.IsNullOrWhiteSpace(UpdateManifestUrlText)
            ? null
            : UpdateManifestUrlText.Trim(),
        LastCheckedAt = lastCheckedAt,
        SkippedVersion = skippedVersion
    };

    private async Task<FeedbackSettings> SaveFeedbackSettingsCoreAsync(CancellationToken cancellationToken)
    {
        var updated = await _thresholdSettingsStore.UpdateAsync(
            settings => settings with
            {
                Feedback = settings.Feedback with { SendRemoteFeedback = SendRemoteFeedback }
            },
            cancellationToken);
        SendRemoteFeedback = updated.Feedback.SendRemoteFeedback;
        return updated.Feedback;
    }

    private void ApplyStartupRegistration(AppBehaviorSettings behavior)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                writable: true);
            if (key is null)
            {
                ThresholdSettingsStatusText = GetText("Ui_StStartupKeyFailed");
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
                ThresholdSettingsStatusText = GetText("Ui_StStartupPathFailed");
                return;
            }

            var args = behavior.StartMinimizedToTray ? " --background" : string.Empty;
            key.SetValue(StartupRegistryValueName, $"\"{executablePath}\"{args}", RegistryValueKind.String);
        }
        catch (Exception error)
        {
            ThresholdSettingsStatusText = FormatText("Ui_StStartupFailed", error.Message);
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
        NotifyHealthCheckProperties();
    }

    private FeedbackReportContext CreateFeedbackReportContext() => new(
        CurrentVersionText,
        StoragePath,
        SelectedTabIndex,
        IsRecording,
        LiveSessionTargetText,
        LiveSessionStateText,
        LiveConfigCollectorsText,
        ActiveThresholdSourceText,
        StorageStatusText,
        ExportStatusText,
        UpdateStatusText,
        LiveWarningText,
        _allSessionItems.FirstOrDefault()?.Session);

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
            UpdateStatusText = GetText("Ui_UpdateCheckingStartup");
            var result = await _updateService.CheckAsync(settings.ManifestUrl.Trim(), cancellationToken);
            _availableUpdateManifest = result.IsUpdateAvailable ? result.Manifest : null;
            IsUpdateAvailable = result.IsUpdateAvailable && result.Manifest is not null;
            ApplyUpdateCheckResult(result);

            await _thresholdSettingsStore.UpdateAsync(
                settings => settings with
                {
                    Updates = ReadUpdateSettingsFromUi(DateTimeOffset.UtcNow, settings.Updates.SkippedVersion)
                },
                cancellationToken);

            if (!result.IsUpdateAvailable || result.Manifest is null)
            {
                return;
            }

            var startupDecision = AppUpdatePolicy.DecideStartupUpdate(_thresholdSettingsStore.Current.Updates, result);
            if (startupDecision.Kind == StartupUpdateDecisionKind.Skipped)
            {
                _availableUpdateManifest = null;
                IsUpdateAvailable = false;
                UpdateStatusText = FormatText("Ui_UpdateStartupSkipped", startupDecision.LatestVersion ?? result.Manifest.Version);
                return;
            }

            IsCheckingForUpdates = false;
            await DownloadAndLaunchUpdateAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = GetText("Ui_UpdateStartupCancelled");
        }
        catch (Exception error)
        {
            UpdateStatusText = FormatText("Ui_UpdateStartupFailed", _updateStatusFormatter.FormatError(error));
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
            UpdateStatusText = automatic ? GetText("Ui_UpdateCheckingAutomatic") : GetText("Ui_UpdateCheckingManual");
            DownloadedUpdateInstallerPath = string.Empty;
            var result = await _updateService.CheckAsync(UpdateManifestUrlText.Trim(), cancellationToken);
            _availableUpdateManifest = result.IsUpdateAvailable ? result.Manifest : null;
            IsUpdateAvailable = result.IsUpdateAvailable && result.Manifest is not null;
            ApplyUpdateCheckResult(result);

            if (result.IsUpdateAvailable && result.Manifest is not null)
            {
                var latestVersion = result.Manifest.Version.Trim().TrimStart('v', 'V');
                if (automatic
                    && AppUpdatePolicy.IsSkippedVersion(latestVersion, _thresholdSettingsStore.Current.Updates.SkippedVersion))
                {
                    _availableUpdateManifest = null;
                    IsUpdateAvailable = false;
                    UpdateStatusText = FormatText("Ui_UpdateAutomaticSkipped", latestVersion);
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
                        UpdateStatusText = FormatText("Ui_UpdateAvailableLater", latestVersion);
                    }
                }
            }

            await _thresholdSettingsStore.UpdateAsync(
                settings => settings with
                {
                    Updates = ReadUpdateSettingsFromUi(DateTimeOffset.UtcNow, settings.Updates.SkippedVersion)
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = GetText("Ui_UpdateCheckCancelled");
        }
        catch (Exception error)
        {
            UpdateStatusText = FormatText("Ui_UpdateCheckFailed", _updateStatusFormatter.FormatError(error));
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private void ApplyUpdateCheckResult(UpdateCheckResult result)
    {
        UpdateLatestVersionText = result.LatestVersion ?? GetText("Ui_Unavailable");
        UpdateReleaseNotesText = string.IsNullOrWhiteSpace(result.Manifest?.ReleaseNotes)
            ? GetText("Ui_NoReleaseNotesProvided")
            : result.Manifest.ReleaseNotes;
        UpdateStatusText = _updateStatusFormatter.FormatCheckStatus(result);
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
        ProfileGpuThresholdPercentText = profile.Limits.EffectiveGpuThresholdPercent.ToString("N0", CultureInfo.CurrentCulture);
    }

    private bool TryReadGlobalThresholds(out ThresholdLimitValues limits) =>
        TryReadThresholds(
            CpuThresholdPercentText,
            RamThresholdMbText,
            DiskReadThresholdMbPerSecText,
            DiskWriteThresholdMbPerSecText,
            GpuThresholdPercentText,
            out limits);

    private CaptureSettings ReadCaptureSettingsFromUi() => new()
    {
        CaptureCpu = CaptureCpu,
        CaptureRam = CaptureRam,
        CaptureDiskRead = CaptureDiskRead,
        CaptureDiskWrite = CaptureDiskWrite,
        CaptureGpu = CaptureGpu
    };

    private bool TryReadProfileThresholds(out ThresholdLimitValues limits) =>
        TryReadThresholds(
            ProfileCpuThresholdPercentText,
            ProfileRamThresholdMbText,
            ProfileDiskReadThresholdMbPerSecText,
            ProfileDiskWriteThresholdMbPerSecText,
            ProfileGpuThresholdPercentText,
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
        string gpuText,
        out ThresholdLimitValues limits)
    {
        limits = new ThresholdLimitValues();
        if (!TryParsePositive(cpuText, out var cpuThreshold)
            || !TryParsePositive(ramText, out var ramThreshold)
            || !TryParsePositive(diskReadText, out var diskReadThreshold)
            || !TryParsePositive(diskWriteText, out var diskWriteThreshold)
            || !TryParsePositive(gpuText, out var gpuThreshold))
        {
            return false;
        }

        limits = new ThresholdLimitValues
        {
            CpuThresholdPercent = Math.Clamp(cpuThreshold, 1, 100),
            RamThresholdMb = Math.Clamp(ramThreshold, 1, 1_048_576),
            DiskReadThresholdMbPerSec = Math.Clamp(diskReadThreshold, 0.1, 1_048_576),
            DiskWriteThresholdMbPerSec = Math.Clamp(diskWriteThreshold, 0.1, 1_048_576),
            GpuThresholdPercent = Math.Clamp(gpuThreshold, 1, 100)
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

    private static string NormalizeExeNameForAssignment(string exeName) =>
        ExecutableIdentity.NormalizeExeNameForAssignment(exeName);

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
}
