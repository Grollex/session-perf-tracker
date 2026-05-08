using System.Diagnostics;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Runner;

public sealed class ProcessSessionRunner : ITargetSessionRunner
{
    private const long LongStartupThresholdMs = 15_000;
    private const int HangConsecutiveUnresponsiveSamples = 3;

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

        var startInfo = new ProcessStartInfo
        {
            FileName = target.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(target.ExecutablePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start target process.");

        var trackedTarget = target with
        {
            ProcessId = process.Id,
            DisplayName = $"{target.DisplayName} ({process.Id})"
        };

        return Task.FromResult<IRunningSessionHandle>(CreateHandle(process, trackedTarget, sampling));
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

        var process = Process.GetProcessById(target.ProcessId.Value);
        return Task.FromResult<IRunningSessionHandle>(CreateHandle(process, target, sampling));
    }

    private RunningProcessSessionHandle CreateHandle(Process process, TargetDescriptor target, SamplingSettings sampling)
    {
        var handle = new RunningProcessSessionHandle(
            process,
            target,
            sampling,
            _metricCollector,
            _sessionStore,
            _summaryService,
            _eventDetectors,
            _spikeContextProvider,
            _eventNoiseFilter,
            _settingsProvider);
        handle.Start();
        return handle;
    }

    private sealed class RunningProcessSessionHandle : IRunningSessionHandle
    {
        private readonly Process _process;
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
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private readonly object _sync = new();
        private bool _stopRequested;
        private int _lastBufferedSampleCount;
        private DateTimeOffset? _suppressDetectorNoiseUntilUtc;
        private string? _noiseSuppressionReason;
        private MetricCapabilities _capabilities = new();
        private bool _startupReadyRecorded;
        private bool _longStartupEventRaised;
        private bool _hangEventRaised;
        private int _consecutiveUnresponsiveSamples;

        public RunningProcessSessionHandle(
            Process process,
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
            _process = process;
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
            _ = RunAsync();
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            _stopRequested = true;
            await _stopCts.CancelAsync();
            await Completion.WaitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _stopCts.CancelAsync();
            _process.Dispose();
            _stopCts.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                _capabilities = ApplyCaptureScope(await _metricCollector.GetCapabilitiesAsync(Target, _stopCts.Token));
                var stopwatch = Stopwatch.StartNew();

                while (!_stopCts.IsCancellationRequested && !HasExited())
                {
                    try
                    {
                        var sample = ApplyCaptureScope(await _metricCollector.CollectAsync(Target, SessionId, stopwatch.ElapsedMilliseconds, _stopCts.Token));
                        lock (_sync)
                        {
                            _samples.Add(sample);
                        }

                        await DetectEventsAsync(sample, _stopCts.Token);
                        await EvaluateStabilityAsync(sample, _stopCts.Token);
                        await SaveBufferedSnapshotIfNeededAsync(_stopCts.Token);
                        SampleCollected?.Invoke(this, sample);
                    }
                    catch (InvalidOperationException) when (HasExited())
                    {
                        break;
                    }

                    await WaitForNextSampleOrExitAsync(_stopCts.Token);
                }

                stopwatch.Stop();
                var externalExit = !_stopRequested && HasExited();
                var externalExitClassification = externalExit
                    ? ClassifyExternalExit()
                    : ExternalExitClassification.Completed;
                if (externalExit)
                {
                    await AddFinalStabilityEventsAsync(stopwatch.ElapsedMilliseconds, externalExitClassification);
                }

                var session = await FinalizeSessionAsync(externalExit
                    ? ToSessionStatus(externalExitClassification.Kind)
                    : _stopRequested ? SessionStatus.Stopped : SessionStatus.Completed);
                _completion.TrySetResult(session);
                Completed?.Invoke(this, session);
            }
            catch (OperationCanceledException)
            {
                var session = await FinalizeSessionAsync(SessionStatus.Stopped);
                _completion.TrySetResult(session);
                Completed?.Invoke(this, session);
            }
            catch (Exception error)
            {
                _completion.TrySetException(error);
            }
            finally
            {
                _process.Dispose();
            }
        }

        private async Task WaitForNextSampleOrExitAsync(CancellationToken cancellationToken)
        {
            var interval = Math.Max(500, _sampling.IntervalMs);
            await Task.Delay(interval, cancellationToken);
        }

        private MetricCapabilities ApplyCaptureScope(MetricCapabilities capabilities) => capabilities with
        {
            CpuPercent = _sampling.CaptureCpu ? capabilities.CpuPercent : MetricReliability.Unavailable,
            MemoryMb = _sampling.CaptureRam ? capabilities.MemoryMb : MetricReliability.Unavailable,
            DiskReadMbPerSec = _sampling.CaptureDiskRead ? capabilities.DiskReadMbPerSec : MetricReliability.Unavailable,
            DiskWriteMbPerSec = _sampling.CaptureDiskWrite ? capabilities.DiskWriteMbPerSec : MetricReliability.Unavailable
        };

        private MetricSample ApplyCaptureScope(MetricSample sample)
        {
            var values = sample.Values
                .Where(item => IsMetricEnabled(item.Key))
                .ToDictionary(item => item.Key, item => item.Value);
            var reliability = sample.SourceReliability
                .Where(item => IsMetricEnabled(item.Key))
                .ToDictionary(item => item.Key, item => item.Value);

            return sample with
            {
                Values = values,
                SourceReliability = reliability
            };
        }

        private bool IsMetricEnabled(MetricKey key) => key switch
        {
            MetricKey.CpuPercent => _sampling.CaptureCpu,
            MetricKey.MemoryMb => _sampling.CaptureRam,
            MetricKey.DiskReadMbPerSec => _sampling.CaptureDiskRead,
            MetricKey.DiskWriteMbPerSec => _sampling.CaptureDiskWrite,
            _ => true
        };

        private async Task EvaluateStabilityAsync(MetricSample sample, CancellationToken cancellationToken)
        {
            var events = new List<PerformanceEvent>();
            EvaluateStartup(sample, events);
            EvaluateHang(sample, events);

            if (events.Count > 0)
            {
                await AddAcceptedEventsAsync(events, cancellationToken);
            }
        }

        private void EvaluateStartup(MetricSample sample, List<PerformanceEvent> events)
        {
            if (_startupReadyRecorded)
            {
                return;
            }

            var windowState = TryReadWindowState();
            var isReady = windowState.HasWindow
                ? windowState.IsResponding
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
                    windowState.HasWindow
                        ? "The target reached a responding window state later than expected."
                        : "No window ready-state was available; startup duration is based on first recorded sample."));
            }
        }

        private void EvaluateHang(MetricSample sample, List<PerformanceEvent> events)
        {
            if (_hangEventRaised || !_startupReadyRecorded)
            {
                return;
            }

            var windowState = TryReadWindowState();
            if (!windowState.HasWindow)
            {
                _consecutiveUnresponsiveSamples = 0;
                return;
            }

            if (windowState.IsResponding)
            {
                _consecutiveUnresponsiveSamples = 0;
                return;
            }

            _consecutiveUnresponsiveSamples++;
            if (_consecutiveUnresponsiveSamples < HangConsecutiveUnresponsiveSamples)
            {
                return;
            }

            _hangEventRaised = true;
            var observedMs = HangConsecutiveUnresponsiveSamples * Math.Max(500, _sampling.IntervalMs);
            events.Add(new PerformanceEvent
            {
                Id = $"{SessionId}_stability_hang_{sample.ElapsedMs}",
                SessionId = SessionId,
                Kind = EventKind.HangSuspected,
                Timestamp = sample.Timestamp,
                ElapsedMs = sample.ElapsedMs,
                Severity = EventSeverity.Warning,
                Title = "Best-effort not responding",
                Details = $"The root window reported not responding for about {observedMs / 1000d:N1}s. This is a lightweight Windows Process.Responding check, not a reliable hang proof.",
                ObservedValue = observedMs,
                ThresholdValue = observedMs,
                DetectionProvider = "stability-layer-best-effort"
            });
        }

        private async Task AddFinalStabilityEventsAsync(
            long elapsedMs,
            ExternalExitClassification classification)
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

            await AddAcceptedEventsAsync(events, CancellationToken.None);
        }

        private ExternalExitClassification ClassifyExternalExit()
        {
            var exitCode = TryReadExitCode();
            if (_hangEventRaised)
            {
                return new ExternalExitClassification(
                    SessionExitKind.CrashLikeExit,
                    exitCode,
                    "The target exited after a best-effort not responding event.");
            }

            if (exitCode is 0)
            {
                return new ExternalExitClassification(
                    SessionExitKind.ExternalClose,
                    exitCode,
                    "The target process closed outside SessionPerfTracker with exit code 0.");
            }

            if (exitCode is not null)
            {
                return new ExternalExitClassification(
                    SessionExitKind.CrashLikeExit,
                    exitCode,
                    $"The target process exited with non-zero exit code {exitCode.Value}.");
            }

            return new ExternalExitClassification(
                SessionExitKind.UnexpectedExit,
                exitCode,
                "The target process disappeared while monitoring was active, and no exit code was available.");
        }

        private PerformanceEvent CreateExitEvent(long elapsedMs, ExternalExitClassification classification)
        {
            var (kind, severity, title) = classification.Kind switch
            {
                SessionExitKind.ExternalClose => (EventKind.ExternalExit, EventSeverity.Info, "External close / graceful exit"),
                SessionExitKind.CrashLikeExit => (EventKind.CrashLikeExit, EventSeverity.Critical, "Crash-like exit"),
                SessionExitKind.UnexpectedExit => (EventKind.UnexpectedExit, EventSeverity.Warning, "Unexpected exit"),
                _ => (EventKind.UnexpectedExit, EventSeverity.Warning, "Unexpected exit")
            };

            var exitCodeText = classification.ExitCode is null
                ? "Exit code: unavailable."
                : $"Exit code: {classification.ExitCode.Value}.";

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

        private (bool HasWindow, bool IsResponding) TryReadWindowState()
        {
            try
            {
                if (_process.HasExited)
                {
                    return (false, false);
                }

                _process.Refresh();
                if (_process.MainWindowHandle == IntPtr.Zero)
                {
                    return (false, true);
                }

                return (true, _process.Responding);
            }
            catch
            {
                return (false, true);
            }
        }

        private int? TryReadExitCode()
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch
            {
                return null;
            }
        }

        private static SessionStatus ToSessionStatus(SessionExitKind kind) => kind switch
        {
            SessionExitKind.ExternalClose => SessionStatus.ExternalExit,
            SessionExitKind.CrashLikeExit => SessionStatus.CrashLikeExit,
            SessionExitKind.UnexpectedExit => SessionStatus.UnexpectedExit,
            _ => SessionStatus.UnexpectedExit
        };

        private sealed record ExternalExitClassification(
            SessionExitKind Kind,
            int? ExitCode,
            string Reason)
        {
            public static ExternalExitClassification Completed { get; } = new(
                SessionExitKind.Completed,
                null,
                "Target completed normally.");
        }

        private async Task AddAcceptedEventsAsync(
            IReadOnlyList<PerformanceEvent> events,
            CancellationToken cancellationToken)
        {
            if (events.Count == 0)
            {
                return;
            }

            PerformanceEvent[] newEvents;
            lock (_sync)
            {
                var existingIds = _events.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                newEvents = events.Where(item => existingIds.Add(item.Id)).ToArray();
                _events.AddRange(newEvents);
            }

            if (newEvents.Length > 0)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                EventsDetected?.Invoke(this, newEvents);
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
                samples = _samples.ToArray();
                previousEvents = _events.ToArray();
                suppressDetectorNoiseUntilUtc = _suppressDetectorNoiseUntilUtc;
                noiseSuppressionReason = _noiseSuppressionReason;
            }

            var input = new EventDetectionInput(SessionId, Target, samples, previousEvents, _sampling);
            var batches = await Task.WhenAll(_eventDetectors.Select(detector => detector.DetectAsync(input, cancellationToken)));
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

            if (detectedEvents.Length == 0)
            {
                return;
            }

            PerformanceEvent[] newEvents;
            lock (_sync)
            {
                var existingIds = _events.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                newEvents = detectedEvents.Where(item => existingIds.Add(item.Id)).ToArray();
            }

            newEvents = await AttachContextIfNeededAsync(newEvents, cancellationToken);

            lock (_sync)
            {
                _events.AddRange(newEvents);
            }

            if (newEvents.Length > 0)
            {
                EventsDetected?.Invoke(this, newEvents);
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
                    cancellationToken);

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
            var batchSize = Math.Max(1, _sampling.StorageBatchSize);
            var sampleCount = _samples.Count;
            if (sampleCount - _lastBufferedSampleCount < batchSize)
            {
                return;
            }

            var session = BuildSession(SessionStatus.Running, endedAt: null);
            await _sessionStore.SaveSessionAsync(session, cancellationToken);
            _lastBufferedSampleCount = sampleCount;
        }

        private async Task<SessionRecord> FinalizeSessionAsync(SessionStatus status)
        {
            var endedAt = DateTimeOffset.UtcNow;
            var session = BuildSession(status, endedAt);
            await _sessionStore.SaveSessionAsync(session);
            return session;
        }

        private SessionRecord BuildSession(SessionStatus status, DateTimeOffset? endedAt)
        {
            MetricSample[] samples;
            PerformanceEvent[] events;

            lock (_sync)
            {
                samples = _samples.ToArray();
                events = _events.ToArray();
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
                Notes = "Recorded by WPF CPU/RAM/Disk target analysis with lightweight event context."
            };
            return withoutSummary.WithSummary(_summaryService.Summarize(withoutSummary));
        }

        private bool HasExited()
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }
}
