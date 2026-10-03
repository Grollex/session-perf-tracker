using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Runner;

public sealed class ProcessSessionRunner : ITargetSessionRunner
{
    private const int ErrorElevationRequired = 740;
    private readonly IMetricCollector _metricCollector;
    private readonly ISessionStore _sessionStore;
    private readonly ISessionSummaryService _summaryService;
    private readonly IReadOnlyList<IEventDetector> _eventDetectors;
    private readonly ISpikeContextProvider? _spikeContextProvider;
    private readonly IEventNoiseFilter? _eventNoiseFilter;
    private readonly IThresholdSettingsProvider? _settingsProvider;

    public ProcessSessionRunner(
        IMetricCollector metricCollector,
        ISessionStore sessionStore,
        ISessionSummaryService summaryService,
        IEnumerable<IEventDetector>? eventDetectors = null,
        ISpikeContextProvider? spikeContextProvider = null,
        IEventNoiseFilter? eventNoiseFilter = null,
        IThresholdSettingsProvider? settingsProvider = null)
    {
        _metricCollector = metricCollector;
        _sessionStore = sessionStore;
        _summaryService = summaryService;
        _eventDetectors = eventDetectors?.ToArray() ?? [];
        _spikeContextProvider = spikeContextProvider;
        _eventNoiseFilter = eventNoiseFilter;
        _settingsProvider = settingsProvider;
    }

    public Task<IRunningSessionHandle> StartAsync(
        TargetDescriptor target,
        SamplingSettings sampling,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target.ExecutablePath))
        {
            throw new InvalidOperationException("Executable target requires a path.");
        }

        using var process = LaunchProcess(target.ExecutablePath);
        var trackedTarget = target with
        {
            ProcessId = process.Id,
            DisplayName = $"{target.DisplayName} ({process.Id})"
        };

        // Register while the launch handle is still open, so a process that exits instantly
        // keeps its pid and exit code readable for the tracker.
        var handle = CreateHandle(trackedTarget, sampling);
        return Task.FromResult<IRunningSessionHandle>(handle);
    }

    public Task<IRunningSessionHandle> AttachAsync(
        TargetDescriptor target,
        SamplingSettings sampling,
        CancellationToken cancellationToken = default)
    {
        if (target.ProcessId is null)
        {
            throw new InvalidOperationException("Attach target requires a process id.");
        }

        // Throws ArgumentException when the process is already gone, which the UI reports as a start failure.
        using (Process.GetProcessById(target.ProcessId.Value))
        {
        }

        return Task.FromResult<IRunningSessionHandle>(CreateHandle(target, sampling));
    }

    private static Process LaunchProcess(string executablePath)
    {
        var workingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory;
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("Failed to start target process.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == ErrorElevationRequired)
        {
            // The target's manifest requires administrator rights: let Windows show the UAC prompt.
            return Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Failed to start target process with elevation.");
        }
    }

    private RunningProcessSessionHandle CreateHandle(TargetDescriptor target, SamplingSettings sampling)
    {
        var handle = new RunningProcessSessionHandle(
            target,
            sampling,
            _metricCollector,
            _sessionStore,
            _summaryService,
            _eventDetectors,
            _spikeContextProvider,
            _eventNoiseFilter,
            _settingsProvider);
        _metricCollector.RegisterSession(handle.SessionId, target, sampling);
        handle.Start();
        return handle;
    }

    internal static ExitClassification ClassifyExit(int? exitCode, bool hungAtExit)
    {
        if (hungAtExit)
        {
            return new ExitClassification(
                SessionExitKind.CrashLikeExit,
                exitCode,
                "The target exited while it was not responding.");
        }

        if (exitCode is null)
        {
            return new ExitClassification(
                SessionExitKind.UnexpectedExit,
                null,
                "The target process disappeared while monitoring was active, and no exit code was available.");
        }

        var code = exitCode.Value;
        var status = unchecked((uint)code);
        if (code == 0 || status == 0xC000013A)
        {
            return new ExitClassification(
                SessionExitKind.ExternalClose,
                code,
                "The target process closed outside SessionPerfTracker normally.");
        }

        var crashName = DescribeCrashStatus(status);
        if (crashName is not null)
        {
            return new ExitClassification(
                SessionExitKind.CrashLikeExit,
                code,
                $"The target process terminated with crash status 0x{status:X8} ({crashName}).");
        }

        return new ExitClassification(
            SessionExitKind.UnexpectedExit,
            code,
            $"The target process exited with non-zero exit code {FormatExitCode(code)}.");
    }

    private static string? DescribeCrashStatus(uint status) => status switch
    {
        0xC0000005 => "access violation",
        0xC00000FD => "stack overflow",
        0xC0000409 => "fail-fast / stack buffer overrun",
        0xC0000374 => "heap corruption",
        0xC000001D => "illegal instruction",
        0xC0000094 => "integer divide by zero",
        0xC0000135 => "required DLL not found",
        0xC0000142 => "DLL initialization failed",
        0xC0000417 => "invalid C runtime parameter",
        0xC000041D => "unhandled exception in a callback",
        0xC06D007E => "delay-loaded module not found",
        0xE0434352 => "unhandled .NET exception",
        0xE06D7363 => "unhandled C++ exception",
        >= 0xC0000000 and <= 0xCFFFFFFF => "Windows error status",
        _ => null
    };

    private static string FormatExitCode(int code) =>
        code < 0 || code > 0xFFFF
            ? $"{code} (0x{unchecked((uint)code):X8})"
            : code.ToString(CultureInfo.InvariantCulture);

    internal sealed record ExitClassification(SessionExitKind Kind, int? ExitCode, string Reason);

    private sealed class RunningProcessSessionHandle : IRunningSessionHandle
    {
        private const long LongStartupThresholdMs = 15_000;
        private const int HangConsecutiveUnresponsiveSamples = 3;
        private const int MaxHangEvents = 10;
        private const int DetectorSampleWindow = 120;
        private const int MaxConsecutiveCollectionFailures = 5;
        private static readonly TimeSpan MaxTimeBetweenSaves = TimeSpan.FromSeconds(15);

        private readonly SamplingSettings _sampling;
        private readonly IMetricCollector _metricCollector;
        private readonly ISessionStore _sessionStore;
        private readonly ISessionSummaryService _summaryService;
        private readonly IReadOnlyList<IEventDetector> _eventDetectors;
        private readonly ISpikeContextProvider? _spikeContextProvider;
        private readonly IEventNoiseFilter? _eventNoiseFilter;
        private readonly IThresholdSettingsProvider? _settingsProvider;
        private readonly CancellationTokenSource _stopCts = new();
        private readonly TaskCompletionSource<SessionRecord> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<MetricSample> _samples = [];
        private readonly List<PerformanceEvent> _events = [];
        private readonly HashSet<string> _eventIds = new(StringComparer.Ordinal);
        private readonly List<string> _notes = [];
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private readonly object _sync = new();
        private Task _runTask = Task.CompletedTask;
        private volatile bool _stopRequested;
        private int _lastSavedSampleCount;
        private DateTimeOffset _lastSavedAt = DateTimeOffset.UtcNow;
        private DateTimeOffset? _suppressDetectorNoiseUntilUtc;
        private string? _noiseSuppressionReason;
        private MetricCapabilities _capabilities = new();
        private bool _startupReadyRecorded;
        private bool _longStartupEventRaised;
        private bool _rootExitNoted;
        private int _hangEventCount;
        private bool _hangActive;
        private int _consecutiveUnresponsiveSamples;
        private int _consecutiveRespondingSamples;

        public RunningProcessSessionHandle(
            TargetDescriptor target,
            SamplingSettings sampling,
            IMetricCollector metricCollector,
            ISessionStore sessionStore,
            ISessionSummaryService summaryService,
            IReadOnlyList<IEventDetector> eventDetectors,
            ISpikeContextProvider? spikeContextProvider,
            IEventNoiseFilter? eventNoiseFilter,
            IThresholdSettingsProvider? settingsProvider)
        {
            Target = target;
            _sampling = sampling;
            _metricCollector = metricCollector;
            _sessionStore = sessionStore;
            _summaryService = summaryService;
            _eventDetectors = eventDetectors;
            _spikeContextProvider = spikeContextProvider;
            _eventNoiseFilter = eventNoiseFilter;
            _settingsProvider = settingsProvider;
            SessionId = $"session_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        }

        public string SessionId { get; }
        public TargetDescriptor Target { get; }
        public Task<SessionRecord> Completion => _completion.Task;
        public event EventHandler<MetricSample>? SampleCollected;
        public event EventHandler<IReadOnlyList<PerformanceEvent>>? EventsDetected;
        public event EventHandler<SessionRecord>? Completed;

        public void SuppressDetectorNoise(TimeSpan duration, string reason)
        {
            if (duration <= TimeSpan.Zero)
            {
                return;
            }

            lock (_sync)
            {
                var suppressUntil = DateTimeOffset.UtcNow.Add(duration);
                if (_suppressDetectorNoiseUntilUtc is null || suppressUntil > _suppressDetectorNoiseUntilUtc)
                {
                    _suppressDetectorNoiseUntilUtc = suppressUntil;
                    _noiseSuppressionReason = reason;
                }
            }
        }

        public void Start()
        {
            // Sampling must never run on the UI thread's synchronization context.
            _runTask = Task.Run(RunAsync);
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            _stopRequested = true;
            await _stopCts.CancelAsync().ConfigureAwait(false);
            await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _stopRequested = true;
            await _stopCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
            }

            _stopCts.Dispose();
        }

        private async Task RunAsync()
        {
            var token = _stopCts.Token;
            var allProcessesExited = false;
            string? failureReason = null;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                _capabilities = ApplyCaptureScope(
                    await _metricCollector.GetCapabilitiesAsync(Target, token).ConfigureAwait(false));
                var consecutiveFailures = 0;
                var interval = Math.Max(500, _sampling.IntervalMs);

                while (!token.IsCancellationRequested)
                {
                    var tickStartedMs = stopwatch.ElapsedMilliseconds;
                    MetricSample sample;
                    try
                    {
                        sample = ApplyCaptureScope(await _metricCollector
                            .CollectAsync(Target, SessionId, tickStartedMs, token)
                            .ConfigureAwait(false));
                        consecutiveFailures = 0;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception error)
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures >= MaxConsecutiveCollectionFailures)
                        {
                            failureReason = $"Metric collection kept failing: {error.Message}";
                            break;
                        }

                        await Task.Delay(interval, token).ConfigureAwait(false);
                        continue;
                    }

                    var treeStatus = _metricCollector.GetTreeStatus(SessionId);
                    if (sample.ProcessCount == 0)
                    {
                        allProcessesExited = true;
                        break;
                    }

                    lock (_sync)
                    {
                        _samples.Add(sample);
                    }

                    NoteRootExitIfNeeded(treeStatus, sample);
                    await DetectEventsAsync(sample, token).ConfigureAwait(false);
                    await EvaluateStabilityAsync(sample, treeStatus, token).ConfigureAwait(false);
                    await SaveBufferedSnapshotIfNeededAsync(token).ConfigureAwait(false);
                    SafeInvoke(() => SampleCollected?.Invoke(this, sample));

                    var remaining = interval - (stopwatch.ElapsedMilliseconds - tickStartedMs);
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(50, remaining)), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                failureReason = $"Monitoring stopped after an internal error: {error.Message}";
            }

            await CompleteAsync(stopwatch.ElapsedMilliseconds, allProcessesExited, failureReason).ConfigureAwait(false);
        }

        private async Task CompleteAsync(long elapsedMs, bool allProcessesExited, string? failureReason)
        {
            try
            {
                SessionStatus status;
                if (allProcessesExited && !_stopRequested)
                {
                    var treeStatus = _metricCollector.GetTreeStatus(SessionId);
                    var classification = ClassifyExit(
                        treeStatus?.LastExitCode ?? treeStatus?.RootExitCode,
                        _hangActive);
                    await AddFinalStabilityEventsAsync(elapsedMs, classification).ConfigureAwait(false);
                    status = ToSessionStatus(classification.Kind);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(failureReason))
                    {
                        lock (_sync)
                        {
                            _notes.Add(failureReason);
                        }
                    }

                    status = SessionStatus.Stopped;
                }

                var session = await FinalizeSessionAsync(status).ConfigureAwait(false);
                _completion.TrySetResult(session);
                SafeInvoke(() => Completed?.Invoke(this, session));
            }
            catch (Exception error)
            {
                _completion.TrySetException(error);
            }
            finally
            {
                _metricCollector.ReleaseSession(SessionId);
            }
        }

        private void NoteRootExitIfNeeded(ProcessTreeStatus? status, MetricSample sample)
        {
            if (_rootExitNoted || status is null || status.RootAlive)
            {
                return;
            }

            _rootExitNoted = true;
            var code = status.RootExitCode is null ? "unknown" : FormatExitCode(status.RootExitCode.Value);
            lock (_sync)
            {
                _notes.Add(
                    $"Root process exited at +{TimeSpan.FromMilliseconds(sample.ElapsedMs):hh\\:mm\\:ss} (exit code {code}); "
                    + $"recording continued for {sample.ProcessCount:N0} child process(es).");
            }
        }

        private MetricCapabilities ApplyCaptureScope(MetricCapabilities capabilities) => capabilities with
        {
            CpuPercent = _sampling.CaptureCpu ? capabilities.CpuPercent : MetricReliability.Unavailable,
            MemoryMb = _sampling.CaptureRam ? capabilities.MemoryMb : MetricReliability.Unavailable,
            DiskReadMbPerSec = _sampling.CaptureDiskRead ? capabilities.DiskReadMbPerSec : MetricReliability.Unavailable,
            DiskWriteMbPerSec = _sampling.CaptureDiskWrite ? capabilities.DiskWriteMbPerSec : MetricReliability.Unavailable,
            GpuPercent = _sampling.CaptureGpu ? capabilities.GpuPercent : MetricReliability.Unavailable,
            GpuMemoryMb = _sampling.CaptureGpu ? capabilities.GpuMemoryMb : MetricReliability.Unavailable,
            TemperatureC = _sampling.CaptureGpu ? capabilities.TemperatureC : MetricReliability.Unavailable
        };

        private MetricSample ApplyCaptureScope(MetricSample sample)
        {
            if (sample.Values.Keys.All(IsMetricEnabled))
            {
                return sample;
            }

            return sample with
            {
                Values = sample.Values
                    .Where(item => IsMetricEnabled(item.Key))
                    .ToDictionary(item => item.Key, item => item.Value),
                SourceReliability = sample.SourceReliability
                    .Where(item => IsMetricEnabled(item.Key))
                    .ToDictionary(item => item.Key, item => item.Value)
            };
        }

        private bool IsMetricEnabled(MetricKey key) => key switch
        {
            MetricKey.CpuPercent => _sampling.CaptureCpu,
            MetricKey.MemoryMb => _sampling.CaptureRam,
            MetricKey.DiskReadMbPerSec => _sampling.CaptureDiskRead,
            MetricKey.DiskWriteMbPerSec => _sampling.CaptureDiskWrite,
            MetricKey.GpuPercent or MetricKey.GpuMemoryMb or MetricKey.TemperatureC => _sampling.CaptureGpu,
            _ => true
        };

        private async Task EvaluateStabilityAsync(
            MetricSample sample,
            ProcessTreeStatus? status,
            CancellationToken cancellationToken)
        {
            var events = new List<PerformanceEvent>();
            EvaluateStartup(sample, status, events);
            EvaluateHang(sample, status, events);

            if (events.Count > 0)
            {
                await AddAcceptedEventsAsync(events, cancellationToken).ConfigureAwait(false);
            }
        }

        private void EvaluateStartup(MetricSample sample, ProcessTreeStatus? status, List<PerformanceEvent> events)
        {
            if (_startupReadyRecorded)
            {
                return;
            }

            var hasWindow = status?.HasWindow == true;
            var isReady = hasWindow
                ? status!.IsResponding
                : sample.ElapsedMs >= Math.Max(500, _sampling.IntervalMs);

            if (!isReady)
            {
                if (!_longStartupEventRaised && sample.ElapsedMs >= LongStartupThresholdMs)
                {
                    _longStartupEventRaised = true;
                    events.Add(CreateLongStartupEvent(
                        sample.ElapsedMs,
                        "The target had not reached a responding window state within the expected startup window."));
                }

                return;
            }

            _startupReadyRecorded = true;
            if (!_longStartupEventRaised && sample.ElapsedMs >= LongStartupThresholdMs)
            {
                _longStartupEventRaised = true;
                events.Add(CreateLongStartupEvent(
                    sample.ElapsedMs,
                    hasWindow
                        ? "The target reached a responding window state later than expected."
                        : "No window ready-state was available; startup duration is based on first recorded sample."));
            }
        }

        private void EvaluateHang(MetricSample sample, ProcessTreeStatus? status, List<PerformanceEvent> events)
        {
            if (!_startupReadyRecorded || status is null || !status.HasWindow)
            {
                _consecutiveUnresponsiveSamples = 0;
                return;
            }

            if (status.IsResponding)
            {
                _consecutiveUnresponsiveSamples = 0;
                if (_hangActive && ++_consecutiveRespondingSamples >= HangConsecutiveUnresponsiveSamples)
                {
                    _hangActive = false;
                }

                return;
            }

            _consecutiveRespondingSamples = 0;
            _consecutiveUnresponsiveSamples++;
            if (_hangActive
                || _hangEventCount >= MaxHangEvents
                || _consecutiveUnresponsiveSamples < HangConsecutiveUnresponsiveSamples)
            {
                return;
            }

            _hangActive = true;
            _hangEventCount++;
            // IsHungAppWindow reports a hang after ~5 s without message processing.
            var observedMs = 5000 + HangConsecutiveUnresponsiveSamples * Math.Max(500, _sampling.IntervalMs);
            var windowOwner = string.IsNullOrWhiteSpace(status.WindowProcessName) ? "The target window" : status.WindowProcessName;
            events.Add(new PerformanceEvent
            {
                Id = $"{SessionId}_stability_hang_{sample.ElapsedMs}",
                SessionId = SessionId,
                Kind = EventKind.HangSuspected,
                Timestamp = sample.Timestamp,
                ElapsedMs = sample.ElapsedMs,
                Severity = EventSeverity.Warning,
                Title = "Not responding",
                Details = $"{windowOwner} stopped processing window messages for about {observedMs / 1000d:N0}s (Windows hung-window check).",
                ObservedValue = observedMs,
                ThresholdValue = observedMs,
                DetectionProvider = "stability-layer-best-effort"
            });
        }

        private async Task AddFinalStabilityEventsAsync(long elapsedMs, ExitClassification classification)
        {
            var events = new List<PerformanceEvent>();
            if (!_startupReadyRecorded && elapsedMs >= LongStartupThresholdMs && !_longStartupEventRaised)
            {
                _longStartupEventRaised = true;
                events.Add(CreateLongStartupEvent(
                    elapsedMs,
                    "The target exited before a responding ready-state was observed."));
            }

            events.Add(CreateExitEvent(elapsedMs, classification));
            await AddAcceptedEventsAsync(events, CancellationToken.None).ConfigureAwait(false);
        }

        private PerformanceEvent CreateExitEvent(long elapsedMs, ExitClassification classification)
        {
            var (kind, severity, title) = classification.Kind switch
            {
                SessionExitKind.ExternalClose => (EventKind.ExternalExit, EventSeverity.Info, "External close / graceful exit"),
                SessionExitKind.CrashLikeExit => (EventKind.CrashLikeExit, EventSeverity.Critical, "Crash-like exit"),
                _ => (EventKind.UnexpectedExit, EventSeverity.Warning, "Unexpected exit")
            };

            var exitCodeText = classification.ExitCode is null
                ? "Exit code: unavailable."
                : $"Exit code: {FormatExitCode(classification.ExitCode.Value)}.";

            return new PerformanceEvent
            {
                Id = $"{SessionId}_stability_exit_{kind}",
                SessionId = SessionId,
                Kind = kind,
                Timestamp = DateTimeOffset.UtcNow,
                ElapsedMs = elapsedMs,
                Severity = severity,
                Title = title,
                Details = $"{classification.Reason} {exitCodeText} This classification is best-effort.",
                ObservedValue = classification.ExitCode is null ? null : Convert.ToDouble(classification.ExitCode.Value),
                DetectionProvider = "stability-layer"
            };
        }

        private PerformanceEvent CreateLongStartupEvent(long elapsedMs, string details) => new()
        {
            Id = $"{SessionId}_stability_long_startup",
            SessionId = SessionId,
            Kind = EventKind.LongStartup,
            Timestamp = _startedAt.AddMilliseconds(elapsedMs),
            ElapsedMs = elapsedMs,
            Severity = EventSeverity.Warning,
            Title = "Long startup",
            Details = $"{details} Startup detection is best-effort.",
            ObservedValue = elapsedMs,
            ThresholdValue = LongStartupThresholdMs,
            DetectionProvider = "stability-layer-best-effort"
        };

        private static SessionStatus ToSessionStatus(SessionExitKind kind) => kind switch
        {
            SessionExitKind.ExternalClose => SessionStatus.ExternalExit,
            SessionExitKind.CrashLikeExit => SessionStatus.CrashLikeExit,
            _ => SessionStatus.UnexpectedExit
        };

        private async Task AddAcceptedEventsAsync(
            IReadOnlyList<PerformanceEvent> events,
            CancellationToken cancellationToken)
        {
            PerformanceEvent[] newEvents;
            lock (_sync)
            {
                newEvents = events.Where(item => _eventIds.Add(item.Id)).ToArray();
                _events.AddRange(newEvents);
            }

            if (newEvents.Length > 0)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                SafeInvoke(() => EventsDetected?.Invoke(this, newEvents));
            }
        }

        private async Task DetectEventsAsync(MetricSample sample, CancellationToken cancellationToken)
        {
            if (_eventDetectors.Count == 0)
            {
                return;
            }

            MetricSample[] samples;
            PerformanceEvent[] previousEvents;
            DateTimeOffset? suppressDetectorNoiseUntilUtc;
            string? noiseSuppressionReason;
            lock (_sync)
            {
                // Detectors only look at recent history; never copy the whole session every tick.
                var start = Math.Max(0, _samples.Count - DetectorSampleWindow);
                samples = _samples.GetRange(start, _samples.Count - start).ToArray();
                previousEvents = _events.ToArray();
                suppressDetectorNoiseUntilUtc = _suppressDetectorNoiseUntilUtc;
                noiseSuppressionReason = _noiseSuppressionReason;
            }

            var input = new EventDetectionInput(SessionId, Target, samples, previousEvents, _sampling);
            var batches = await Task.WhenAll(_eventDetectors.Select(detector => detector.DetectAsync(input, cancellationToken))).ConfigureAwait(false);
            var detectedEvents = batches.SelectMany(batch => batch).ToArray();
            if (detectedEvents.Length == 0)
            {
                return;
            }

            if (_eventNoiseFilter is not null)
            {
                detectedEvents = _eventNoiseFilter.Filter(new EventNoiseFilterInput(
                    SessionId,
                    Target,
                    samples,
                    detectedEvents,
                    previousEvents,
                    _sampling,
                    suppressDetectorNoiseUntilUtc,
                    noiseSuppressionReason)).ToArray();
            }

            PerformanceEvent[] newEvents;
            lock (_sync)
            {
                newEvents = detectedEvents.Where(item => !_eventIds.Contains(item.Id)).ToArray();
            }

            if (newEvents.Length == 0)
            {
                return;
            }

            newEvents = await AttachContextIfNeededAsync(newEvents, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                newEvents = newEvents.Where(item => _eventIds.Add(item.Id)).ToArray();
                _events.AddRange(newEvents);
            }

            if (newEvents.Length > 0)
            {
                SafeInvoke(() => EventsDetected?.Invoke(this, newEvents));
            }
        }

        private async Task<PerformanceEvent[]> AttachContextIfNeededAsync(
            PerformanceEvent[] events,
            CancellationToken cancellationToken)
        {
            if (_spikeContextProvider is null || events.Length == 0)
            {
                return events;
            }

            var triggerEvent = events.FirstOrDefault(ShouldCaptureContext);
            if (triggerEvent is null)
            {
                return events;
            }

            try
            {
                var context = await _spikeContextProvider.CaptureAsync(
                    new SpikeContextInput(SessionId, Target, triggerEvent, LookbackMs: 5000, LookaheadMs: 0),
                    cancellationToken).ConfigureAwait(false);

                if (context is null)
                {
                    return events;
                }

                SuppressDetectorNoise(
                    TimeSpan.FromSeconds(_settingsProvider?.Current.AntiNoise.SnapshotSuppressionSeconds ?? 3),
                    "event context snapshot");

                return events
                    .Select(performanceEvent => ShouldCaptureContext(performanceEvent)
                        ? performanceEvent with
                        {
                            Context = context with
                            {
                                TriggerEventId = performanceEvent.Id,
                                TriggerEventKind = performanceEvent.Kind,
                                TriggerMetricKey = performanceEvent.MetricKey,
                                TriggeredAt = performanceEvent.Timestamp
                            }
                        }
                        : performanceEvent)
                    .ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return events;
            }
        }

        private static bool ShouldCaptureContext(PerformanceEvent performanceEvent) =>
            performanceEvent.Kind is EventKind.ThresholdBreach or EventKind.Spike;

        private async Task SaveBufferedSnapshotIfNeededAsync(CancellationToken cancellationToken)
        {
            int sampleCount;
            lock (_sync)
            {
                sampleCount = _samples.Count;
            }

            var batchSize = Math.Max(1, _sampling.StorageBatchSize);
            if (sampleCount - _lastSavedSampleCount < batchSize
                && DateTimeOffset.UtcNow - _lastSavedAt < MaxTimeBetweenSaves)
            {
                return;
            }

            try
            {
                var session = BuildSession(SessionStatus.Running, endedAt: null);
                await _sessionStore.SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);
                _lastSavedSampleCount = sampleCount;
                _lastSavedAt = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A transient storage error must not stop recording; the final save retries everything.
                _lastSavedAt = DateTimeOffset.UtcNow;
            }
        }

        private async Task<SessionRecord> FinalizeSessionAsync(SessionStatus status)
        {
            var session = BuildSession(status, DateTimeOffset.UtcNow);
            Exception? lastError = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await _sessionStore.SaveSessionAsync(session).ConfigureAwait(false);
                    return session;
                }
                catch (Exception error)
                {
                    lastError = error;
                    await Task.Delay(250 * (attempt + 1)).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException($"Session could not be saved: {lastError?.Message}", lastError);
        }

        private SessionRecord BuildSession(SessionStatus status, DateTimeOffset? endedAt)
        {
            MetricSample[] samples;
            PerformanceEvent[] events;
            string? notes;

            lock (_sync)
            {
                samples = _samples.ToArray();
                events = _events.ToArray();
                notes = _notes.Count == 0 ? null : string.Join(" ", _notes);
            }

            var withoutSummary = new SessionRecordWithoutSummary
            {
                Id = SessionId,
                Target = Target,
                Status = status,
                StartedAt = _startedAt,
                EndedAt = endedAt,
                Sampling = _sampling,
                Capabilities = _capabilities,
                Samples = samples,
                Events = events,
                Notes = notes
            };
            return withoutSummary.WithSummary(_summaryService.Summarize(withoutSummary));
        }

        private static void SafeInvoke(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                // UI subscribers must never break the recording loop.
            }
        }
    }
}
