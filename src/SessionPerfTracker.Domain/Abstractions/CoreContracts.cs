using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Domain.Abstractions;

public interface IProcessTargetResolver
{
    Task<IReadOnlyList<TargetDescriptor>> ListRunningTargetsAsync(CancellationToken cancellationToken = default);
    Task<TargetDescriptor> ResolveExecutableAsync(string path, bool includeChildProcesses, CancellationToken cancellationToken = default);
    Task<TargetDescriptor> ResolveProcessAsync(int processId, bool includeChildProcesses, CancellationToken cancellationToken = default);
}

public interface ITargetSessionRunner
{
    Task<IRunningSessionHandle> StartAsync(TargetDescriptor target, SamplingSettings sampling, CancellationToken cancellationToken = default);
    Task<IRunningSessionHandle> AttachAsync(TargetDescriptor target, SamplingSettings sampling, CancellationToken cancellationToken = default);
}

public interface IRunningSessionHandle : IAsyncDisposable
{
    string SessionId { get; }
    TargetDescriptor Target { get; }
    Task<SessionRecord> Completion { get; }
    event EventHandler<MetricSample>? SampleCollected;
    event EventHandler<IReadOnlyList<PerformanceEvent>>? EventsDetected;
    event EventHandler<SessionRecord>? Completed;
    void SuppressDetectorNoise(TimeSpan duration, string reason);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IMetricCollector
{
    string Id { get; }
    string Label { get; }
    Task<MetricCapabilities> GetCapabilitiesAsync(TargetDescriptor target, CancellationToken cancellationToken = default);
    Task<MetricSample> CollectAsync(TargetDescriptor target, string sessionId, long elapsedMs, CancellationToken cancellationToken = default);

    /// <summary>Prepares per-session tracking (e.g. opens the root handle right after launch).</summary>
    void RegisterSession(string sessionId, TargetDescriptor target, SamplingSettings sampling)
    {
    }

    /// <summary>Process-tree facts from the latest sample: exits, exit codes and window responsiveness.</summary>
    ProcessTreeStatus? GetTreeStatus(string sessionId) => null;

    /// <summary>Releases handles and cached state when a session ends.</summary>
    void ReleaseSession(string sessionId)
    {
    }
}

public sealed record ProcessTreeStatus
{
    public bool RootAlive { get; init; } = true;
    public int AliveCount { get; init; }
    public int? RootExitCode { get; init; }
    public DateTimeOffset? RootExitedAt { get; init; }
    public int? LastExitCode { get; init; }
    public string? LastExitedProcessName { get; init; }
    public bool HasWindow { get; init; }
    public bool IsResponding { get; init; } = true;
    public string? WindowProcessName { get; init; }
}

public sealed record EventDetectionInput(
    string SessionId,
    TargetDescriptor Target,
    IReadOnlyList<MetricSample> Samples,
    IReadOnlyList<PerformanceEvent> PreviousEvents,
    SamplingSettings Sampling);

public sealed record EventNoiseFilterInput(
    string SessionId,
    TargetDescriptor Target,
    IReadOnlyList<MetricSample> Samples,
    IReadOnlyList<PerformanceEvent> RawEvents,
    IReadOnlyList<PerformanceEvent> PreviousAcceptedEvents,
    SamplingSettings Sampling,
    DateTimeOffset? SuppressDiskEventsUntilUtc = null,
    string? SuppressionReason = null);

public interface IEventDetector
{
    string Id { get; }
    IReadOnlySet<EventKind> HandledKinds { get; }
    Task<IReadOnlyList<PerformanceEvent>> DetectAsync(EventDetectionInput input, CancellationToken cancellationToken = default);
}

public interface IEventNoiseFilter
{
    IReadOnlyList<PerformanceEvent> Filter(EventNoiseFilterInput input);
}

public interface IHangDetector : IEventDetector
{
    bool RequiresWindowHandle { get; }
    string Reliability { get; }
}

public sealed record SpikeContextInput(
    string SessionId,
    TargetDescriptor Target,
    PerformanceEvent Event,
    int LookbackMs,
    int LookaheadMs);

public interface ISpikeContextProvider
{
    Task<SpikeContextSnapshot?> CaptureAsync(SpikeContextInput input, CancellationToken cancellationToken = default);
}

public interface ISessionStore
{
    Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists sessions with their stored summaries but without samples/events, so the list view stays
    /// cheap no matter how long the recordings are. Use <see cref="GetSessionAsync"/> for full data.
    /// </summary>
    Task<IReadOnlyList<SessionRecord>> ListSessionHeadersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalizes sessions left in the Running state by a crash or forced shutdown.
    /// </summary>
    Task<int> RecoverInterruptedSessionsAsync(CancellationToken cancellationToken = default);
    Task<SessionRecord?> GetSessionAsync(string id, CancellationToken cancellationToken = default);
    Task SaveSessionAsync(SessionRecord session, CancellationToken cancellationToken = default);
    Task DeleteSessionAsync(string id, CancellationToken cancellationToken = default);
    Task<int> DeleteSessionsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default);
    Task<int> DeleteAllSessionsAsync(CancellationToken cancellationToken = default);
    Task<int> DeleteSessionsOlderThanAsync(TimeSpan age, CancellationToken cancellationToken = default);
}

public interface ISessionSummaryService
{
    SessionSummary Summarize(SessionRecordWithoutSummary session);
}

public interface IComparisonEngine
{
    SessionComparisonResult Compare(SessionRecord left, SessionRecord right);
}

public interface IExportService
{
    string ExportDirectory { get; }
    void SetExportDirectory(string exportDirectory);
    Task<IReadOnlyList<ExportFileDescriptor>> ListExportsAsync(CancellationToken cancellationToken = default);
    Task OpenExportAsync(string path, CancellationToken cancellationToken = default);
    Task OpenExportDirectoryAsync(CancellationToken cancellationToken = default);
    Task<string> ExportSessionAsync(SessionRecord session, string format, CancellationToken cancellationToken = default);
    Task<string> ExportComparisonAsync(
        SessionRecord left,
        SessionRecord right,
        SessionComparisonResult comparison,
        string format,
        CancellationToken cancellationToken = default);
    Task<string> ExportProcessActionAuditAsync(
        IReadOnlyList<ProcessBanEvent> events,
        string format,
        CancellationToken cancellationToken = default);
}

public interface IUpdateService
{
    string CurrentVersion { get; }
    Task<UpdateCheckResult> CheckAsync(string manifestUrl, CancellationToken cancellationToken = default);
    Task<string> DownloadInstallerAsync(
        UpdateManifest manifest,
        string updateDirectory,
        CancellationToken cancellationToken = default);
    Task LaunchInstallerAsync(string installerPath, CancellationToken cancellationToken = default);
}

public sealed record ExportFileDescriptor(
    string Path,
    string FileName,
    DateTimeOffset LastWriteTime,
    long SizeBytes);

public interface IThresholdSettingsProvider
{
    CpuRamThresholdSettings Current { get; }
    ThresholdLimitValues ResolveThresholds(TargetDescriptor target);
}

public interface IThresholdSettingsStore : IThresholdSettingsProvider
{
    Task<CpuRamThresholdSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CpuRamThresholdSettings settings, CancellationToken cancellationToken = default);
    Task<CpuRamThresholdSettings> UpdateAsync(
        Func<CpuRamThresholdSettings, CpuRamThresholdSettings> update,
        CancellationToken cancellationToken = default);
}

public sealed record SelfMonitoringSample(
    DateTimeOffset Timestamp,
    double? CpuPercent = null,
    double? MemoryMb = null,
    double? DiskWriteMbPerSec = null);

public interface ISelfMonitoringProvider
{
    Task<SelfMonitoringSample> SampleSelfAsync(CancellationToken cancellationToken = default);
    Task<SelfOverheadSummary> SummarizeAsync(IReadOnlyList<SelfMonitoringSample> samples, CancellationToken cancellationToken = default);
}

public interface IGlobalProcessScanner
{
    Task<GlobalProcessScan> ScanAsync(CancellationToken cancellationToken = default);
}

public interface IProcessControlService
{
    Task<ProcessControlResult> KillProcessAsync(
        int processId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<ProcessControlResult> KillProcessTreeAsync(
        int processId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<ProcessControlResult> KillProcessesAsync(
        IReadOnlyList<int> processIds,
        string reason,
        CancellationToken cancellationToken = default);
}

public sealed record ProcessControlResult(
    int RequestedCount,
    int TerminatedCount,
    IReadOnlyList<string> Messages);

public sealed record GlobalProcessScan(
    DateTimeOffset CapturedAt,
    TimeSpan? ComparedToPreviousScan,
    IReadOnlyList<GlobalProcessSnapshot> Processes)
{
    public SystemLoadSnapshot? System { get; init; }
}

/// <summary>Whole-machine load shown above the Global Watch table and next to Live sessions.</summary>
public sealed record SystemLoadSnapshot
{
    public DateTimeOffset CapturedAt { get; init; }
    public double? CpuPercent { get; init; }
    public double? MemoryUsedMb { get; init; }
    public double? MemoryTotalMb { get; init; }
    public int ProcessCount { get; init; }
    public bool GpuAvailable { get; init; }
    public string? GpuName { get; init; }
    public double? GpuPercent { get; init; }
    public double? GpuDedicatedUsedMb { get; init; }
    public double? GpuDedicatedTotalMb { get; init; }
    public double? GpuTemperatureC { get; init; }
}

public interface ISystemLoadProvider
{
    SystemLoadSnapshot Sample();
}

/// <summary>Reads version/publisher/signature facts for an executable. Results are cached per file.</summary>
public interface IExecutableIdentityProvider
{
    Task<ExecutableFingerprint?> DescribeAsync(
        string path,
        bool includeSignature,
        bool includeHash,
        CancellationToken cancellationToken = default);
}

/// <summary>Searches likely install locations for an executable whose saved path disappeared.</summary>
public interface IExecutableLocator
{
    Task<IReadOnlyList<string>> FindCandidatePathsAsync(
        string missingPath,
        string exeName,
        string? productName,
        int maxResults,
        CancellationToken cancellationToken = default);
}

public sealed record GlobalProcessSnapshot
{
    public int ProcessId { get; init; }
    public int? ParentProcessId { get; init; }
    public string? ParentProcessName { get; init; }
    public int DescendantProcessCount { get; init; }
    public required string Name { get; init; }
    public string? WindowTitle { get; init; }
    public string? FullPath { get; init; }
    public string? FileDescription { get; init; }
    public string? ProductName { get; init; }
    public string? CompanyName { get; init; }
    public string? SignerStatus { get; init; }
    public string? Version { get; init; }
    public string? OriginalFileName { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public bool IsNewSincePreviousScan { get; init; }
    public bool StartedRecently { get; init; }
    public double? CpuPercent { get; init; }
    public double? MemoryMb { get; init; }
    public double? MemoryDeltaMb { get; init; }
    public double? DiskReadMbPerSec { get; init; }
    public double? DiskWriteMbPerSec { get; init; }
    public double? GpuPercent { get; init; }
    public double? GpuDedicatedMb { get; init; }
}

public interface IRamAccountingDiagnosticProvider
{
    Task<RamAccountingDiagnosticSnapshot> CaptureAsync(
        TargetDescriptor target,
        CancellationToken cancellationToken = default);
}

public sealed record RamAccountingDiagnosticSnapshot
{
    public int RootProcessId { get; init; }
    public required string RootProcessName { get; init; }
    public int? RootParentProcessId { get; init; }
    public bool IncludeChildProcesses { get; init; }
    public required string MemoryMetricName { get; init; }
    public double AggregatedMemoryMb { get; init; }
    public IReadOnlyList<RamAccountingProcessSnapshot> Processes { get; init; } = [];
}

public sealed record RamAccountingProcessSnapshot
{
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public int? ParentProcessId { get; init; }
    public bool IsRoot { get; init; }
    public double MemoryMb { get; init; }
    public required string MemoryMetricName { get; init; }
}

public sealed record SessionRecordWithoutSummary
{
    public required string Id { get; init; }
    public required TargetDescriptor Target { get; init; }
    public SessionStatus Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public SamplingSettings Sampling { get; init; } = new();
    public MetricCapabilities Capabilities { get; init; } = new();
    public IReadOnlyList<MetricSample> Samples { get; init; } = [];
    public IReadOnlyList<PerformanceEvent> Events { get; init; } = [];
    public string? Notes { get; init; }
    public int SchemaVersion { get; init; } = 1;

    public SessionRecord WithSummary(SessionSummary summary) => new()
    {
        Id = Id,
        Target = Target,
        Status = Status,
        StartedAt = StartedAt,
        EndedAt = EndedAt,
        Sampling = Sampling,
        Capabilities = Capabilities,
        Samples = Samples,
        Events = Events,
        Summary = summary,
        Notes = Notes,
        SchemaVersion = SchemaVersion
    };
}
