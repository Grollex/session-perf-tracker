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
    public async Task RefreshRunningProcessesAsync(CancellationToken cancellationToken = default)
    {
        var targets = await _targetResolver.ListRunningTargetsAsync(cancellationToken);
        _allRunningTargets.Clear();
        _allRunningTargets.AddRange(targets.Select(target => new TargetOptionViewModel(target)));
        ApplyProcessFilter();
        SelectedRunningTarget ??= RunningTargets.FirstOrDefault();
        RefreshAssignedTargets();
        NotifyTargetSelectionProperties();
    }

    public void SelectExecutable(string path)
    {
        SelectedExecutablePath = path;
        SelectedTargetMode = TargetModeExecutable;
    }

    public async Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (!CanStart)
        {
            RecordingStatusText = TargetReadinessText;
            return;
        }

        try
        {
            var target = await ResolveSelectedRecordingTargetAsync(cancellationToken);
            if (target is null)
            {
                RecordingStatusText = TargetReadinessText;
                return;
            }

            var sampling = CreateSamplingSettings(target);

            _activeHandle = target.LifecycleMode == TargetLifecycleMode.LaunchAndTrack
                ? await _sessionRunner.StartAsync(target, sampling, cancellationToken)
                : await _sessionRunner.AttachAsync(target, sampling, cancellationToken);

            _activeHandle.SampleCollected += OnSampleCollected;
            _activeHandle.EventsDetected += OnEventsDetected;
            _activeHandle.Completed += OnSessionCompleted;
            _ = WatchActiveSessionAsync(_activeHandle);
            _activeTargetName = _activeHandle.Target.DisplayName;
            _activeSessionProfileText = sampling.ThresholdSourceLabel ?? ResolveActiveThresholdSourceText(target);
            _activeStartedAt = DateTimeOffset.UtcNow;
            _liveSampleCount = 0;
            _liveEventCount = 0;
            _liveSpikeCount = 0;
            _liveBreachCount = 0;
            _liveHangCount = 0;
            _liveTrackedProcessCount = 0;
            _liveRootProcessId = _activeHandle.Target.ProcessId;
            _lastLiveUiRefresh = DateTimeOffset.MinValue;
            _liveCapabilities = CreateLiveCapabilities(sampling);
            RecordingStatusText = $"Recording {_activeTargetName}";
            LiveWarningText = string.Empty;
            LiveSnapshotText = $"sampling {SelectedSamplingOption.Label}";
            CurrentMetrics.Clear();
            VisibleEvents.Clear();
            _manualStopInProgress = false;
            SetLiveSessionState(LiveSessionUiState.Recording);
            NotifyLivePanelProperties();
            IsRecording = true;
            NotifyTargetSelectionProperties();
            NotifySummaryProperties();
        }
        catch (Exception error)
        {
            RecordingStatusText = $"Start failed: {error.Message}";
            LiveWarningText = $"Start failed: {error.Message}";
            LiveSnapshotText = "not recording";
            CurrentMetrics.Clear();
            VisibleEvents.Clear();
            IsRecording = false;
            SetLiveSessionState(LiveSessionUiState.EndedUnexpectedly);
            NotifyLivePanelProperties();
        }
    }

    public async Task StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (_activeHandle is null)
        {
            return;
        }

        try
        {
            RecordingStatusText = "Stopping and saving session";
            _manualStopInProgress = true;
            await _activeHandle.StopAsync(cancellationToken);
        }
        catch (Exception error)
        {
            _manualStopInProgress = false;
            RecordingStatusText = $"Stop failed: {error.Message}";
        }
    }

    public async Task CaptureRamDiagnosticAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var target = _activeHandle?.Target;
            if (target is null)
            {
                target = await ResolveSelectedAttachTargetAsync(cancellationToken);
                if (target is null)
                {
                    RamDiagnosticText = "Select a running target first.";
                    return;
                }
            }

            var snapshot = await _ramDiagnosticProvider.CaptureAsync(target, cancellationToken);
            RamDiagnosticText = FormatRamDiagnostic(snapshot);
        }
        catch (Exception error)
        {
            RamDiagnosticText = $"RAM diagnostic failed: {error.Message}";
        }
    }

    public async Task CaptureSystemContextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var target = _activeHandle?.Target;
            if (target is null)
            {
                target = await ResolveSelectedAttachTargetAsync(cancellationToken);
                if (target is null)
                {
                    SystemContextText = "Select a running target first.";
                    return;
                }
            }

            var now = DateTimeOffset.UtcNow;
            var manualEvent = new PerformanceEvent
            {
                Id = $"manual_context_{now:yyyyMMdd_HHmmss}",
                SessionId = _activeHandle?.SessionId ?? "manual_context",
                Kind = EventKind.Spike,
                Timestamp = now,
                ElapsedMs = _activeStartedAt is null ? 0 : (long)(now - _activeStartedAt.Value).TotalMilliseconds,
                Severity = EventSeverity.Info,
                Title = "Manual system context snapshot",
                Details = "Manual best-effort snapshot.",
                DetectionProvider = "manual"
            };

            _activeHandle?.SuppressDetectorNoise(
                TimeSpan.FromSeconds(_thresholdSettingsStore.Current.AntiNoise.SnapshotSuppressionSeconds),
                "manual system context snapshot");

            var snapshot = await _spikeContextProvider.CaptureAsync(
                new SpikeContextInput(manualEvent.SessionId, target, manualEvent, LookbackMs: 5000, LookaheadMs: 0),
                cancellationToken);

            SystemContextText = snapshot is null
                ? "System context snapshot unavailable."
                : FormatSystemContext(snapshot);
        }
        catch (Exception error)
        {
            SystemContextText = $"System context snapshot failed: {error.Message}";
        }
    }

    private void OnSampleCollected(object? sender, MetricSample sample)
    {
        var now = DateTimeOffset.UtcNow;
        var liveRefreshMs = 2000;
        _liveTrackedProcessCount = sample.ProcessCount;
        _liveRootProcessId = sample.RootProcessId ?? _liveRootProcessId;
        if ((now - _lastLiveUiRefresh).TotalMilliseconds < liveRefreshMs)
        {
            _liveSampleCount++;
            return;
        }

        _lastLiveUiRefresh = now;
        _liveSampleCount++;

        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (IsLiveMonitorEnabled)
            {
                CurrentMetrics.ReplaceWith(MetricDisplayFactory.CreateMetricRows(sample, _liveCapabilities));
            }

            LiveSnapshotText = $"latest sample +{TimeSpan.FromMilliseconds(sample.ElapsedMs):mm\\:ss}";
            NotifyLivePanelProperties();
            NotifySummaryProperties();
        });
    }

    private void OnEventsDetected(object? sender, IReadOnlyList<PerformanceEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var performanceEvent in events.OrderByDescending(item => item.ElapsedMs))
            {
                VisibleEvents.Insert(0, new EventRowViewModel(performanceEvent));
            }

            while (VisibleEvents.Count > 12)
            {
                VisibleEvents.RemoveAt(VisibleEvents.Count - 1);
            }

            _liveEventCount += events.Count;
            _liveSpikeCount += events.Count(item => HasEventKind(item, EventKind.Spike));
            _liveBreachCount += events.Count(item => HasEventKind(item, EventKind.ThresholdBreach));
            _liveHangCount += events.Count(item => HasEventKind(item, EventKind.HangSuspected));
            NotifyLivePanelProperties();
            NotifySummaryProperties();
        });
    }

    private void ApplyProcessFilter()
    {
        var query = ProcessFilterText.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allRunningTargets
            : _allRunningTargets
                .Where(target => target.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var previousProcessId = SelectedRunningTarget?.ProcessId;
        RunningTargets.ReplaceWith(filtered);
        SelectedRunningTarget = previousProcessId is null
            ? RunningTargets.FirstOrDefault()
            : RunningTargets.FirstOrDefault(target => target.ProcessId == previousProcessId) ?? RunningTargets.FirstOrDefault();
    }

    private void RefreshAssignedTargets()
    {
        var previousExeName = SelectedAssignedTarget?.ExeName;
        var profileNames = _thresholdSettingsStore.Current.Profiles
            .ToDictionary(profile => profile.Id, profile => profile.Name, StringComparer.OrdinalIgnoreCase);

        AssignedTargets.ReplaceWith(_thresholdSettingsStore.Current.AppProfileAssignments
            .OrderBy(assignment => assignment.Key, StringComparer.OrdinalIgnoreCase)
            .Select(assignment =>
            {
                var exeName = NormalizeExeNameForAssignment(assignment.Key);
                var runningTarget = FindRunningTargetForExe(exeName);
                return new AssignedTargetOptionViewModel(
                    exeName,
                    assignment.Value,
                    profileNames.TryGetValue(assignment.Value, out var profileName) ? profileName : assignment.Value,
                    runningTarget?.ProcessId,
                    runningTarget?.DisplayName);
            }));

        SelectedAssignedTarget = !string.IsNullOrWhiteSpace(previousExeName)
            ? AssignedTargets.FirstOrDefault(target => string.Equals(target.ExeName, previousExeName, StringComparison.OrdinalIgnoreCase))
                ?? AssignedTargets.FirstOrDefault(target => target.IsRunning)
                ?? AssignedTargets.FirstOrDefault()
            : AssignedTargets.FirstOrDefault(target => target.IsRunning) ?? AssignedTargets.FirstOrDefault();
    }

    private TargetOptionViewModel? FindRunningTargetForExe(string exeName)
    {
        var normalizedExeName = NormalizeExeNameForAssignment(exeName);
        return _allRunningTargets.FirstOrDefault(target =>
            string.Equals(
                NormalizeExeNameForAssignment(GetExeNameFromTarget(target.Target)),
                normalizedExeName,
                StringComparison.OrdinalIgnoreCase));
    }

    private void OnSessionCompleted(object? sender, SessionRecord session)
    {
        if (_activeHandle is not null)
        {
            _activeHandle.SampleCollected -= OnSampleCollected;
            _activeHandle.EventsDetected -= OnEventsDetected;
            _activeHandle.Completed -= OnSessionCompleted;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var wasAutoStopped = _autoStopInProgress;
            var wasManualStop = _manualStopInProgress;
            UpdateLastCompletedSession(session);
            var nextState = ResolveCompletedLiveState(session, wasAutoStopped, wasManualStop);
            IsRecording = false;
            RecordingStatusText = wasAutoStopped
                ? _autoStopStatusText
                : $"Saved {session.Target.DisplayName}";
            LiveWarningText = wasAutoStopped
                ? _autoStopStatusText
                : string.Empty;
            LiveSnapshotText = "not recording";
            CurrentMetrics.Clear();
            VisibleEvents.Clear();
            _liveTrackedProcessCount = 0;
            _liveRootProcessId = null;
            _activeHandle = null;
            _activeStartedAt = null;
            _activeSessionProfileText = string.Empty;
            _autoStopInProgress = false;
            _manualStopInProgress = false;
            _autoStopStatusText = GetText("Ui_MonitoringStoppedChanges");
            SetLiveSessionState(nextState);
            NotifyLivePanelProperties();
            NotifyTargetSelectionProperties();
            await ReloadSessionsAsync(session.Id);
        });
    }

    private async Task WatchActiveSessionAsync(IRunningSessionHandle handle)
    {
        try
        {
            await handle.Completion;
        }
        catch (Exception error)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsRecording = false;
                RecordingStatusText = $"Recording failed: {error.Message}";
                LiveWarningText = $"Recording failed: {error.Message}";
                LiveSnapshotText = "failed";
                CurrentMetrics.Clear();
                VisibleEvents.Clear();
                _activeHandle = null;
                _activeStartedAt = null;
                _activeSessionProfileText = string.Empty;
                _autoStopInProgress = false;
                _manualStopInProgress = false;
                _liveEventCount = 0;
                _liveSpikeCount = 0;
                _liveBreachCount = 0;
                _liveHangCount = 0;
                _liveTrackedProcessCount = 0;
                _liveRootProcessId = null;
                SetLiveSessionState(LiveSessionUiState.EndedUnexpectedly);
                NotifyLivePanelProperties();
                NotifyTargetSelectionProperties();
                NotifySummaryProperties();
            });
        }
    }

    private void StartSelfMonitoring()
    {
        if (_selfMonitoringCts is not null)
        {
            return;
        }

        _selfMonitoringCts = new CancellationTokenSource();
        _ = RunSelfMonitoringAsync(_selfMonitoringCts.Token);
    }

    private async Task RunSelfMonitoringAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var sample = await _selfMonitoringProvider.SampleSelfAsync(cancellationToken);
                _selfMonitoringSamples.Add(sample);
                if (_selfMonitoringSamples.Count > MaxSelfMonitoringSamples)
                {
                    _selfMonitoringSamples.RemoveRange(0, _selfMonitoringSamples.Count - MaxSelfMonitoringSamples);
                }

                var summary = await _selfMonitoringProvider.SummarizeAsync(_selfMonitoringSamples, cancellationToken);
                await Application.Current.Dispatcher.InvokeAsync(() => UpdateSelfMonitoringUi(sample, summary));
                await Task.Delay(SelfMonitoringIntervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                SelfMonitoringStatusText = $"self-monitoring failed: {error.Message}";
            });
        }
    }

    private void UpdateSelfMonitoringUi(SelfMonitoringSample sample, SelfOverheadSummary summary)
    {
        SelfMonitoringStatusText = "active";
        SelfCurrentCpuText = FormatPercent(sample.CpuPercent);
        SelfAvgCpuText = FormatPercent(summary.AvgCpuPercent);
        SelfPeakCpuText = FormatPercent(summary.MaxCpuPercent);
        SelfCurrentRamText = FormatMb(sample.MemoryMb);
        SelfAvgRamText = FormatMb(summary.AvgMemoryMb);
        SelfPeakRamText = FormatMb(summary.MaxMemoryMb);
        SelfDiskWriteText = FormatMbPerSec(sample.DiskWriteMbPerSec);
        SelfSampleCountText = summary.SampleCount.ToString("N0");
        SelfCpuBudgetStatusText = FormatBudget(summary.AvgCpuPercent, AvgCpuBudgetPercent);
        SelfRamBudgetStatusText = FormatBudget(summary.MaxMemoryMb, PeakRamBudgetMb);
        SelfWritesBudgetStatusText = "configured";
        SelfSnapshotsBudgetStatusText = "event-only";
    }

    private void NotifySummaryProperties()
    {
        OnPropertyChanged(nameof(TargetName));
        OnPropertyChanged(nameof(StartedText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SampleCountText));
        OnPropertyChanged(nameof(TrackedProcessCountText));
        OnPropertyChanged(nameof(EventCountText));
        OnPropertyChanged(nameof(SpikeCountText));
        OnPropertyChanged(nameof(BreachCountText));
        OnPropertyChanged(nameof(HangCountText));
        OnPropertyChanged(nameof(CurrentMonitoringStripText));
        NotifySessionStateProperties();
    }

    private void NotifyTargetSelectionProperties()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(IsRunningProcessMode));
        OnPropertyChanged(nameof(IsAssignedAppsMode));
        OnPropertyChanged(nameof(IsExecutableMode));
        OnPropertyChanged(nameof(TargetReadinessText));
        OnPropertyChanged(nameof(ActiveThresholdSourceText));
        NotifyLiveConfigProperties();
        NotifySessionStateProperties();
    }

    private void NotifySessionStateProperties()
    {
        OnPropertyChanged(nameof(HasCrossScreenMonitoringStrip));
        OnPropertyChanged(nameof(CrossScreenMonitoringStripText));
        OnPropertyChanged(nameof(LiveSessionStateText));
        OnPropertyChanged(nameof(LiveSessionStateTone));
        OnPropertyChanged(nameof(LiveSessionTargetText));
        OnPropertyChanged(nameof(LiveSessionMetaText));
        OnPropertyChanged(nameof(LiveSessionHintText));
        OnPropertyChanged(nameof(HasLastCompletedSession));
        OnPropertyChanged(nameof(LastCompletedSessionText));
        OnPropertyChanged(nameof(LastCompletedSessionHint));
    }

    private void NotifyLiveConfigProperties()
    {
        OnPropertyChanged(nameof(LiveConfigTargetText));
        OnPropertyChanged(nameof(LiveConfigCollectorsText));
        OnPropertyChanged(nameof(LiveConfigSessionProfileText));
        OnPropertyChanged(nameof(LiveConfigSamplingText));
        OnPropertyChanged(nameof(LiveConfigChildScopeText));
        OnPropertyChanged(nameof(LiveConfigMonitorText));
        OnPropertyChanged(nameof(LiveSnapshotScopeText));
    }

    private void NotifyLivePanelProperties()
    {
        OnPropertyChanged(nameof(HasActiveLiveMetrics));
        OnPropertyChanged(nameof(ShowLiveSnapshotEmptyState));
        OnPropertyChanged(nameof(LiveSnapshotTitle));
        OnPropertyChanged(nameof(LiveSnapshotStatusText));
        OnPropertyChanged(nameof(LiveSnapshotEmptyText));
        OnPropertyChanged(nameof(LiveSnapshotScopeText));
        OnPropertyChanged(nameof(HasLiveEvents));
        OnPropertyChanged(nameof(ShowLiveEventEmptyState));
        OnPropertyChanged(nameof(LiveEventEmptyText));
    }

    private void SetLiveSessionState(LiveSessionUiState state)
    {
        if (_liveSessionState == state)
        {
            return;
        }

        _liveSessionState = state;
        NotifySessionStateProperties();
        NotifySummaryProperties();
    }

    private void MarkReadyForConfigurationChange()
    {
        NotifyLiveConfigProperties();
        if (IsRecording || _liveSessionState == LiveSessionUiState.ChangesPending)
        {
            return;
        }

        SetLiveSessionState(LiveSessionUiState.ReadyToStart);
    }

    private void UpdateLastCompletedSession(SessionRecord session)
    {
        var item = new SessionListItemViewModel(session);
        _lastCompletedSessionText = $"{item.AppName} - {item.StartedFullText} - {item.DurationText} - {item.StatusText}";
        _lastCompletedSessionHint = item.ExitText;
        OnPropertyChanged(nameof(HasLastCompletedSession));
        OnPropertyChanged(nameof(LastCompletedSessionText));
        OnPropertyChanged(nameof(LastCompletedSessionHint));
    }

    private static LiveSessionUiState ResolveCompletedLiveState(
        SessionRecord session,
        bool wasAutoStopped,
        bool wasManualStop)
    {
        if (wasAutoStopped)
        {
            return LiveSessionUiState.ChangesPending;
        }

        if (session.Status is SessionStatus.UnexpectedExit or SessionStatus.CrashLikeExit)
        {
            return LiveSessionUiState.EndedUnexpectedly;
        }

        if (wasManualStop || session.Summary.ExitKind == SessionExitKind.NormalStop)
        {
            return LiveSessionUiState.StoppedManually;
        }

        return LiveSessionUiState.Stopped;
    }

    private void StopForParameterChange(string reason)
    {
        if (!IsRecording || _activeHandle is null || _autoStopInProgress)
        {
            return;
        }

        _autoStopInProgress = true;
        _manualStopInProgress = false;
        _autoStopStatusText = GetText("Ui_MonitoringStoppedChanges");
        RecordingStatusText = _autoStopStatusText;
        LiveWarningText = _autoStopStatusText;
        LiveSnapshotText = $"stopping: {reason}";
        SetLiveSessionState(LiveSessionUiState.ChangesPending);
        _ = StopAfterParameterChangeAsync(_activeHandle);
    }

    private void StopForCaptureChange(string reason)
    {
        if (!IsRecording || _activeHandle is null || _autoStopInProgress)
        {
            return;
        }

        _autoStopInProgress = true;
        _manualStopInProgress = false;
        _autoStopStatusText = GetText("Ui_MonitoringStoppedCapture");
        RecordingStatusText = _autoStopStatusText;
        LiveWarningText = _autoStopStatusText;
        LiveSnapshotText = $"stopping: {reason}";
        SetLiveSessionState(LiveSessionUiState.ChangesPending);
        _ = StopAfterParameterChangeAsync(_activeHandle);
    }

    private static async Task StopAfterParameterChangeAsync(IRunningSessionHandle handle)
    {
        try
        {
            await handle.StopAsync();
        }
        catch
        {
        }
    }

    private async Task<TargetDescriptor?> ResolveSelectedRecordingTargetAsync(CancellationToken cancellationToken)
    {
        var mode = SelectedTargetMode;
        if (mode == TargetModeExecutable)
            return await _targetResolver.ResolveExecutableAsync(SelectedExecutablePath, IncludeChildProcesses, cancellationToken);
        if (mode == TargetModeAssignedApps && SelectedAssignedTarget?.RunningProcessId is int processId)
            return await _targetResolver.ResolveProcessAsync(processId, IncludeChildProcesses, cancellationToken);
        if (mode == TargetModeRunningProcess && SelectedRunningTarget is not null)
            return await _targetResolver.ResolveProcessAsync(SelectedRunningTarget.ProcessId, IncludeChildProcesses, cancellationToken);
        return null;
    }

    private SamplingSettings CreateSamplingSettings(TargetDescriptor target)
    {
        var exeName = GetExeNameFromTarget(target);
        var assignedProfile = ResolveAssignedProfile(exeName);
        var manualSessionProfile = SelectedSessionProfileOption?.IsAuto == false
            ? _thresholdSettingsStore.Current.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, SelectedSessionProfileOption.ProfileId, StringComparison.OrdinalIgnoreCase))
            : null;
        var sourceLabel = ResolveActiveThresholdSourceText(target);
        var sampling = new SamplingSettings
        {
            IntervalMs = SelectedSamplingOption.IntervalMs,
            LiveUiRefreshMs = 2000,
            StorageBatchSize = 10,
            CaptureCpu = CaptureCpu,
            CaptureRam = CaptureRam,
            CaptureDiskRead = CaptureDiskRead,
            CaptureDiskWrite = CaptureDiskWrite,
            SessionProfileMode = manualSessionProfile is not null
                ? "Manual"
                : assignedProfile is not null ? "ProcessAssignment" : "Auto",
            ThresholdSourceLabel = sourceLabel
        };

        if (manualSessionProfile is not null)
        {
            sampling = sampling with
            {
                SessionProfileId = manualSessionProfile.Id,
                SessionProfileName = manualSessionProfile.Name,
                SessionThresholds = manualSessionProfile.Limits
            };
        }
        else if (assignedProfile is not null)
        {
            sampling = sampling with
            {
                SessionProfileId = assignedProfile.Id,
                SessionProfileName = assignedProfile.Name,
                SessionThresholds = assignedProfile.Limits
            };
        }

        return sampling;
    }

    private async Task<TargetDescriptor?> ResolveSelectedAttachTargetAsync(CancellationToken cancellationToken)
    {
        var mode = SelectedTargetMode;
        if (mode == TargetModeAssignedApps && SelectedAssignedTarget?.RunningProcessId is int processId)
            return await _targetResolver.ResolveProcessAsync(processId, IncludeChildProcesses, cancellationToken);
        if (mode == TargetModeRunningProcess && SelectedRunningTarget is not null)
            return await _targetResolver.ResolveProcessAsync(SelectedRunningTarget.ProcessId, IncludeChildProcesses, cancellationToken);
        return null;
    }

    private string ResolveActiveThresholdSourceText()
    {
        if (IsRecording && !string.IsNullOrWhiteSpace(_activeSessionProfileText))
        {
            return _activeSessionProfileText;
        }

        return ResolveActiveThresholdSourceText(GetSelectedTargetDescriptorForThresholdSource());
    }

    private string ResolveActiveThresholdSourceText(TargetDescriptor? target)
    {
        var exeName = target is null ? GetSelectedTargetExeNameForThresholdSource() : GetExeNameFromTarget(target);
        if (SelectedSessionProfileOption?.IsAuto == false)
        {
            return $"Session profile: {SelectedSessionProfileOption.ProfileName}";
        }

        var assignedProfile = ResolveAssignedProfile(exeName);
        if (assignedProfile is not null)
        {
            return $"Session profile: Auto -> {assignedProfile.Name}";
        }

        return "Session profile: Auto -> Global fallback";
    }

    private TargetDescriptor? GetSelectedTargetDescriptorForThresholdSource() => SelectedTargetMode switch
    {
        var mode when mode == TargetModeAssignedApps => null,
        var mode when mode == TargetModeExecutable => string.IsNullOrWhiteSpace(SelectedExecutablePath)
            ? null
            : new TargetDescriptor
            {
                Id = "selected_executable",
                DisplayName = Path.GetFileName(SelectedExecutablePath),
                ExecutablePath = SelectedExecutablePath,
                Kind = TargetSelectionKind.Executable,
                LifecycleMode = TargetLifecycleMode.LaunchAndTrack
            },
        _ => SelectedRunningTarget?.Target
    };

    private string? GetSelectedTargetExeNameForThresholdSource() => SelectedTargetMode switch
    {
        var mode when mode == TargetModeAssignedApps => SelectedAssignedTarget?.ExeName,
        var mode when mode == TargetModeExecutable => Path.GetFileName(SelectedExecutablePath),
        _ => SelectedRunningTarget is null ? null : GetExeNameFromTarget(SelectedRunningTarget.Target)
    };

    private ThresholdProfile? ResolveAssignedProfile(string? exeName)
    {
        var normalizedExeName = NormalizeExeNameForAssignment(exeName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(normalizedExeName)
            && _thresholdSettingsStore.Current.AppProfileAssignments.TryGetValue(normalizedExeName, out var profileId)
            && _thresholdSettingsStore.Current.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)) is { } profile)
        {
            return profile;
        }

        return null;
    }

    private static MetricCapabilities CreateLiveCapabilities(SamplingSettings sampling) => new()
    {
        CpuPercent = sampling.CaptureCpu ? MetricReliability.Stable : MetricReliability.Unavailable,
        MemoryMb = sampling.CaptureRam ? MetricReliability.Stable : MetricReliability.Unavailable,
        DiskReadMbPerSec = sampling.CaptureDiskRead ? MetricReliability.BestEffort : MetricReliability.Unavailable,
        DiskWriteMbPerSec = sampling.CaptureDiskWrite ? MetricReliability.BestEffort : MetricReliability.Unavailable,
        GpuPercent = MetricReliability.Unavailable,
        TemperatureC = MetricReliability.Unavailable
    };

    private string FormatThresholdSource(string? exeName)
    {
        return ResolveActiveThresholdSourceText(new TargetDescriptor
        {
            Id = "selected_exe",
            DisplayName = exeName ?? string.Empty,
            Kind = TargetSelectionKind.Process,
            LifecycleMode = TargetLifecycleMode.AttachToRunning
        });
    }

    private static bool SameSessionProfile(SessionProfileOptionViewModel? left, SessionProfileOptionViewModel? right) =>
        left?.IsAuto == right?.IsAuto
        && string.Equals(left?.ProfileId, right?.ProfileId, StringComparison.OrdinalIgnoreCase);

    private static bool SameAssignedTarget(AssignedTargetOptionViewModel? left, AssignedTargetOptionViewModel? right) =>
        string.Equals(left?.ExeName, right?.ExeName, StringComparison.OrdinalIgnoreCase)
        && left?.RunningProcessId == right?.RunningProcessId
        && string.Equals(left?.ProfileId, right?.ProfileId, StringComparison.OrdinalIgnoreCase);

    private static bool HasEventKind(PerformanceEvent performanceEvent, EventKind kind) =>
        performanceEvent.Kind == kind || performanceEvent.GroupedKinds.Contains(kind);

}
