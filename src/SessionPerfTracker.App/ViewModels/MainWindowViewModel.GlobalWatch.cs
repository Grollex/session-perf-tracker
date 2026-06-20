using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using SessionPerfTracker.Domain.Metrics;
using SessionPerfTracker.App.Localization;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace SessionPerfTracker.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public void ToggleGlobalWatchSort(string sortMode)
    {
        if (string.IsNullOrWhiteSpace(sortMode))
        {
            return;
        }

        if (string.Equals(_selectedGlobalWatchSortMode, sortMode, StringComparison.Ordinal))
        {
            _globalWatchSortDescending = !_globalWatchSortDescending;
        }
        else
        {
            _selectedGlobalWatchSortMode = sortMode;
            OnPropertyChanged(nameof(SelectedGlobalWatchSortMode));
            _globalWatchSortDescending = sortMode != GlobalSortName && sortMode != GlobalSortProfile;
        }

        ApplyGlobalWatchFilterAndSort();
        NotifyGlobalWatchSortHeaderProperties();
    }

    public void SortGlobalWatchByProcess() => ToggleGlobalWatchSort(GlobalSortName);

    public void SortGlobalWatchByPid() => ToggleGlobalWatchSort(GlobalSortPid);

    public void SortGlobalWatchByCpu() => ToggleGlobalWatchSort(GlobalSortCpu);

    public void SortGlobalWatchByRam() => ToggleGlobalWatchSort(GlobalSortRam);

    public void SortGlobalWatchByDisk() => ToggleGlobalWatchSort(GlobalSortDisk);

    public void SortGlobalWatchByProfile() => ToggleGlobalWatchSort(GlobalSortProfile);

    public void SortGlobalWatchByHealth() => ToggleGlobalWatchSort(GlobalSortHealth);

    public Task RefreshGlobalWatchAsync(CancellationToken cancellationToken = default) =>
        RefreshGlobalWatchCoreAsync(manual: true, cancellationToken);

    public async Task MonitorSelectedGlobalProcessAsync(CancellationToken cancellationToken = default)
    {
        var selectedProcess = GetSelectedGlobalWatchRow();
        if (selectedProcess is null)
        {
            GlobalWatchStatusText = "Select a process in Global Watch first.";
            return;
        }

        var processId = selectedProcess.ProcessId;
        var wasRecording = IsRecording;
        await RefreshRunningProcessesAsync(cancellationToken);
        var target = _allRunningTargets.FirstOrDefault(item => item.ProcessId == processId);
        if (target is null)
        {
            GlobalWatchStatusText = $"{selectedProcess.Name} ({processId}) is no longer running.";
            return;
        }

        ProcessFilterText = string.Empty;
        SelectedTargetMode = TargetModeRunningProcess;
        SelectedRunningTarget = RunningTargets.FirstOrDefault(item => item.ProcessId == processId) ?? target;
        SelectedTabIndex = LiveTabIndex;

        if (wasRecording)
        {
            GlobalWatchStatusText = $"{target.DisplayName} selected in Live. Current monitoring was stopped; press Start to apply changes.";
            NotifyTargetSelectionProperties();
            return;
        }

        RecordingStatusText = $"Starting detailed monitoring for {target.DisplayName}.";
        if (!IsRecording)
        {
            LiveWarningText = string.Empty;
        }

        GlobalWatchStatusText = $"{target.DisplayName} selected in Live for detailed monitoring.";
        NotifyTargetSelectionProperties();
        await StartRecordingAsync(cancellationToken);
    }

    public Task OpenSelectedGlobalProcessFileLocationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetSelectedGlobalProcessUsablePath(out var path) || !File.Exists(path))
        {
            GlobalWatchStatusText = "File location is unavailable for the selected process.";
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
            GlobalWatchStatusText = $"Opened file location for {Path.GetFileName(path)}.";
        }
        catch (Exception error)
        {
            GlobalWatchStatusText = $"Could not open file location: {error.Message}";
        }

        return Task.CompletedTask;
    }

    public void CopySelectedGlobalProcessFullPath()
    {
        if (!TryGetSelectedGlobalProcessUsablePath(out var path))
        {
            GlobalWatchStatusText = "Full path is unavailable for the selected process.";
            return;
        }

        try
        {
            Clipboard.SetText(path);
            GlobalWatchStatusText = $"Copied full path for {Path.GetFileName(path)}.";
        }
        catch (Exception error)
        {
            GlobalWatchStatusText = $"Could not copy full path: {error.Message}";
        }
    }

    public bool PrepareInspectorFromSelectedGlobalProcess()
    {
        var selected = GetSelectedGlobalWatchRow();
        if (selected is null)
        {
            GlobalWatchStatusText = "Select a process or application first.";
            return false;
        }

        SetProcessInspectorTarget(ProcessInspectorTargetViewModel.FromGlobalProcess(
            selected,
            "Global Watch Overview - latest scan",
            IsGlobalWatchGroupedMode));
        return true;
    }

    public async Task MonitorInspectorTargetAsync(CancellationToken cancellationToken = default)
    {
        var row = _processInspectorTarget?.ActiveRow
            ?? FindActiveGlobalWatchRowForInspector(
                _processInspectorTarget?.ExeName,
                processId: null,
                _processInspectorTarget?.NormalizedFullPath,
                preferApplications: _processInspectorTarget?.IsGroup ?? true);
        if (row is null)
        {
            GlobalWatchStatusText = $"{_processInspectorTarget?.ExeName ?? "Selected target"} is not running in the latest scan.";
            return;
        }

        SelectGlobalWatchRowForAction(row);
        await MonitorSelectedGlobalProcessAsync(cancellationToken);
    }

    public Task OpenInspectorFileLocationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetInspectorUsablePath(out var path) || !File.Exists(path))
        {
            GlobalWatchStatusText = "File location is unavailable for the inspector target.";
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
            GlobalWatchStatusText = $"Opened file location for {Path.GetFileName(path)}.";
        }
        catch (Exception error)
        {
            GlobalWatchStatusText = $"Could not open file location: {error.Message}";
        }

        return Task.CompletedTask;
    }

    public void CopyInspectorFullPath()
    {
        if (!TryGetInspectorUsablePath(out var path))
        {
            GlobalWatchStatusText = "Full path is unavailable for the inspector target.";
            return;
        }

        try
        {
            Clipboard.SetText(path);
            GlobalWatchStatusText = $"Copied full path for {Path.GetFileName(path)}.";
        }
        catch (Exception error)
        {
            GlobalWatchStatusText = $"Could not copy full path: {error.Message}";
        }
    }

    public async Task KillInspectorProcessAsync(CancellationToken cancellationToken = default)
    {
        if (_processInspectorTarget?.ActiveRow is not { } activeRow)
        {
            GlobalWatchStatusText = "Inspector target is not running; nothing to kill.";
            return;
        }

        var result = await _processControlService.KillProcessAsync(
            activeRow.ProcessId,
            "manual Process Inspector hard kill",
            cancellationToken);
        GlobalWatchStatusText = FormatProcessControlResult("Inspector hard kill", result);
        await RefreshGlobalWatchAsync(cancellationToken);
    }

    public async Task KillInspectorTreeOrGroupAsync(CancellationToken cancellationToken = default)
    {
        if (_processInspectorTarget?.ActiveRow is not { } activeRow)
        {
            GlobalWatchStatusText = "Inspector target is not running; nothing to kill.";
            return;
        }

        var result = await KillGlobalWatchRowTreeOrGroupAsync(
            activeRow,
            activeRow.IsGroup
                ? "manual Process Inspector app-group hard kill"
                : "manual Process Inspector process-tree hard kill",
            cancellationToken);

        GlobalWatchStatusText = FormatProcessControlResult(
            activeRow.IsGroup ? "Inspector app-group hard kill" : "Inspector process-tree hard kill",
            result);
        await RefreshGlobalWatchAsync(cancellationToken);
    }

    public async Task MarkInspectorSuspiciousAsync(CancellationToken cancellationToken = default)
    {
        var target = _processInspectorTarget;
        if (target is null)
        {
            SuspiciousWatchStatusText = "Open a process or saved watch item in Details first.";
            return;
        }

        if (!TryGetInspectorUsablePath(out _))
        {
            SuspiciousWatchStatusText = "Full path is unavailable; cannot mark it suspicious.";
            return;
        }

        var settings = _thresholdSettingsStore.Current;
        var watchlist = settings.SuspiciousWatchlist;
        var items = watchlist.Items
            .Where(item => !string.Equals(item.NormalizedPath, target.NormalizedFullPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        items.Insert(0, new SuspiciousWatchItem
        {
            NormalizedPath = target.NormalizedFullPath,
            ExeName = NormalizeExeNameForAssignment(target.ExeName),
            ProductName = target.ProductName,
            CompanyName = target.CompanyName,
            SignerStatus = target.SignerStatus,
            MarkedAt = DateTimeOffset.UtcNow,
            Note = $"Marked from Process Inspector ({target.SourceText})."
        });

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                SuspiciousWatchlist = watchlist with { Items = items }
            },
            cancellationToken);
        RefreshSuspiciousWatchCollections();
        NotifyInspectorTargetProperties();
        SuspiciousWatchStatusText = $"{target.ExeName} marked suspicious. Future launch transitions will be logged.";
    }

    public async Task RemoveInspectorSuspiciousAsync(CancellationToken cancellationToken = default)
    {
        if (_processInspectorTarget is null || !TryGetInspectorUsablePath(out _))
        {
            SuspiciousWatchStatusText = "Open a marked process or watch item in Details first.";
            return;
        }

        await RemoveSuspiciousPathAsync(_processInspectorTarget.NormalizedFullPath, cancellationToken);
        NotifyInspectorTargetProperties();
    }

    public Task BanInspectorTargetAsync(CancellationToken cancellationToken = default) =>
        BanInspectorTargetCoreAsync(killAfterBan: false, cancellationToken);

    public Task BanAndKillInspectorTargetAsync(CancellationToken cancellationToken = default) =>
        BanInspectorTargetCoreAsync(killAfterBan: true, cancellationToken);

    public void OpenRecommendationForInspectorTarget()
    {
        var target = _processInspectorTarget;
        if (target is null)
        {
            ProfileRecommendationStatusText = "Open a process in Details first.";
            return;
        }

        var recommendation = ProfileRecommendations.FirstOrDefault(item =>
            string.Equals(item.ExeName, target.ExeName, StringComparison.OrdinalIgnoreCase));
        if (recommendation is null)
        {
            ProfileRecommendationStatusText = $"No active recommendation for {target.ExeName}.";
            return;
        }

        SelectedProfileRecommendation = recommendation;
        SelectedTabIndex = GlobalWatchTabIndex;
        SelectedGlobalWatchSectionIndex = GlobalWatchRecommendationsSectionIndex;
        ProfileRecommendationStatusText = $"{recommendation.ExeName} recommendation selected.";
    }

    public Task PromoteSelectedRecommendationAsync(CancellationToken cancellationToken = default) =>
        SelectedProfileRecommendation is null
            ? SetRecommendationStatusAsync("Select a recommendation to promote.")
            : PromoteRecommendationsAsync([SelectedProfileRecommendation], cancellationToken);

    public Task PromoteRecommendationsAsync(
        IReadOnlyList<ProfileRecommendationViewModel> recommendations,
        CancellationToken cancellationToken = default) =>
        PromoteRecommendationsCoreAsync(recommendations, cancellationToken);

    public Task DenySelectedRecommendationAsync(CancellationToken cancellationToken = default) =>
        SelectedProfileRecommendation is null
            ? SetRecommendationStatusAsync("Select a recommendation to ignore.")
            : DenyRecommendationsAsync([SelectedProfileRecommendation], cancellationToken);

    public Task DenyRecommendationsAsync(
        IReadOnlyList<ProfileRecommendationViewModel> recommendations,
        CancellationToken cancellationToken = default) =>
        DenyRecommendationsCoreAsync(recommendations, cancellationToken);

    public async Task AssignSelectedGlobalProcessProfileAsync(CancellationToken cancellationToken = default)
    {
        var selectedProcess = GetSelectedGlobalWatchRow();
        if (selectedProcess is null)
        {
            GlobalWatchStatusText = "Select a process or exe group first.";
            return;
        }

        if (SelectedLiveAssignmentProfile is null)
        {
            GlobalWatchStatusText = "Select a profile in the right panel first.";
            return;
        }

        var exeName = NormalizeExeNameForAssignment(selectedProcess.ExeName);
        if (string.IsNullOrWhiteSpace(exeName))
        {
            GlobalWatchStatusText = "Could not determine exe name for assignment.";
            return;
        }

        var settings = _thresholdSettingsStore.Current;
        var assignments = new Dictionary<string, string>(settings.AppProfileAssignments, StringComparer.OrdinalIgnoreCase)
        {
            [exeName] = SelectedLiveAssignmentProfile.Id
        };

        await _thresholdSettingsStore.SaveAsync(
            settings with { AppProfileAssignments = assignments },
            cancellationToken);
        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
        GlobalWatchStatusText = $"{exeName} assigned to {SelectedLiveAssignmentProfile.Name}. Refreshing profile health on next scan.";
    }

    public void OpenRecommendationsForSelectedGlobalProcess()
    {
        var selectedProcess = GetSelectedGlobalWatchRow();
        if (selectedProcess is null)
        {
            ProfileRecommendationStatusText = "Select a process or exe group first.";
            return;
        }

        var recommendation = ProfileRecommendations.FirstOrDefault(item =>
            string.Equals(item.ExeName, selectedProcess.ExeName, StringComparison.OrdinalIgnoreCase));
        if (recommendation is null)
        {
            ProfileRecommendationStatusText = $"No active recommendation for {selectedProcess.ExeName}.";
        }
        else
        {
            SelectedProfileRecommendation = recommendation;
            ProfileRecommendationStatusText = $"{recommendation.ExeName} recommendation selected.";
        }

        SelectedTabIndex = GlobalWatchTabIndex;
        SelectedGlobalWatchSectionIndex = GlobalWatchRecommendationsSectionIndex;
    }

    public bool SelectRecommendationTargetForOverview(ProfileRecommendationViewModel? recommendation = null)
    {
        recommendation ??= SelectedProfileRecommendation;
        if (recommendation is null)
        {
            ProfileRecommendationStatusText = "Select a recommendation first.";
            return false;
        }

        var selected = SelectGlobalWatchTargetForExeOrPid(
            recommendation.ExeName,
            processId: null,
            preferApplications: true,
            statusContext: "recommendation",
            navigateToOverview: true);
        ProfileRecommendationStatusText = selected
            ? $"{recommendation.ExeName} selected in Global Watch Overview."
            : GlobalWatchStatusText;
        return selected;
    }

    public bool SelectRecommendationTargetForInspector(ProfileRecommendationViewModel? recommendation = null)
    {
        recommendation ??= SelectedProfileRecommendation;
        if (recommendation is null)
        {
            ProfileRecommendationStatusText = "Select a recommendation first.";
            return false;
        }

        var activeRow = FindActiveGlobalWatchRowForInspector(
            recommendation.ExeName,
            processId: null,
            normalizedPath: null,
            preferApplications: true);
        SetProcessInspectorTarget(ProcessInspectorTargetViewModel.FromRecommendation(
            recommendation.Recommendation,
            activeRow));
        ProfileRecommendationStatusText = activeRow is null
            ? $"{recommendation.ExeName} loaded in Process Inspector from saved recommendation; it is not running now."
            : $"{recommendation.ExeName} loaded in Process Inspector.";
        return true;
    }

    public bool SelectJournalTargetForOverview(GlobalWatchJournalGroupViewModel? group)
    {
        if (group is null)
        {
            GlobalWatchJournalStatusText = "Select a journal group first.";
            return false;
        }

        var preferApplications = group.ModeText.Contains("Applications", StringComparison.OrdinalIgnoreCase)
            || group.Entries.Count > 1
            || group.ProcessId is null;

        var selected = SelectGlobalWatchTargetForExeOrPid(
            group.ExeName,
            group.ProcessId,
            preferApplications,
            statusContext: "journal entry",
            navigateToOverview: true);
        GlobalWatchJournalStatusText = selected
            ? $"{group.ExeName} selected in Global Watch Overview."
            : GlobalWatchStatusText;
        return selected;
    }

    public bool SelectJournalTargetForInspector(GlobalWatchJournalGroupViewModel? group)
    {
        if (group is null)
        {
            GlobalWatchJournalStatusText = "Select a journal group first.";
            return false;
        }

        var preferApplications = group.ModeText.Contains("Applications", StringComparison.OrdinalIgnoreCase)
            || group.Entries.Count > 1
            || group.ProcessId is null;

        var activeRow = FindActiveGlobalWatchRowForInspector(
            group.ExeName,
            group.ProcessId,
            normalizedPath: null,
            preferApplications);
        SetProcessInspectorTarget(ProcessInspectorTargetViewModel.FromJournalGroup(
            group,
            activeRow));
        GlobalWatchJournalStatusText = activeRow is null
            ? $"{group.ExeName} loaded in Process Inspector from saved journal data; it is not running now."
            : $"{group.ExeName} loaded in Process Inspector.";
        return true;
    }

    public async Task MarkRecommendationTargetSuspiciousAsync(
        ProfileRecommendationViewModel? recommendation = null,
        CancellationToken cancellationToken = default)
    {
        if (!SelectRecommendationTargetForInspector(recommendation))
        {
            return;
        }

        await MarkInspectorSuspiciousAsync(cancellationToken);
    }

    public async Task BanRecommendationTargetAsync(
        ProfileRecommendationViewModel? recommendation = null,
        bool killAfterBan = false,
        CancellationToken cancellationToken = default)
    {
        if (!SelectRecommendationTargetForInspector(recommendation))
        {
            return;
        }

        await BanInspectorTargetCoreAsync(killAfterBan, cancellationToken);
    }

    public async Task MarkJournalTargetSuspiciousAsync(
        GlobalWatchJournalGroupViewModel? group,
        CancellationToken cancellationToken = default)
    {
        if (!SelectJournalTargetForInspector(group))
        {
            return;
        }

        await MarkInspectorSuspiciousAsync(cancellationToken);
    }

    public async Task BanJournalTargetAsync(
        GlobalWatchJournalGroupViewModel? group,
        bool killAfterBan = false,
        CancellationToken cancellationToken = default)
    {
        if (!SelectJournalTargetForInspector(group))
        {
            return;
        }

        await BanInspectorTargetCoreAsync(killAfterBan, cancellationToken);
    }

    public async Task RemoveSelectedRecommendationDenyAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedDeniedProfileRecommendation is null)
        {
            ProfileRecommendationStatusText = "Select an ignored recommendation first.";
            return;
        }

        var denied = SelectedDeniedProfileRecommendation.Denied;
        var settings = _thresholdSettingsStore.Current;
        var recommendationSettings = settings.Recommendations;
        var deniedItems = recommendationSettings.Denied
            .Where(item => !string.Equals(item.ExeName, denied.ExeName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.SuggestedProfileId, denied.SuggestedProfileId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var history = AddRecommendationHistory(
            recommendationSettings.History,
            "recommendation_deny_removed",
            denied.ExeName,
            denied.SuggestedProfileId,
            denied.SuggestedProfileName,
            $"Deny removed for {denied.ExeName} -> {denied.SuggestedProfileName}.");

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                Recommendations = recommendationSettings with
                {
                    Denied = deniedItems,
                    History = history
                }
            },
            cancellationToken);
        RefreshRecommendationCollections();
        ProfileRecommendationStatusText = $"{denied.ExeName} recommendation re-enabled.";
    }

    public async Task MarkSelectedGlobalProcessSuspiciousAsync(CancellationToken cancellationToken = default)
    {
        var selected = GetSelectedGlobalWatchRow();
        if (selected is null)
        {
            SuspiciousWatchStatusText = "Select an application or process first.";
            return;
        }

        if (!TryGetSelectedGlobalProcessUsablePath(out _)
            || string.IsNullOrWhiteSpace(selected.NormalizedFullPath))
        {
            SuspiciousWatchStatusText = "Full path is unavailable for this process; cannot mark it suspicious.";
            return;
        }

        var settings = _thresholdSettingsStore.Current;
        var watchlist = settings.SuspiciousWatchlist;
        var items = watchlist.Items
            .Where(item => !string.Equals(item.NormalizedPath, selected.NormalizedFullPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        items.Insert(0, new SuspiciousWatchItem
        {
            NormalizedPath = selected.NormalizedFullPath,
            ExeName = NormalizeExeNameForAssignment(selected.ExeName),
            ProductName = selected.ProductName,
            CompanyName = selected.CompanyName,
            SignerStatus = selected.SignerStatus,
            MarkedAt = DateTimeOffset.UtcNow
        });

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                SuspiciousWatchlist = watchlist with { Items = items }
            },
            cancellationToken);
        RefreshSuspiciousWatchCollections();
        NotifySelectedGlobalProcessProperties();
        SuspiciousWatchStatusText = $"{selected.ExeName} marked suspicious. Future launch transitions will be logged.";
    }

    public async Task RemoveSelectedGlobalProcessSuspiciousAsync(CancellationToken cancellationToken = default)
    {
        var selected = GetSelectedGlobalWatchRow();
        if (selected is null
            || !TryGetSelectedGlobalProcessUsablePath(out _)
            || string.IsNullOrWhiteSpace(selected.NormalizedFullPath))
        {
            SuspiciousWatchStatusText = "Select a marked application or process first.";
            return;
        }

        await RemoveSuspiciousPathAsync(selected.NormalizedFullPath, cancellationToken);
    }

    public async Task RemoveSelectedSuspiciousWatchItemAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSuspiciousWatchItem is null)
        {
            SuspiciousWatchStatusText = "Select a suspicious watchlist item first.";
            return;
        }

        await RemoveSuspiciousPathAsync(SelectedSuspiciousWatchItem.Item.NormalizedPath, cancellationToken);
    }

    public bool SelectSuspiciousTargetForInspector(SuspiciousWatchItemViewModel? suspiciousItem = null)
    {
        suspiciousItem ??= SelectedSuspiciousWatchItem;
        if (suspiciousItem is null)
        {
            SuspiciousWatchStatusText = "Select a suspicious watchlist item first.";
            return false;
        }

        var activeRow = FindActiveGlobalWatchRowForInspector(
            suspiciousItem.ExeName,
            processId: null,
            suspiciousItem.Item.NormalizedPath,
            preferApplications: true);
        SetProcessInspectorTarget(ProcessInspectorTargetViewModel.FromSuspiciousItem(
            suspiciousItem.Item,
            activeRow));
        SuspiciousWatchStatusText = activeRow is null
            ? $"{suspiciousItem.ExeName} loaded in Process Inspector from watchlist; it is not running now."
            : $"{suspiciousItem.ExeName} loaded in Process Inspector.";
        return true;
    }

    public async Task KillSelectedGlobalProcessAsync(CancellationToken cancellationToken = default)
    {
        var selected = GetSelectedGlobalWatchRow();
        if (selected is null)
        {
            GlobalWatchStatusText = "Select a process or application first.";
            return;
        }

        var result = await _processControlService.KillProcessAsync(
            selected.ProcessId,
            "manual Global Watch hard kill",
            cancellationToken);
        GlobalWatchStatusText = FormatProcessControlResult("Manual hard kill", result);
        await RefreshGlobalWatchAsync(cancellationToken);
    }

    public async Task KillSelectedGlobalProcessTreeOrGroupAsync(CancellationToken cancellationToken = default)
    {
        var selected = GetSelectedGlobalWatchRow();
        if (selected is null)
        {
            GlobalWatchStatusText = "Select a process or application first.";
            return;
        }

        var result = await KillGlobalWatchRowTreeOrGroupAsync(
            selected,
            selected.IsGroup
                ? "manual Global Watch app-group hard kill"
                : "manual Global Watch process-tree hard kill",
            cancellationToken);

        GlobalWatchStatusText = FormatProcessControlResult(
            selected.IsGroup ? "Manual app-group hard kill" : "Manual process-tree hard kill",
            result);
        await RefreshGlobalWatchAsync(cancellationToken);
    }

    public Task BanSelectedGlobalProcessAsync(CancellationToken cancellationToken = default) =>
        BanSelectedGlobalProcessCoreAsync(killAfterBan: false, cancellationToken);

    public Task BanAndKillSelectedGlobalProcessAsync(CancellationToken cancellationToken = default) =>
        BanSelectedGlobalProcessCoreAsync(killAfterBan: true, cancellationToken);

    public async Task RemoveSelectedProcessBanAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedProcessBan is null)
        {
            ProcessBanStatusText = "Select a ban to remove.";
            return;
        }

        var selected = SelectedProcessBan.Rule;
        var settings = _thresholdSettingsStore.Current;
        var processBans = settings.ProcessBans;
        var active = processBans.Active
            .Where(item => !string.Equals(item.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var history = processBans.History.ToList();
        history.Insert(0, new ProcessBanEvent
        {
            Id = $"ban_event_{Guid.NewGuid():N}",
            Timestamp = DateTimeOffset.UtcNow,
            NormalizedPath = selected.NormalizedPath,
            ExeName = selected.ExeName,
            ProductName = selected.ProductName,
            CompanyName = selected.CompanyName,
            SignerStatus = selected.SignerStatus,
            Action = "ban_removed",
            DurationLabel = selected.DurationLabel,
            Details = "Manual allow/remove from Bans tab."
        });

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                ProcessBans = processBans with
                {
                    Active = active,
                    History = history
                }
            },
            cancellationToken);
        _runningBannedPaths.Remove(selected.NormalizedPath);
        RefreshProcessBanCollections();
        ProcessBanStatusText = $"{selected.ExeName} ban removed. Future launches are allowed unless another ban exists.";
    }

    private async Task BanSelectedGlobalProcessCoreAsync(bool killAfterBan, CancellationToken cancellationToken)
    {
        var selected = GetSelectedGlobalWatchRow();
        if (selected is null)
        {
            ProcessBanStatusText = "Select a process or application first.";
            return;
        }

        if (!TryGetSelectedGlobalProcessUsablePath(out _)
            || string.IsNullOrWhiteSpace(selected.NormalizedFullPath))
        {
            ProcessBanStatusText = "Full path is unavailable; cannot create a ban.";
            return;
        }

        var duration = SelectedProcessBanDurationOption ?? ProcessBanDurationOptions.First();
        var now = DateTimeOffset.UtcNow;
        var settings = _thresholdSettingsStore.Current;
        var processBans = settings.ProcessBans;
        var active = processBans.Active
            .Where(item => !string.Equals(item.NormalizedPath, selected.NormalizedFullPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var rule = new ProcessBanRule
        {
            Id = $"ban_{Guid.NewGuid():N}",
            NormalizedPath = selected.NormalizedFullPath,
            ExeName = NormalizeExeNameForAssignment(selected.ExeName),
            ProductName = selected.ProductName,
            CompanyName = selected.CompanyName,
            SignerStatus = selected.SignerStatus,
            CreatedAt = now,
            ExpiresAt = duration.Duration is null ? null : now + duration.Duration.Value,
            DurationLabel = duration.Label,
            Reason = "Manual ban from Global Watch."
        };
        active.Insert(0, rule);

        var history = processBans.History.ToList();
        history.Insert(0, CreateProcessBanEvent(
            rule,
            "ban_created",
            selected,
            terminatedCount: 0,
            details: killAfterBan
                ? "Created from Global Watch; selected target will be killed immediately."
                : "Created from Global Watch."));

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                ProcessBans = processBans with
                {
                    Active = active,
                    History = history
                }
            },
            cancellationToken);

        RefreshProcessBanCollections();
        NotifySelectedGlobalProcessProperties();
        ProcessBanStatusText = $"{rule.ExeName} banned for {rule.DurationLabel}. Enforcement happens on Global Watch scans while this utility is running.";

        if (killAfterBan)
        {
            var result = await KillGlobalWatchRowTreeOrGroupAsync(
                selected,
                "manual ban plus hard kill",
                cancellationToken);
            var latestSettings = _thresholdSettingsStore.Current;
            var latestProcessBans = latestSettings.ProcessBans;
            var latestHistory = latestProcessBans.History.ToList();
            latestHistory.Insert(0, CreateProcessBanEvent(
                rule,
                "ban_kill",
                selected,
                result.TerminatedCount,
                result.Messages.Count == 0
                    ? "Ban + kill executed immediately."
                    : string.Join(" ", result.Messages.Take(3))));
            await _thresholdSettingsStore.SaveAsync(
                latestSettings with
                {
                    ProcessBans = latestProcessBans with
                    {
                        History = latestHistory
                            .OrderByDescending(entry => entry.Timestamp)
                            .Take(latestProcessBans.MaxHistory)
                            .ToList()
                    }
                },
                cancellationToken);
            RefreshProcessBanCollections();
            ProcessBanStatusText = $"{rule.ExeName} banned for {rule.DurationLabel}; {result.TerminatedCount:N0} running process{(result.TerminatedCount == 1 ? string.Empty : "es")} killed now.";
            GlobalWatchStatusText = FormatProcessControlResult("Ban + hard kill", result);
            await RefreshGlobalWatchAsync(cancellationToken);
        }
    }

    private async Task BanInspectorTargetCoreAsync(bool killAfterBan, CancellationToken cancellationToken)
    {
        var target = _processInspectorTarget;
        if (target is null)
        {
            ProcessBanStatusText = "Open a process or saved watch item in Details first.";
            return;
        }

        if (!TryGetInspectorUsablePath(out _))
        {
            ProcessBanStatusText = "Full path is unavailable; cannot create a ban.";
            return;
        }

        var duration = SelectedProcessBanDurationOption ?? ProcessBanDurationOptions.First();
        var now = DateTimeOffset.UtcNow;
        var settings = _thresholdSettingsStore.Current;
        var processBans = settings.ProcessBans;
        var active = processBans.Active
            .Where(item => !string.Equals(item.NormalizedPath, target.NormalizedFullPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var rule = new ProcessBanRule
        {
            Id = $"ban_{Guid.NewGuid():N}",
            NormalizedPath = target.NormalizedFullPath,
            ExeName = NormalizeExeNameForAssignment(target.ExeName),
            ProductName = target.ProductName,
            CompanyName = target.CompanyName,
            SignerStatus = target.SignerStatus,
            CreatedAt = now,
            ExpiresAt = duration.Duration is null ? null : now + duration.Duration.Value,
            DurationLabel = duration.Label,
            Reason = $"Manual ban from Process Inspector ({target.SourceText})."
        };
        active.Insert(0, rule);

        var history = processBans.History.ToList();
        history.Insert(0, CreateProcessBanEvent(
            rule,
            "ban_created",
            target.ActiveRow,
            terminatedCount: 0,
            details: killAfterBan
                ? "Created from Process Inspector; running target will be killed when available."
                : "Created from Process Inspector."));

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                ProcessBans = processBans with
                {
                    Active = active,
                    History = history
                        .OrderByDescending(entry => entry.Timestamp)
                        .Take(processBans.MaxHistory)
                        .ToList()
                }
            },
            cancellationToken);

        RefreshProcessBanCollections();
        NotifyInspectorTargetProperties();
        ProcessBanStatusText = $"{rule.ExeName} banned for {rule.DurationLabel}. Enforcement happens on Global Watch scans while this utility is running.";

        if (!killAfterBan)
        {
            return;
        }

        if (target.ActiveRow is null)
        {
            ProcessBanStatusText = $"{rule.ExeName} banned for {rule.DurationLabel}. It is not running now, so nothing was killed immediately.";
            return;
        }

        var result = await KillGlobalWatchRowTreeOrGroupAsync(
            target.ActiveRow,
            "manual Process Inspector ban plus hard kill",
            cancellationToken);
        var latestSettings = _thresholdSettingsStore.Current;
        var latestProcessBans = latestSettings.ProcessBans;
        var latestHistory = latestProcessBans.History.ToList();
        latestHistory.Insert(0, CreateProcessBanEvent(
            rule,
            "ban_kill",
            target.ActiveRow,
            result.TerminatedCount,
            result.Messages.Count == 0
                ? "Ban + kill executed immediately from Process Inspector."
                : string.Join(" ", result.Messages.Take(3))));
        await _thresholdSettingsStore.SaveAsync(
            latestSettings with
            {
                ProcessBans = latestProcessBans with
                {
                    History = latestHistory
                        .OrderByDescending(entry => entry.Timestamp)
                        .Take(latestProcessBans.MaxHistory)
                        .ToList()
                }
            },
            cancellationToken);
        RefreshProcessBanCollections();
        ProcessBanStatusText = $"{rule.ExeName} banned for {rule.DurationLabel}; {result.TerminatedCount:N0} running process{(result.TerminatedCount == 1 ? string.Empty : "es")} killed now.";
        GlobalWatchStatusText = FormatProcessControlResult("Inspector ban + hard kill", result);
        await RefreshGlobalWatchAsync(cancellationToken);
    }

    private Task<ProcessControlResult> KillGlobalWatchRowTreeOrGroupAsync(
        GlobalProcessRowViewModel selected,
        string reason,
        CancellationToken cancellationToken)
    {
        return selected.IsGroup
            ? _processControlService.KillProcessesAsync(
                selected.IncludedProcesses.Select(process => process.ProcessId).ToArray(),
                reason,
                cancellationToken)
            : _processControlService.KillProcessTreeAsync(
                selected.ProcessId,
                reason,
                cancellationToken);
    }

    private Task SetRecommendationStatusAsync(string status)
    {
        ProfileRecommendationStatusText = status;
        return Task.CompletedTask;
    }

    private async Task PromoteRecommendationsCoreAsync(
        IReadOnlyList<ProfileRecommendationViewModel> recommendations,
        CancellationToken cancellationToken)
    {
        var selected = recommendations
            .Where(item => item is not null)
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (selected.Length == 0)
        {
            ProfileRecommendationStatusText = "Select one or more recommendations to promote.";
            return;
        }

        var settings = _thresholdSettingsStore.Current;
        var recommendationSettings = settings.Recommendations;
        var assignments = new Dictionary<string, string>(settings.AppProfileAssignments, StringComparer.OrdinalIgnoreCase);
        var active = recommendationSettings.Active.ToList();
        var history = recommendationSettings.History.ToList();

        foreach (var item in selected)
        {
            var recommendation = item.Recommendation;
            assignments[recommendation.ExeName] = recommendation.SuggestedProfileId;
            _globalWatchOverLimitWarnings.Remove(recommendation.ExeName);
            active.RemoveAll(activeItem => string.Equals(activeItem.Id, recommendation.Id, StringComparison.OrdinalIgnoreCase));
            history = AddRecommendationHistory(
                history,
                "recommendation_promoted",
                recommendation.ExeName,
                recommendation.SuggestedProfileId,
                recommendation.SuggestedProfileName,
                $"{recommendation.ExeName} promoted to {recommendation.SuggestedProfileName}.");
        }

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                AppProfileAssignments = assignments,
                Recommendations = recommendationSettings with
                {
                    Active = active,
                    History = history
                }
            },
            cancellationToken);

        LoadThresholdSettingsIntoUi(_thresholdSettingsStore.Current, SelectedThresholdProfile?.Id);
        ApplyGlobalWatchFilterAndSort();
        ProfileRecommendationStatusText = $"Promoted {selected.Length:N0} recommendation{(selected.Length == 1 ? string.Empty : "s")}.";
    }

    private async Task DenyRecommendationsCoreAsync(
        IReadOnlyList<ProfileRecommendationViewModel> recommendations,
        CancellationToken cancellationToken)
    {
        var selected = recommendations
            .Where(item => item is not null)
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (selected.Length == 0)
        {
            ProfileRecommendationStatusText = "Select one or more recommendations to ignore.";
            return;
        }

        var settings = _thresholdSettingsStore.Current;
        var recommendationSettings = settings.Recommendations;
        var active = recommendationSettings.Active.ToList();
        var denied = recommendationSettings.Denied.ToList();
        var history = recommendationSettings.History.ToList();
        var now = DateTimeOffset.UtcNow;

        foreach (var item in selected)
        {
            var recommendation = item.Recommendation;
            _globalWatchOverLimitWarnings.Remove(recommendation.ExeName);
            active.RemoveAll(activeItem => string.Equals(activeItem.Id, recommendation.Id, StringComparison.OrdinalIgnoreCase));
            if (!denied.Any(deniedItem => string.Equals(deniedItem.ExeName, recommendation.ExeName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(deniedItem.SuggestedProfileId, recommendation.SuggestedProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                denied.Add(new DeniedProfileRecommendation
                {
                    ExeName = recommendation.ExeName,
                    SuggestedProfileId = recommendation.SuggestedProfileId,
                    SuggestedProfileName = recommendation.SuggestedProfileName,
                    DeniedAt = now,
                    Reason = recommendation.Reason
                });
            }

            history = AddRecommendationHistory(
                history,
                "recommendation_denied",
                recommendation.ExeName,
                recommendation.SuggestedProfileId,
                recommendation.SuggestedProfileName,
                $"{recommendation.ExeName} recommendation ignored: {recommendation.Reason}");
        }

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                Recommendations = recommendationSettings with
                {
                    Active = active,
                    Denied = denied.OrderByDescending(item => item.DeniedAt).ToList(),
                    History = history
                }
            },
            cancellationToken);

        RefreshRecommendationCollections();
        ProfileRecommendationStatusText = $"Ignored {selected.Length:N0} recommendation{(selected.Length == 1 ? string.Empty : "s")}.";
    }

    private void RefreshRecommendationCollections()
    {
        var selectedId = SelectedProfileRecommendation?.Id;
        var selectedDenied = SelectedDeniedProfileRecommendation?.Denied;
        ProfileRecommendations.ReplaceWith(_thresholdSettingsStore.Current.Recommendations.Active
            .OrderByDescending(item => item.LastSeen)
            .Select(item => new ProfileRecommendationViewModel(item)));
        DeniedProfileRecommendations.ReplaceWith(_thresholdSettingsStore.Current.Recommendations.Denied
            .OrderByDescending(item => item.DeniedAt)
            .Select(item => new DeniedProfileRecommendationViewModel(item)));
        SelectedProfileRecommendation = string.IsNullOrWhiteSpace(selectedId)
            ? ProfileRecommendations.FirstOrDefault()
            : ProfileRecommendations.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                ?? ProfileRecommendations.FirstOrDefault();
        SelectedDeniedProfileRecommendation = selectedDenied is null
            ? DeniedProfileRecommendations.FirstOrDefault()
            : DeniedProfileRecommendations.FirstOrDefault(item =>
                string.Equals(item.ExeName, selectedDenied.ExeName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Denied.SuggestedProfileId, selectedDenied.SuggestedProfileId, StringComparison.OrdinalIgnoreCase))
                ?? DeniedProfileRecommendations.FirstOrDefault();
        NotifySelectedGlobalProcessProperties();
    }

    private void RefreshGlobalWatchJournalCollection()
    {
        var recentEntries = _thresholdSettingsStore.Current.WatchJournal.Entries
            .OrderByDescending(item => item.Timestamp)
            .Take(GlobalWatchJournalUiLimit)
            .ToArray();
        GlobalWatchJournalEntries.ReplaceWith(recentEntries
            .Select(item => new GlobalWatchJournalEntryViewModel(item)));
        GlobalWatchJournalGroups.ReplaceWith(recentEntries
            .GroupBy(CreateGlobalWatchJournalGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GlobalWatchJournalGroupViewModel(group.ToArray()))
            .OrderByDescending(group => group.Entry.Timestamp));
        OnPropertyChanged(nameof(HasGlobalWatchJournalEntries));
        OnPropertyChanged(nameof(HasGlobalWatchJournalGroups));
        GlobalWatchJournalStatusText = GlobalWatchJournalGroups.Count == 0
            ? "Watch Journal is empty. Near, over, critical and recommendation states will appear here."
            : $"{GlobalWatchJournalEntries.Count:N0} recent watch journal entries grouped into {GlobalWatchJournalGroups.Count:N0} branch log{(GlobalWatchJournalGroups.Count == 1 ? string.Empty : "s")}.";
    }

    private static string CreateGlobalWatchJournalGroupKey(GlobalWatchJournalEntry entry)
    {
        var exeName = NormalizeExeNameForAssignment(entry.ExeName);
        var displayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? exeName : entry.DisplayName.Trim();
        var identity = string.IsNullOrWhiteSpace(exeName) ? displayName : exeName;
        return string.Join(
            "|",
            entry.WatchMode.Trim(),
            identity,
            entry.ProfileSource.Trim(),
            entry.HealthState.Trim(),
            entry.Reason.Trim());
    }

    private void RefreshSuspiciousWatchCollections()
    {
        var selectedPath = SelectedSuspiciousWatchItem?.Item.NormalizedPath;
        var watchlist = _thresholdSettingsStore.Current.SuspiciousWatchlist;
        SuspiciousWatchItems.ReplaceWith(watchlist.Items
            .OrderByDescending(item => item.MarkedAt)
            .Select(item => new SuspiciousWatchItemViewModel(item)));
        SuspiciousLaunchEntries.ReplaceWith(watchlist.LaunchHistory
            .OrderByDescending(item => item.Timestamp)
            .Take(watchlist.MaxLaunchHistory)
            .Select(item => new SuspiciousLaunchEntryViewModel(item)));
        SelectedSuspiciousWatchItem = string.IsNullOrWhiteSpace(selectedPath)
            ? SuspiciousWatchItems.FirstOrDefault()
            : SuspiciousWatchItems.FirstOrDefault(item => string.Equals(item.Item.NormalizedPath, selectedPath, StringComparison.OrdinalIgnoreCase))
                ?? SuspiciousWatchItems.FirstOrDefault();
        OnPropertyChanged(nameof(HasSuspiciousWatchItems));
        OnPropertyChanged(nameof(HasSuspiciousLaunchEntries));
        NotifySelectedGlobalProcessProperties();
        SuspiciousWatchStatusText = SuspiciousWatchItems.Count == 0
            ? "No suspicious watchlist items yet. Mark a selected process or application from Global Watch."
            : $"{SuspiciousWatchItems.Count:N0} suspicious item{(SuspiciousWatchItems.Count == 1 ? string.Empty : "s")} watched; {SuspiciousLaunchEntries.Count:N0} launch entries shown.";
    }

    private void RefreshProcessBanCollections()
    {
        var selectedId = SelectedProcessBan?.Rule.Id;
        var processBans = _thresholdSettingsStore.Current.ProcessBans;
        ProcessBans.ReplaceWith(processBans.Active
            .OrderBy(rule => rule.ExpiresAt ?? DateTimeOffset.MaxValue)
            .ThenByDescending(rule => rule.CreatedAt)
            .Select(rule => new ProcessBanRuleViewModel(rule)));
        ProcessBanEvents.ReplaceWith(processBans.History
            .OrderByDescending(entry => entry.Timestamp)
            .Take(processBans.MaxHistory)
            .Select(entry => new ProcessBanEventViewModel(entry)));
        SelectedProcessBan = string.IsNullOrWhiteSpace(selectedId)
            ? ProcessBans.FirstOrDefault()
            : ProcessBans.FirstOrDefault(item => string.Equals(item.Rule.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                ?? ProcessBans.FirstOrDefault();
        OnPropertyChanged(nameof(HasProcessBans));
        OnPropertyChanged(nameof(HasProcessBanEvents));
        OnPropertyChanged(nameof(CanRemoveSelectedProcessBan));
        ProcessBanStatusText = ProcessBans.Count == 0
            ? "No active process bans. Create one from Global Watch Overview or Process Inspector."
            : $"{ProcessBans.Count:N0} active ban{(ProcessBans.Count == 1 ? string.Empty : "s")}; enforcement runs during Global Watch scans.";
    }

    private static bool IsProcessBanActive(ProcessBanRule rule, DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(rule.NormalizedPath)
        && (rule.ExpiresAt is null || rule.ExpiresAt > now);

    private static ProcessBanEvent CreateProcessBanEvent(
        ProcessBanRule rule,
        string action,
        GlobalProcessRowViewModel? row,
        int terminatedCount,
        string? details)
    {
        return new ProcessBanEvent
        {
            Id = $"ban_event_{Guid.NewGuid():N}",
            Timestamp = DateTimeOffset.UtcNow,
            NormalizedPath = rule.NormalizedPath,
            ExeName = rule.ExeName,
            ProductName = string.IsNullOrWhiteSpace(row?.ProductName) ? rule.ProductName : row.ProductName,
            CompanyName = string.IsNullOrWhiteSpace(row?.CompanyName) ? rule.CompanyName : row.CompanyName,
            SignerStatus = string.IsNullOrWhiteSpace(row?.SignerStatus) ? rule.SignerStatus : row.SignerStatus,
            Action = action,
            DurationLabel = rule.DurationLabel,
            ProcessId = row?.IsGroup == true ? null : row?.ProcessId,
            ProcessName = row?.DisplayName,
            TerminatedCount = terminatedCount,
            Details = details
        };
    }

    private static string FormatProcessControlResult(string action, ProcessControlResult result)
    {
        var detail = result.Messages.Count == 0
            ? string.Empty
            : $" {string.Join(" ", result.Messages.Take(2))}";
        return $"{action}: {result.TerminatedCount:N0}/{result.RequestedCount:N0} process{(result.RequestedCount == 1 ? string.Empty : "es")} terminated.{detail}";
    }

    private async Task RemoveSuspiciousPathAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        var settings = _thresholdSettingsStore.Current;
        var watchlist = settings.SuspiciousWatchlist;
        var items = watchlist.Items
            .Where(item => !string.Equals(item.NormalizedPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (items.Count == watchlist.Items.Count)
        {
            SuspiciousWatchStatusText = "Selected item is not marked suspicious.";
            return;
        }

        _runningSuspiciousPaths.Remove(normalizedPath);
        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                SuspiciousWatchlist = watchlist with { Items = items }
            },
            cancellationToken);
        RefreshSuspiciousWatchCollections();
        NotifySelectedGlobalProcessProperties();
        SuspiciousWatchStatusText = "Suspicious mark removed. Existing launch history is kept for review.";
    }

    private GlobalWatchJournalEntry CreateGlobalWatchJournalEntry(
        GlobalProcessRowViewModel row,
        DateTimeOffset capturedAt,
        string healthState,
        string reason,
        string? recommendationId = null)
    {
        return new GlobalWatchJournalEntry
        {
            Id = $"watch_{Guid.NewGuid():N}",
            Timestamp = capturedAt,
            ExeName = NormalizeExeNameForAssignment(row.ExeName),
            DisplayName = row.DisplayName,
            ProcessId = row.IsGroup ? null : row.ProcessId,
            WatchMode = row.IsGroup ? GlobalModeApplications : GlobalModeProcesses,
            ProfileSource = row.ProfileSourceText,
            HealthState = healthState,
            Reason = reason,
            CpuPercent = row.CpuPercent,
            MemoryMb = row.MemoryMb,
            DiskReadMbPerSec = row.DiskReadMbPerSec,
            DiskWriteMbPerSec = row.DiskWriteMbPerSec,
            RecommendationId = recommendationId
        };
    }

    private static List<GlobalWatchJournalEntry> TrimGlobalWatchJournalEntries(
        IEnumerable<GlobalWatchJournalEntry> entries,
        int maxEntries)
    {
        return entries
            .OrderByDescending(item => item.Timestamp)
            .Take(Math.Clamp(maxEntries, 50, 5000))
            .ToList();
    }

    private static List<ProfileRecommendationHistoryEntry> AddRecommendationHistory(
        IReadOnlyList<ProfileRecommendationHistoryEntry> history,
        string kind,
        string exeName,
        string suggestedProfileId,
        string suggestedProfileName,
        string reason)
    {
        return new[]
            {
                new ProfileRecommendationHistoryEntry
                {
                    Kind = kind,
                    ExeName = NormalizeExeNameForAssignment(exeName),
                    SuggestedProfileId = suggestedProfileId,
                    SuggestedProfileName = suggestedProfileName,
                    Timestamp = DateTimeOffset.UtcNow,
                    Reason = reason
                }
            }
            .Concat(history)
            .Take(500)
            .ToList();
    }

    private void StartGlobalWatch()
    {
        if (_globalWatchCts is not null)
        {
            return;
        }

        _globalWatchCts = new CancellationTokenSource();
        _ = RunGlobalWatchAsync(_globalWatchCts.Token);
    }

    private async Task RunGlobalWatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshGlobalWatchCoreAsync(manual: false, cancellationToken);
                await Task.Delay(GlobalWatchIntervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                GlobalWatchStatusText = $"Global Watch failed: {error.Message}";
            });
        }
    }

    private async Task RefreshGlobalWatchCoreAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!await _globalWatchScanLock.WaitAsync(0, cancellationToken))
        {
            if (manual)
            {
                GlobalWatchStatusText = "Global Watch scan is already running.";
            }

            return;
        }

        try
        {
            var scan = await _globalProcessScanner.ScanAsync(cancellationToken);
            var rows = scan.Processes.Select(CreateGlobalProcessRow).ToArray();
            await EnforceProcessBansAsync(rows, scan.CapturedAt, cancellationToken);
            await UpdateSuspiciousLaunchLoggingAsync(rows, scan.CapturedAt, cancellationToken);
            await UpdateProfileRecommendationsFromGlobalWatchAsync(rows, scan.CapturedAt, cancellationToken);
            var journalRows = SelectedGlobalWatchMode == GlobalModeApplications
                ? CreateGlobalWatchGroups(rows).ToArray()
                : rows;
            await UpdateGlobalWatchJournalAsync(journalRows, scan.CapturedAt, cancellationToken);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var selectedKey = _selectedGlobalProcessKey ?? GetGlobalWatchSelectionKey(SelectedGlobalProcess);
                _allGlobalProcessRows.Clear();
                _allGlobalProcessRows.AddRange(rows);
                ApplyGlobalWatchFilterAndSort(selectedKey);
                GlobalWatchLastScanText = scan.ComparedToPreviousScan is null
                    ? $"Last scan: {scan.CapturedAt.ToLocalTime():HH:mm:ss}; CPU/Disk deltas warm up on next scan"
                    : $"Last scan: {scan.CapturedAt.ToLocalTime():HH:mm:ss}; interval {scan.ComparedToPreviousScan.Value.TotalSeconds:N1}s";
                GlobalWatchStatusText = $"Global Watch: profile-aware scan every {GlobalWatchIntervalMs / 1000:N0}s. Unassigned apps use Light background fallback.";
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                GlobalWatchStatusText = $"Global Watch scan failed: {error.Message}";
            });
        }
        finally
        {
            _globalWatchScanLock.Release();
        }
    }

    private void ApplyGlobalWatchFilterAndSort(string? preferredSelectionKey = null)
    {
        var query = GlobalWatchFilterText.Trim();
        IEnumerable<GlobalProcessRowViewModel> filtered = IsGlobalWatchGroupedMode
            ? CreateGlobalWatchGroups(_allGlobalProcessRows)
            : _allGlobalProcessRows;
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(process => process.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || process.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || process.ExeName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || process.ProfileSourceText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || process.ProcessIdText.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (GlobalWatchOnlyOverLimit)
        {
            filtered = filtered.Where(process => process.IsOutOfProfile);
        }

        if (GlobalWatchOnlyCritical)
        {
            filtered = filtered.Where(process => process.IsCritical);
        }

        if (GlobalWatchOnlyUnassigned)
        {
            filtered = filtered.Where(process => process.IsUnassigned);
        }

        if (GlobalWatchOnlyNearLimit)
        {
            filtered = filtered.Where(process => process.IsNearLimit);
        }

        filtered = SortGlobalWatchRows(filtered);

        var previousKey = preferredSelectionKey
            ?? _selectedGlobalProcessKey
            ?? GetGlobalWatchSelectionKey(SelectedGlobalProcess);
        var filteredList = filtered.ToList();
        GlobalWatchProcesses.ReplaceWith(filteredList);
        OnPropertyChanged(nameof(HasGlobalWatchRows));
        OnPropertyChanged(nameof(GlobalWatchEmptyStateText));
        var nextSelection = previousKey is null
            ? GlobalWatchProcesses.FirstOrDefault()
            : GlobalWatchProcesses.FirstOrDefault(process => string.Equals(process.SelectionKey, previousKey, StringComparison.OrdinalIgnoreCase))
                ?? GlobalWatchProcesses.FirstOrDefault();
        SelectedGlobalProcess = nextSelection;
        _selectedGlobalProcessKey = nextSelection?.SelectionKey;

        var displayRows = IsGlobalWatchGroupedMode
            ? CreateGlobalWatchGroups(_allGlobalProcessRows).ToArray()
            : _allGlobalProcessRows.ToArray();
        TopCpuOffenders.ReplaceWith(displayRows
            .OrderByDescending(process => process.ProfileSeverityRank)
            .ThenByDescending(process => process.CpuPercent ?? 0)
            .ThenByDescending(process => process.MemoryMb ?? 0)
            .Take(3));
        TopRamOffenders.ReplaceWith(displayRows
            .OrderByDescending(process => process.ProfileSeverityRank)
            .ThenByDescending(process => process.MemoryMb ?? 0)
            .ThenByDescending(process => process.CpuPercent ?? 0)
            .Take(3));
        TopDiskOffenders.ReplaceWith(displayRows
            .OrderByDescending(process => process.ProfileSeverityRank)
            .ThenByDescending(process => process.DiskTotalMbPerSec)
            .ThenByDescending(process => process.MemoryMb ?? 0)
            .Take(3));
    }

    private IEnumerable<GlobalProcessRowViewModel> SortGlobalWatchRows(IEnumerable<GlobalProcessRowViewModel> rows)
    {
        if (SelectedGlobalWatchSortMode == GlobalSortRam)
        {
            return _globalWatchSortDescending
                ? rows.OrderByDescending(process => process.MemoryMb ?? 0).ThenByDescending(process => process.CpuPercent ?? 0)
                : rows.OrderBy(process => process.MemoryMb ?? 0).ThenBy(process => process.CpuPercent ?? 0);
        }

        if (SelectedGlobalWatchSortMode == GlobalSortDisk)
        {
            return _globalWatchSortDescending
                ? rows.OrderByDescending(process => process.DiskTotalMbPerSec).ThenByDescending(process => process.CpuPercent ?? 0)
                : rows.OrderBy(process => process.DiskTotalMbPerSec).ThenBy(process => process.CpuPercent ?? 0);
        }

        if (SelectedGlobalWatchSortMode == GlobalSortName)
        {
            return _globalWatchSortDescending
                ? rows.OrderByDescending(process => process.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(process => process.ProcessId)
                : rows.OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase).ThenBy(process => process.ProcessId);
        }

        if (SelectedGlobalWatchSortMode == GlobalSortPid)
        {
            return _globalWatchSortDescending
                ? rows.OrderByDescending(process => process.IsGroup ? process.InstanceCount : process.ProcessId).ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(process => process.IsGroup ? process.InstanceCount : process.ProcessId).ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase);
        }

        if (SelectedGlobalWatchSortMode == GlobalSortProfile)
        {
            return _globalWatchSortDescending
                ? rows.OrderByDescending(process => process.ProfileBadgeText, StringComparer.OrdinalIgnoreCase).ThenByDescending(process => process.CpuPercent ?? 0)
                : rows.OrderBy(process => process.ProfileBadgeText, StringComparer.OrdinalIgnoreCase).ThenByDescending(process => process.CpuPercent ?? 0);
        }

        if (SelectedGlobalWatchSortMode == GlobalSortHealth)
        {
            return _globalWatchSortDescending
                ? rows.OrderByDescending(process => process.ProfileSeverityRank).ThenByDescending(process => process.CpuPercent ?? 0)
                : rows.OrderBy(process => process.ProfileSeverityRank).ThenByDescending(process => process.CpuPercent ?? 0);
        }

        return _globalWatchSortDescending
            ? rows.OrderByDescending(process => process.CpuPercent ?? 0).ThenByDescending(process => process.MemoryMb ?? 0)
            : rows.OrderBy(process => process.CpuPercent ?? 0).ThenBy(process => process.MemoryMb ?? 0);
    }

    private string FormatGlobalWatchSortHeader(string label, string sortMode)
    {
        if (!string.Equals(SelectedGlobalWatchSortMode, sortMode, StringComparison.Ordinal))
        {
            return label;
        }

        return $"{label} {(_globalWatchSortDescending ? "↓" : "↑")}";
    }

    private void NotifyGlobalWatchSortHeaderProperties()
    {
        OnPropertyChanged(nameof(GlobalWatchProcessHeaderText));
        OnPropertyChanged(nameof(GlobalWatchPidHeaderText));
        OnPropertyChanged(nameof(GlobalWatchCpuHeaderText));
        OnPropertyChanged(nameof(GlobalWatchRamHeaderText));
        OnPropertyChanged(nameof(GlobalWatchDiskHeaderText));
        OnPropertyChanged(nameof(GlobalWatchProfileHeaderText));
        OnPropertyChanged(nameof(GlobalWatchHealthHeaderText));
    }

    private GlobalProcessRowViewModel? GetSelectedGlobalWatchRow()
    {
        if (SelectedGlobalProcess is not null
            && GlobalWatchProcesses.Any(row => ReferenceEquals(row, SelectedGlobalProcess)))
        {
            return SelectedGlobalProcess;
        }

        return _selectedGlobalProcessKey is null
            ? null
            : GlobalWatchProcesses.FirstOrDefault(row =>
                string.Equals(row.SelectionKey, _selectedGlobalProcessKey, StringComparison.OrdinalIgnoreCase));
    }

    private bool SelectGlobalWatchTargetForExeOrPid(
        string? exeName,
        int? processId,
        bool preferApplications,
        string statusContext,
        bool navigateToOverview)
    {
        var normalizedExeName = NormalizeExeNameForAssignment(exeName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedExeName))
        {
            GlobalWatchStatusText = $"Could not determine exe for {statusContext}.";
            return false;
        }

        if (_allGlobalProcessRows.Count == 0)
        {
            GlobalWatchStatusText = $"No recent Global Watch scan is available for {normalizedExeName}. Refresh and try again.";
            return false;
        }

        GlobalWatchFilterText = string.Empty;
        GlobalWatchOnlyOverLimit = false;
        GlobalWatchOnlyCritical = false;
        GlobalWatchOnlyNearLimit = false;
        GlobalWatchOnlyUnassigned = false;
        if (navigateToOverview)
        {
            SelectedTabIndex = GlobalWatchTabIndex;
            SelectedGlobalWatchSectionIndex = GlobalWatchOverviewSectionIndex;
        }

        var processRow = processId is null
            ? null
            : _allGlobalProcessRows.FirstOrDefault(row => row.ProcessId == processId.Value);
        var hasExeRows = _allGlobalProcessRows.Any(row =>
            string.Equals(row.ExeName, normalizedExeName, StringComparison.OrdinalIgnoreCase));
        if (processRow is null && !hasExeRows)
        {
            GlobalWatchStatusText = $"{normalizedExeName} is not running in the latest Global Watch scan.";
            return false;
        }

        var selectionKey = preferApplications || processRow is null
            ? $"group:{normalizedExeName}"
            : processRow.SelectionKey;
        _selectedGlobalProcessKey = selectionKey;
        SelectedGlobalWatchMode = selectionKey.StartsWith("pid:", StringComparison.OrdinalIgnoreCase)
            ? GlobalModeProcesses
            : GlobalModeApplications;
        ApplyGlobalWatchFilterAndSort(selectionKey);

        if (SelectedGlobalProcess is not null
            && string.Equals(SelectedGlobalProcess.SelectionKey, selectionKey, StringComparison.OrdinalIgnoreCase))
        {
            GlobalWatchStatusText = $"{normalizedExeName} selected from {statusContext}.";
            return true;
        }

        if (!selectionKey.StartsWith("group:", StringComparison.OrdinalIgnoreCase) && hasExeRows)
        {
            var fallbackKey = $"group:{normalizedExeName}";
            _selectedGlobalProcessKey = fallbackKey;
            SelectedGlobalWatchMode = GlobalModeApplications;
            ApplyGlobalWatchFilterAndSort(fallbackKey);
            if (SelectedGlobalProcess is not null
                && string.Equals(SelectedGlobalProcess.SelectionKey, fallbackKey, StringComparison.OrdinalIgnoreCase))
            {
                GlobalWatchStatusText = $"{normalizedExeName} application group selected from {statusContext}.";
                return true;
            }
        }

        GlobalWatchStatusText = $"Could not select a current Global Watch row for {normalizedExeName}. Refresh and try again.";
        return false;
    }

    private bool TryGetSelectedGlobalProcessUsablePath(out string path)
    {
        path = string.Empty;
        var selected = GetSelectedGlobalWatchRow();
        if (selected is null
            || string.IsNullOrWhiteSpace(selected.FullPath)
            || string.Equals(selected.FullPath, "Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(selected.FullPath);
            return Path.IsPathFullyQualified(path);
        }
        catch
        {
            path = selected.FullPath.Trim();
            return Path.IsPathFullyQualified(path);
        }
    }

    private bool TryGetInspectorUsablePath(out string path)
    {
        path = string.Empty;
        var target = _processInspectorTarget;
        if (target is null
            || !target.HasUsablePath
            || string.IsNullOrWhiteSpace(target.FullPath)
            || string.Equals(target.FullPath, "Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(target.FullPath);
            return Path.IsPathFullyQualified(path);
        }
        catch
        {
            path = target.FullPath.Trim();
            return Path.IsPathFullyQualified(path);
        }
    }

    private void SetProcessInspectorTarget(ProcessInspectorTargetViewModel target)
    {
        _processInspectorTarget = target;
        NotifyInspectorTargetProperties();
    }

    private void SelectGlobalWatchRowForAction(GlobalProcessRowViewModel row)
    {
        _selectedGlobalProcessKey = row.SelectionKey;
        SelectedGlobalWatchMode = row.IsGroup ? GlobalModeApplications : GlobalModeProcesses;
        ApplyGlobalWatchFilterAndSort(row.SelectionKey);
    }

    private GlobalProcessRowViewModel? FindActiveGlobalWatchRowForInspector(
        string? exeName,
        int? processId,
        string? normalizedPath,
        bool preferApplications)
    {
        var normalizedExeName = NormalizeExeNameForAssignment(exeName ?? string.Empty);
        var path = NormalizeFullPathForIdentity(normalizedPath);
        var candidates = _allGlobalProcessRows.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates = candidates.Where(row =>
                string.Equals(row.NormalizedFullPath, path, StringComparison.OrdinalIgnoreCase));
        }
        else if (processId is not null)
        {
            candidates = candidates.Where(row => row.ProcessId == processId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(normalizedExeName))
        {
            candidates = candidates.Where(row =>
                string.Equals(row.ExeName, normalizedExeName, StringComparison.OrdinalIgnoreCase));
        }

        var rows = candidates.ToArray();
        if (rows.Length == 0 && !string.IsNullOrWhiteSpace(normalizedExeName))
        {
            rows = _allGlobalProcessRows
                .Where(row => string.Equals(row.ExeName, normalizedExeName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (rows.Length == 0)
        {
            return null;
        }

        if (preferApplications || rows.Length > 1)
        {
            return GlobalProcessRowViewModel.CreateGroup(rows);
        }

        if (processId is not null)
        {
            return rows.FirstOrDefault(row => row.ProcessId == processId.Value) ?? rows[0];
        }

        return rows
            .OrderByDescending(row => row.CpuPercent ?? 0)
            .ThenByDescending(row => row.MemoryMb ?? 0)
            .First();
    }

    private static string NormalizeFullPathForIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || string.Equals(path.Trim(), "Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();
        }
        catch
        {
            return path.Trim().ToLowerInvariant();
        }
    }

    private static string? GetGlobalWatchSelectionKey(GlobalProcessRowViewModel? row) => row?.SelectionKey;

    private static IReadOnlyList<GlobalProcessRowViewModel> CreateGlobalWatchGroups(
        IReadOnlyList<GlobalProcessRowViewModel> rows)
    {
        return rows
            .GroupBy(row => row.ExeName, StringComparer.OrdinalIgnoreCase)
            .Select(group => GlobalProcessRowViewModel.CreateGroup(group.ToArray()))
            .ToArray();
    }

    private GlobalProcessRowViewModel CreateGlobalProcessRow(GlobalProcessSnapshot process)
    {
        var exeName = NormalizeExeNameForAssignment(process.Name);
        var settings = _thresholdSettingsStore.Current;
        var profile = ResolveGlobalWatchProfile(exeName, settings, out var isAssignedProfile);

        return new GlobalProcessRowViewModel(
            process,
            exeName,
            profile.Id,
            profile.Name,
            isAssignedProfile,
            profile.Limits);
    }

    private static ThresholdProfile ResolveGlobalWatchProfile(
        string exeName,
        CpuRamThresholdSettings settings,
        out bool isAssignedProfile)
    {
        isAssignedProfile = false;
        if (!string.IsNullOrWhiteSpace(exeName)
            && settings.AppProfileAssignments.TryGetValue(exeName, out var assignedProfileId)
            && settings.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, assignedProfileId, StringComparison.OrdinalIgnoreCase)) is { } assignedProfile)
        {
            isAssignedProfile = true;
            return assignedProfile;
        }

        return settings.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, ThresholdProfileDefaults.LightBackgroundId, StringComparison.OrdinalIgnoreCase))
            ?? ThresholdProfileDefaults.CreateProfiles().First(profile => profile.Id == ThresholdProfileDefaults.LightBackgroundId);
    }

    private async Task UpdateGlobalWatchJournalAsync(
        IReadOnlyList<GlobalProcessRowViewModel> rows,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var entriesToAdd = new List<GlobalWatchJournalEntry>();
        foreach (var row in rows.Where(row => row.IsNearLimit || row.IsOutOfProfile))
        {
            var key = $"{SelectedGlobalWatchMode}|{row.SelectionKey}|{row.HealthBadgeText}|{row.ProfileReason}";
            if (_globalWatchJournalLastEntryByKey.TryGetValue(key, out var lastEntryAt)
                && capturedAt - lastEntryAt < GlobalWatchJournalCooldown)
            {
                continue;
            }

            _globalWatchJournalLastEntryByKey[key] = capturedAt;
            entriesToAdd.Add(CreateGlobalWatchJournalEntry(row, capturedAt, row.HealthBadgeText, row.ProfileReason));
        }

        if (entriesToAdd.Count == 0)
        {
            return;
        }

        var settings = _thresholdSettingsStore.Current;
        var journal = settings.WatchJournal;
        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                WatchJournal = journal with
                {
                    Entries = TrimGlobalWatchJournalEntries(entriesToAdd.Concat(journal.Entries), journal.MaxEntries)
                }
            },
            cancellationToken);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            RefreshGlobalWatchJournalCollection();
            GlobalWatchJournalStatusText = $"{entriesToAdd.Count:N0} watch journal entries added at {capturedAt.ToLocalTime():HH:mm:ss}.";
        });
    }

    private async Task UpdateSuspiciousLaunchLoggingAsync(
        IReadOnlyList<GlobalProcessRowViewModel> rows,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var settings = _thresholdSettingsStore.Current;
        var watchlist = settings.SuspiciousWatchlist;
        if (watchlist.Items.Count == 0)
        {
            _runningSuspiciousPaths.Clear();
            return;
        }

        var suspiciousByPath = watchlist.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.NormalizedPath))
            .ToDictionary(item => item.NormalizedPath, StringComparer.OrdinalIgnoreCase);
        var runningByPath = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.NormalizedFullPath)
                && suspiciousByPath.ContainsKey(row.NormalizedFullPath))
            .GroupBy(row => row.NormalizedFullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.CpuPercent ?? 0).First(), StringComparer.OrdinalIgnoreCase);

        var entriesToAdd = new List<SuspiciousLaunchEntry>();
        foreach (var item in suspiciousByPath.Values)
        {
            if (!runningByPath.TryGetValue(item.NormalizedPath, out var row))
            {
                _runningSuspiciousPaths.Remove(item.NormalizedPath);
                continue;
            }

            if (!_runningSuspiciousPaths.Add(item.NormalizedPath))
            {
                continue;
            }

            entriesToAdd.Add(new SuspiciousLaunchEntry
            {
                Id = $"suspicious_launch_{Guid.NewGuid():N}",
                Timestamp = capturedAt,
                NormalizedPath = item.NormalizedPath,
                ExeName = item.ExeName,
                ProductName = string.IsNullOrWhiteSpace(row.ProductName) ? item.ProductName : row.ProductName,
                CompanyName = string.IsNullOrWhiteSpace(row.CompanyName) ? item.CompanyName : row.CompanyName,
                SignerStatus = string.IsNullOrWhiteSpace(row.SignerStatus) ? item.SignerStatus : row.SignerStatus,
                WatchMode = SelectedGlobalWatchMode,
                ParentProcessId = row.Process.ParentProcessId,
                ParentProcessName = row.Process.ParentProcessName
            });
        }

        if (entriesToAdd.Count == 0)
        {
            return;
        }

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                SuspiciousWatchlist = watchlist with
                {
                    LaunchHistory = entriesToAdd
                        .Concat(watchlist.LaunchHistory)
                        .OrderByDescending(item => item.Timestamp)
                        .Take(watchlist.MaxLaunchHistory)
                        .ToList()
                }
            },
            cancellationToken);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            RefreshSuspiciousWatchCollections();
            SuspiciousWatchStatusText = $"{entriesToAdd.Count:N0} suspicious launch transition{(entriesToAdd.Count == 1 ? string.Empty : "s")} logged.";
        });
    }

    private async Task EnforceProcessBansAsync(
        IReadOnlyList<GlobalProcessRowViewModel> rows,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var settings = _thresholdSettingsStore.Current;
        var processBans = settings.ProcessBans;
        if (processBans.Active.Count == 0)
        {
            _runningBannedPaths.Clear();
            return;
        }

        var active = processBans.Active
            .Where(rule => IsProcessBanActive(rule, capturedAt))
            .GroupBy(rule => rule.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(rule => rule.CreatedAt)
                .First())
            .ToList();
        var changed = active.Count != processBans.Active.Count;
        var activeByPath = active
            .Where(rule => !string.IsNullOrWhiteSpace(rule.NormalizedPath))
            .ToDictionary(rule => rule.NormalizedPath, StringComparer.OrdinalIgnoreCase);
        var runningByPath = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.NormalizedFullPath)
                && activeByPath.ContainsKey(row.NormalizedFullPath))
            .GroupBy(row => row.NormalizedFullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var history = processBans.History.ToList();
        var totalTerminated = 0;

        foreach (var rule in active)
        {
            if (!runningByPath.TryGetValue(rule.NormalizedPath, out var matchingRows))
            {
                _runningBannedPaths.Remove(rule.NormalizedPath);
                continue;
            }

            var processIds = matchingRows
                .Select(row => row.ProcessId)
                .Where(processId => processId > 0)
                .Distinct()
                .ToArray();
            if (processIds.Length == 0)
            {
                continue;
            }

            var result = await _processControlService.KillProcessesAsync(
                processIds,
                "active process ban",
                cancellationToken);
            totalTerminated += result.TerminatedCount;

            if (_runningBannedPaths.Add(rule.NormalizedPath)
                || result.TerminatedCount > 0)
            {
                var representative = matchingRows
                    .OrderByDescending(row => row.CpuPercent ?? 0)
                    .First();
                history.Insert(0, CreateProcessBanEvent(
                    rule,
                    "ban_enforced",
                    representative,
                    result.TerminatedCount,
                    result.Messages.Count == 0
                        ? "Active process ban matched a running process."
                        : string.Join(" ", result.Messages.Take(3))));
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                ProcessBans = processBans with
                {
                    Active = active,
                    History = history
                        .OrderByDescending(entry => entry.Timestamp)
                        .Take(processBans.MaxHistory)
                        .ToList()
                }
            },
            cancellationToken);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            RefreshProcessBanCollections();
            if (totalTerminated > 0)
            {
                ProcessBanStatusText = $"Process bans enforced at {capturedAt.ToLocalTime():HH:mm:ss}: {totalTerminated:N0} process{(totalTerminated == 1 ? string.Empty : "es")} terminated.";
            }
        });
    }

    private async Task UpdateProfileRecommendationsFromGlobalWatchAsync(
        IReadOnlyList<GlobalProcessRowViewModel> rows,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var settings = _thresholdSettingsStore.Current;
        var recommendationSettings = settings.Recommendations;
        var active = recommendationSettings.Active.ToList();
        var history = recommendationSettings.History.ToList();
        var watchJournal = settings.WatchJournal;
        var journalEntries = watchJournal.Entries.ToList();
        var changed = false;
        var window = TimeSpan.FromMinutes(recommendationSettings.TriggerWindowMinutes);
        var cutoff = capturedAt - window;

        foreach (var group in rows
                     .Where(row => row.IsOutOfProfile && !string.IsNullOrWhiteSpace(row.ExeName))
                     .GroupBy(row => row.ExeName, StringComparer.OrdinalIgnoreCase))
        {
            var row = group
                .OrderByDescending(item => item.IsCritical)
                .ThenByDescending(item => item.IsOverLimit)
                .ThenByDescending(item => item.CpuPercent ?? 0)
                .ThenByDescending(item => item.MemoryMb ?? 0)
                .First();
            var suggestedProfile = ResolveNextProfile(row.ProfileId, settings);
            if (suggestedProfile is null || IsRecommendationDenied(recommendationSettings, row.ExeName, suggestedProfile.Id))
            {
                continue;
            }

            if (!_globalWatchOverLimitWarnings.TryGetValue(row.ExeName, out var warnings))
            {
                warnings = [];
                _globalWatchOverLimitWarnings[row.ExeName] = warnings;
            }

            warnings.RemoveAll(timestamp => timestamp < cutoff);
            warnings.Add(capturedAt);

            if (warnings.Count < recommendationSettings.TriggerWarningCount)
            {
                continue;
            }

            var recommendationId = CreateRecommendationId(row.ExeName, row.ProfileId, suggestedProfile.Id);
            var existingIndex = active.FindIndex(item => string.Equals(item.Id, recommendationId, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                var existing = active[existingIndex];
                if ((capturedAt - existing.LastSeen).TotalSeconds >= 30)
                {
                    active[existingIndex] = existing with
                    {
                        WarningCount = Math.Max(existing.WarningCount, warnings.Count),
                        LastSeen = capturedAt,
                        Reason = row.RecommendationReason
                    };
                    changed = true;
                }

                continue;
            }

            active.Add(new ProfileRecommendationRecord
            {
                Id = recommendationId,
                ExeName = row.ExeName,
                CurrentProfileId = row.ProfileId,
                CurrentProfileName = row.ProfileName,
                CurrentProfileSource = row.IsAssignedProfile ? "Assigned profile" : "Global fallback",
                SuggestedProfileId = suggestedProfile.Id,
                SuggestedProfileName = suggestedProfile.Name,
                WarningCount = warnings.Count,
                Reason = row.RecommendationReason,
                FirstSeen = warnings.Min(),
                LastSeen = capturedAt
            });
            journalEntries.Insert(0, CreateGlobalWatchJournalEntry(
                row,
                capturedAt,
                "Recommendation",
                $"{row.ExeName} repeatedly exceeded {row.ProfileSourceText}; suggested {suggestedProfile.Name}.",
                recommendationId));
            history = AddRecommendationHistory(
                history,
                "recommendation_created",
                row.ExeName,
                suggestedProfile.Id,
                suggestedProfile.Name,
                $"{row.ExeName} repeatedly exceeded {row.ProfileSourceText}; suggested {suggestedProfile.Name}.");
            changed = true;
        }

        foreach (var key in _globalWatchOverLimitWarnings.Keys.ToArray())
        {
            _globalWatchOverLimitWarnings[key].RemoveAll(timestamp => timestamp < cutoff);
            if (_globalWatchOverLimitWarnings[key].Count == 0)
            {
                _globalWatchOverLimitWarnings.Remove(key);
            }
        }

        if (!changed)
        {
            return;
        }

        await _thresholdSettingsStore.SaveAsync(
            settings with
            {
                Recommendations = recommendationSettings with
                {
                    Active = active
                        .OrderByDescending(item => item.LastSeen)
                        .ToList(),
                    History = history
                },
                WatchJournal = watchJournal with
                {
                    Entries = TrimGlobalWatchJournalEntries(journalEntries, watchJournal.MaxEntries)
                }
            },
            cancellationToken);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            RefreshRecommendationCollections();
            RefreshGlobalWatchJournalCollection();
            ProfileRecommendationStatusText = $"{ProfileRecommendations.Count:N0} active profile recommendations.";
        });
    }

    private static ThresholdProfile? ResolveNextProfile(string profileId, CpuRamThresholdSettings settings)
    {
        var nextProfileId = profileId switch
        {
            ThresholdProfileDefaults.LightBackgroundId => ThresholdProfileDefaults.BrowsersChatsId,
            ThresholdProfileDefaults.BrowsersChatsId => ThresholdProfileDefaults.GamesId,
            ThresholdProfileDefaults.GamesId => ThresholdProfileDefaults.HardcoreId,
            _ => null
        };

        return nextProfileId is null
            ? null
            : settings.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, nextProfileId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRecommendationDenied(
        ProfileRecommendationSettings recommendationSettings,
        string exeName,
        string suggestedProfileId) =>
        recommendationSettings.Denied.Any(item => string.Equals(item.ExeName, exeName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.SuggestedProfileId, suggestedProfileId, StringComparison.OrdinalIgnoreCase));

    private static string CreateRecommendationId(string exeName, string currentProfileId, string suggestedProfileId) =>
        $"{NormalizeExeNameForAssignment(exeName)}|{currentProfileId}|{suggestedProfileId}".ToLowerInvariant();

    private void NotifySelectedGlobalProcessProperties()
    {
        OnPropertyChanged(nameof(SelectedGlobalProcessPanelTitle));
        OnPropertyChanged(nameof(SelectedGlobalProcessTitle));
        OnPropertyChanged(nameof(SelectedGlobalProcessHint));
        OnPropertyChanged(nameof(SelectedGlobalProcessExeText));
        OnPropertyChanged(nameof(SelectedGlobalProcessPathText));
        OnPropertyChanged(nameof(SelectedGlobalProcessProductText));
        OnPropertyChanged(nameof(SelectedGlobalProcessDescriptionText));
        OnPropertyChanged(nameof(SelectedGlobalProcessCompanyText));
        OnPropertyChanged(nameof(SelectedGlobalProcessSignerText));
        OnPropertyChanged(nameof(SelectedGlobalProcessVersionText));
        OnPropertyChanged(nameof(SelectedGlobalProcessOriginalFileText));
        OnPropertyChanged(nameof(SelectedGlobalProcessParentText));
        OnPropertyChanged(nameof(SelectedGlobalProcessDescendantsText));
        OnPropertyChanged(nameof(SelectedGlobalProcessPidText));
        OnPropertyChanged(nameof(SelectedGlobalProcessCpuText));
        OnPropertyChanged(nameof(SelectedGlobalProcessRamText));
        OnPropertyChanged(nameof(SelectedGlobalProcessDiskText));
        OnPropertyChanged(nameof(SelectedGlobalProcessStatusText));
        OnPropertyChanged(nameof(SelectedGlobalProcessProfileText));
        OnPropertyChanged(nameof(SelectedGlobalProcessHealthText));
        OnPropertyChanged(nameof(SelectedGlobalProcessReasonText));
        OnPropertyChanged(nameof(SelectedGlobalProcessIncludedText));
        OnPropertyChanged(nameof(SelectedGlobalProcessWhatItDoesText));
        OnPropertyChanged(nameof(SelectedGlobalProcessInspectorModeText));
        OnPropertyChanged(nameof(SelectedGlobalProcessRelationText));
        OnPropertyChanged(nameof(SelectedGlobalProcessAppearsBecauseText));
        OnPropertyChanged(nameof(SelectedGlobalProcessCommandLineText));
        OnPropertyChanged(nameof(SelectedGlobalProcessSuspiciousLastLaunchText));
        OnPropertyChanged(nameof(CanInspectSelectedGlobalProcess));
        OnPropertyChanged(nameof(CanOpenSelectedGlobalProcessFileLocation));
        OnPropertyChanged(nameof(CanCopySelectedGlobalProcessPath));
        OnPropertyChanged(nameof(CanKillSelectedGlobalProcess));
        OnPropertyChanged(nameof(CanBanSelectedGlobalProcess));
        OnPropertyChanged(nameof(SelectedGlobalProcessKillTreeLabel));
        OnPropertyChanged(nameof(SelectedGlobalProcessBanText));
        OnPropertyChanged(nameof(CanAssignSelectedGlobalProcessProfile));
        OnPropertyChanged(nameof(CanMarkSelectedGlobalProcessSuspicious));
        OnPropertyChanged(nameof(CanRemoveSelectedGlobalProcessSuspicious));
        OnPropertyChanged(nameof(IsSelectedGlobalProcessSuspicious));
        OnPropertyChanged(nameof(SelectedGlobalProcessSuspiciousText));
        OnPropertyChanged(nameof(SelectedGlobalProcessHasRecommendation));
        OnPropertyChanged(nameof(SelectedGlobalProcessRecommendationText));
    }

    private void NotifyInspectorTargetProperties()
    {
        OnPropertyChanged(nameof(HasInspectorTarget));
        OnPropertyChanged(nameof(InspectorTitle));
        OnPropertyChanged(nameof(InspectorModeText));
        OnPropertyChanged(nameof(InspectorSourceText));
        OnPropertyChanged(nameof(InspectorExeText));
        OnPropertyChanged(nameof(InspectorPathText));
        OnPropertyChanged(nameof(InspectorProductText));
        OnPropertyChanged(nameof(InspectorDescriptionText));
        OnPropertyChanged(nameof(InspectorCompanyText));
        OnPropertyChanged(nameof(InspectorSignerText));
        OnPropertyChanged(nameof(InspectorVersionText));
        OnPropertyChanged(nameof(InspectorOriginalFileText));
        OnPropertyChanged(nameof(InspectorParentText));
        OnPropertyChanged(nameof(InspectorDescendantsText));
        OnPropertyChanged(nameof(InspectorPidText));
        OnPropertyChanged(nameof(InspectorCpuText));
        OnPropertyChanged(nameof(InspectorRamText));
        OnPropertyChanged(nameof(InspectorDiskText));
        OnPropertyChanged(nameof(InspectorStatusText));
        OnPropertyChanged(nameof(InspectorProfileText));
        OnPropertyChanged(nameof(InspectorHealthText));
        OnPropertyChanged(nameof(InspectorReasonText));
        OnPropertyChanged(nameof(InspectorIncludedText));
        OnPropertyChanged(nameof(InspectorRelationText));
        OnPropertyChanged(nameof(InspectorAppearsBecauseText));
        OnPropertyChanged(nameof(InspectorCommandLineText));
        OnPropertyChanged(nameof(InspectorIsGroup));
        OnPropertyChanged(nameof(InspectorIncludedProcessRows));
        OnPropertyChanged(nameof(InspectorWhatItDoesText));
        OnPropertyChanged(nameof(InspectorSuspiciousLastLaunchText));
        OnPropertyChanged(nameof(CanMonitorInspectorTarget));
        OnPropertyChanged(nameof(CanOpenInspectorFileLocation));
        OnPropertyChanged(nameof(CanCopyInspectorPath));
        OnPropertyChanged(nameof(CanKillInspectorTarget));
        OnPropertyChanged(nameof(CanBanInspectorTarget));
        OnPropertyChanged(nameof(CanMarkInspectorSuspicious));
        OnPropertyChanged(nameof(CanRemoveInspectorSuspicious));
        OnPropertyChanged(nameof(IsInspectorTargetSuspicious));
        OnPropertyChanged(nameof(InspectorKillTreeLabel));
        OnPropertyChanged(nameof(InspectorSuspiciousText));
        OnPropertyChanged(nameof(InspectorBanText));
        OnPropertyChanged(nameof(InspectorHasRecommendation));
        OnPropertyChanged(nameof(InspectorRecommendationText));
    }

}
