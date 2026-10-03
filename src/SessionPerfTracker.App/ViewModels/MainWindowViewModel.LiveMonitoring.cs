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
            _activeSessionLimits = sampling.SessionThresholds ?? _thresholdSettingsStore.ResolveThresholds(target);
            _latestLiveSample = null;
            _liveHistory.Clear();
            ResetLiveTiles();
            RecordingStatusText = FormatText("Ui_RecordingTarget", _activeTargetName);
            LiveWarningText = string.Empty;
            LiveSnapshotText = FormatText("Ui_SamplingEvery", SelectedSamplingOption.Label);
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
            RecordingStatusText = FormatText("Ui_StartFailed", error.Message);
            LiveWarningText = FormatText("Ui_StartFailed", error.Message);
            LiveSnapshotText = GetText("Ui_LiveNotRecording");
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
            RecordingStatusText = GetText("Ui_StoppingAndSaving");
            _manualStopInProgress = true;
            await _activeHandle.StopAsync(cancellationToken);
        }
        catch (Exception error)
        {
            _manualStopInProgress = false;
            RecordingStatusText = FormatText("Ui_StopFailed", error.Message);
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

    /// <summary>Runs on the sampling thread. Only bookkeeping here; rendering is throttled and skipped when hidden.</summary>
    private void OnSampleCollected(object? sender, MetricSample sample)
    {
        Interlocked.Increment(ref _liveSampleCount);
        _liveTrackedProcessCount = sample.ProcessCount;
        _liveRootProcessId = sample.RootProcessId ?? _liveRootProcessId;
        _latestLiveSample = sample;
        _liveHistory.Add(sample);

        var now = DateTimeOffset.UtcNow;
        var visibleOnLive = IsLiveSurfaceVisible;
        var refreshInterval = visibleOnLive ? LiveUiRefreshInterval : HiddenStripRefreshInterval;
        if (!_isUiVisible || now - _lastLiveUiRefresh < refreshInterval)
        {
            return;
        }

        _lastLiveUiRefresh = now;
        _ = Application.Current.Dispatcher.InvokeAsync(
            visibleOnLive ? RefreshLiveSurface : NotifySummaryProperties,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private static readonly TimeSpan LiveUiRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HiddenStripRefreshInterval = TimeSpan.FromSeconds(5);

    private void InitializeLiveTiles()
    {
        if (LiveTiles.Count > 0)
        {
            return;
        }

        LiveTiles.Add(new LiveMetricTileViewModel("cpu", GetText("Ui_CPU"), 100, "cpu"));
        LiveTiles.Add(new LiveMetricTileViewModel("ram", GetText("Ui_RAM"), 0, "ram"));
        LiveTiles.Add(new LiveMetricTileViewModel("gpu", "GPU", 100, "gpu"));
        LiveTiles.Add(new LiveMetricTileViewModel("disk", GetText("Ui_Disk"), 0, "disk"));
        ResetLiveTiles();
    }

    private void ResetLiveTiles()
    {
        foreach (var tile in LiveTiles)
        {
            tile.ValueText = "—";
            tile.DetailText = string.Empty;
            tile.Tone = "normal";
            tile.History = [];
            tile.IsAvailable = tile.Key != "gpu" || _liveCapabilities.GpuPercent != MetricReliability.Unavailable;
        }

        _liveTilesHaveData = false;
    }

    /// <summary>Pushes the latest sample and recent history into the KPI tiles (UI thread).</summary>
    private void RefreshLiveSurface()
    {
        var sample = _latestLiveSample;
        if (IsRecording && IsLiveMonitorEnabled && sample is not null)
        {
            var values = sample.Values;
            var limits = _activeSessionLimits;
            foreach (var tile in LiveTiles)
            {
                switch (tile.Key)
                {
                    case "cpu":
                        UpdateTile(tile, values, MetricKey.CpuPercent, value => $"{value:N1}%", limits?.CpuThresholdPercent);
                        tile.DetailText = FormatText("Ui_LiveTileProcesses", sample.ProcessCount);
                        break;
                    case "ram":
                        UpdateTile(tile, values, MetricKey.MemoryMb, FormatMemory, limits?.RamThresholdMb);
                        tile.DetailText = GetText("Ui_LiveTilePrivateWorkingSet");
                        break;
                    case "gpu":
                        var gpuLimit = limits is null || limits.EffectiveGpuThresholdPercent >= 100 ? (double?)null : limits.EffectiveGpuThresholdPercent;
                        UpdateTile(tile, values, MetricKey.GpuPercent, value => $"{value:N1}%", gpuLimit);
                        var gpuMemory = values.TryGetValue(MetricKey.GpuMemoryMb, out var vram) ? FormatMemory(vram) : "—";
                        tile.DetailText = values.TryGetValue(MetricKey.TemperatureC, out var temperature)
                            ? FormatText("Ui_LiveTileGpuDetailTemp", gpuMemory, temperature)
                            : FormatText("Ui_LiveTileGpuDetail", gpuMemory);
                        break;
                    case "disk":
                        values.TryGetValue(MetricKey.DiskReadMbPerSec, out var read);
                        values.TryGetValue(MetricKey.DiskWriteMbPerSec, out var write);
                        var hasDisk = values.ContainsKey(MetricKey.DiskReadMbPerSec) || values.ContainsKey(MetricKey.DiskWriteMbPerSec);
                        tile.IsAvailable = hasDisk;
                        tile.ValueText = hasDisk ? $"{read + write:N1} MB/s" : "—";
                        tile.DetailText = hasDisk ? FormatText("Ui_LiveTileDiskDetail", read, write) : string.Empty;
                        tile.Tone = LiveMetricTileViewModel.ResolveTone(
                            Math.Max(read / Math.Max(0.1, limits?.DiskReadThresholdMbPerSec ?? double.MaxValue),
                                write / Math.Max(0.1, limits?.DiskWriteThresholdMbPerSec ?? double.MaxValue)),
                            1);
                        tile.History = _liveHistory.Snapshot("disk");
                        break;
                }
            }

            _liveTilesHaveData = true;
            LiveSnapshotText = FormatText("Ui_LatestSampleAt", MainWindowViewModel.FormatDuration(TimeSpan.FromMilliseconds(sample.ElapsedMs)));
        }

        NotifyLivePanelProperties();
        NotifySummaryProperties();
    }

    private void UpdateTile(
        LiveMetricTileViewModel tile,
        IReadOnlyDictionary<MetricKey, double> values,
        MetricKey key,
        Func<double, string> format,
        double? limit)
    {
        if (!values.TryGetValue(key, out var value))
        {
            tile.IsAvailable = false;
            tile.ValueText = "—";
            tile.Tone = "normal";
            return;
        }

        tile.IsAvailable = true;
        tile.ValueText = format(value);
        tile.Tone = LiveMetricTileViewModel.ResolveTone(value, limit);
        tile.History = _liveHistory.Snapshot(tile.Key);
    }

    private static string FormatMemory(double megabytes) =>
        megabytes >= 1024 ? $"{megabytes / 1024:N2} GB" : $"{megabytes:N0} MB";

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
                : FormatText("Ui_SavedSessionFor", session.Target.DisplayName);
            LiveWarningText = wasAutoStopped
                ? _autoStopStatusText
                : string.Empty;
            LiveSnapshotText = GetText("Ui_LiveNotRecording");
            CurrentMetrics.Clear();
            VisibleEvents.Clear();
            _latestLiveSample = null;
            _liveTilesHaveData = false;
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
                RecordingStatusText = FormatText("Ui_RecordingFailed", error.Message);
                LiveWarningText = FormatText("Ui_RecordingFailed", error.Message);
                LiveSnapshotText = GetText("Ui_LiveNotRecording");
                _latestLiveSample = null;
                _liveTilesHaveData = false;
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
        var token = _selfMonitoringCts.Token;
        _ = Task.Run(() => RunSelfMonitoringAsync(token), token);
    }

    private async Task RunSelfMonitoringAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var sample = await _selfMonitoringProvider.SampleSelfAsync(cancellationToken).ConfigureAwait(false);
                _selfMonitoringSamples.Add(sample);
                if (_selfMonitoringSamples.Count > MaxSelfMonitoringSamples)
                {
                    _selfMonitoringSamples.RemoveRange(0, _selfMonitoringSamples.Count - MaxSelfMonitoringSamples);
                }

                var summary = await _selfMonitoringProvider.SummarizeAsync(_selfMonitoringSamples, cancellationToken).ConfigureAwait(false);
                if (_isUiVisible && SelectedTabIndex == SettingsTabIndex)
                {
                    await Application.Current.Dispatcher.InvokeAsync(
                        () => UpdateSelfMonitoringUi(sample, summary),
                        System.Windows.Threading.DispatcherPriority.Background);
                }

                await Task.Delay(SelfMonitoringIntervalMs, cancellationToken).ConfigureAwait(false);
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
        SelfWritesBudgetStatusText = UiText.Get("Ui_Configured");
        SelfSnapshotsBudgetStatusText = UiText.Get("Ui_EventOnly");
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
            LiveUiRefreshMs = (int)LiveUiRefreshInterval.TotalMilliseconds,
            StorageBatchSize = 10,
            CaptureCpu = CaptureCpu,
            CaptureRam = CaptureRam,
            CaptureDiskRead = CaptureDiskRead,
            CaptureDiskWrite = CaptureDiskWrite,
            CaptureGpu = CaptureGpu,
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

    private MetricCapabilities CreateLiveCapabilities(SamplingSettings sampling) => new()
    {
        CpuPercent = sampling.CaptureCpu ? MetricReliability.Stable : MetricReliability.Unavailable,
        MemoryMb = sampling.CaptureRam ? MetricReliability.Stable : MetricReliability.Unavailable,
        DiskReadMbPerSec = sampling.CaptureDiskRead ? MetricReliability.BestEffort : MetricReliability.Unavailable,
        DiskWriteMbPerSec = sampling.CaptureDiskWrite ? MetricReliability.BestEffort : MetricReliability.Unavailable,
        GpuPercent = sampling.CaptureGpu && HasSystemGpu ? MetricReliability.BestEffort : MetricReliability.Unavailable,
        GpuMemoryMb = sampling.CaptureGpu && HasSystemGpu ? MetricReliability.BestEffort : MetricReliability.Unavailable,
        TemperatureC = sampling.CaptureGpu && HasSystemGpu ? MetricReliability.BestEffort : MetricReliability.Unavailable
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
