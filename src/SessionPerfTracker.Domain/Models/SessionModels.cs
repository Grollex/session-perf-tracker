namespace SessionPerfTracker.Domain.Models;

public sealed record TargetDescriptor
{
    public required string Id { get; init; }
    public TargetSelectionKind Kind { get; init; }
    public TargetLifecycleMode LifecycleMode { get; init; }
    public required string DisplayName { get; init; }
    public string? ExecutablePath { get; init; }
    public int? ProcessId { get; init; }
    public string? WindowTitle { get; init; }
    public bool IncludeChildProcesses { get; init; }
    public ProcessScopeMode ScopeMode { get; init; }
}

public sealed record SamplingSettings
{
    public string Mode { get; init; } = "Auto";
    public int IntervalMs { get; init; } = 1000;
    public int LiveUiRefreshMs { get; init; } = 2000;
    public int StorageBatchSize { get; init; } = 10;
    public string SessionProfileMode { get; init; } = "Auto";
    public string? SessionProfileId { get; init; }
    public string? SessionProfileName { get; init; }
    public ThresholdLimitValues? SessionThresholds { get; init; }
    public string? ThresholdSourceLabel { get; init; }
    public bool CaptureCpu { get; init; } = true;
    public bool CaptureRam { get; init; } = true;
    public bool CaptureDiskRead { get; init; } = true;
    public bool CaptureDiskWrite { get; init; } = true;
}

public sealed record MetricCapabilities
{
    public MetricReliability CpuPercent { get; init; } = MetricReliability.Unavailable;
    public MetricReliability MemoryMb { get; init; } = MetricReliability.Unavailable;
    public MetricReliability GpuPercent { get; init; } = MetricReliability.Unavailable;
    public MetricReliability DiskReadMbPerSec { get; init; } = MetricReliability.Unavailable;
    public MetricReliability DiskWriteMbPerSec { get; init; } = MetricReliability.Unavailable;
    public MetricReliability TemperatureC { get; init; } = MetricReliability.Unavailable;

    public MetricReliability For(MetricKey key) => key switch
    {
        MetricKey.CpuPercent => CpuPercent,
        MetricKey.MemoryMb => MemoryMb,
        MetricKey.GpuPercent => GpuPercent,
        MetricKey.DiskReadMbPerSec => DiskReadMbPerSec,
        MetricKey.DiskWriteMbPerSec => DiskWriteMbPerSec,
        MetricKey.TemperatureC => TemperatureC,
        _ => MetricReliability.Unavailable
    };
}

public sealed record MetricSample
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public long ElapsedMs { get; init; }
    public int? RootProcessId { get; init; }
    public int ProcessCount { get; init; }
    public Dictionary<MetricKey, double> Values { get; init; } = new();
    public Dictionary<MetricKey, MetricReliability> SourceReliability { get; init; } = new();
}

public sealed record ContextProcessSnapshot
{
    public int ProcessId { get; init; }
    public required string Name { get; init; }
    public double? CpuPercent { get; init; }
    public double? MemoryMb { get; init; }
    public double? DiskMbPerSec { get; init; }
    public double? DiskReadMbPerSec { get; init; }
    public double? DiskWriteMbPerSec { get; init; }
    public double? GpuPercent { get; init; }
}

public sealed record SpikeContextSnapshot
{
    public string? TriggerEventId { get; init; }
    public EventKind? TriggerEventKind { get; init; }
    public MetricKey? TriggerMetricKey { get; init; }
    public DateTimeOffset? TriggeredAt { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public string? RootTargetName { get; init; }
    public int? RootProcessId { get; init; }
    public int WindowMsBefore { get; init; }
    public int WindowMsAfter { get; init; }
    public IReadOnlyList<ContextProcessSnapshot> TopProcessesByCpu { get; init; } = [];
    public IReadOnlyList<ContextProcessSnapshot> TopProcessesByMemory { get; init; } = [];
    public IReadOnlyList<ContextProcessSnapshot> TopProcessesByDisk { get; init; } = [];
    public IReadOnlyList<ContextProcessSnapshot> TopProcessesByGpu { get; init; } = [];
    public IReadOnlyList<string> NewProcessNames { get; init; } = [];
    public string? CaptureProvider { get; init; }
    public string? Note { get; init; }
}

public sealed record PerformanceEvent
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public EventKind Kind { get; init; }
    public MetricKey? MetricKey { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public long ElapsedMs { get; init; }
    public EventSeverity Severity { get; init; }
    public required string Title { get; init; }
    public required string Details { get; init; }
    public double? ObservedValue { get; init; }
    public double? ThresholdValue { get; init; }
    public SpikeContextSnapshot? Context { get; init; }
    public IReadOnlyList<EventKind> GroupedKinds { get; init; } = [];
    public string? NoisePolicy { get; init; }
    public required string DetectionProvider { get; init; }
}

public sealed record MetricSummary
{
    public MetricKey Key { get; init; }
    public required string Label { get; init; }
    public required string Unit { get; init; }
    public ComparisonDirection Direction { get; init; }
    public double Min { get; init; }
    public double Avg { get; init; }
    public double Max { get; init; }
    public int Samples { get; init; }
    public int Spikes { get; init; }
    public int ThresholdBreaches { get; init; }
    public MetricReliability Reliability { get; init; }
}

public sealed record SelfOverheadSummary
{
    public double? AvgCpuPercent { get; init; }
    public double? MaxCpuPercent { get; init; }
    public double? AvgMemoryMb { get; init; }
    public double? MaxMemoryMb { get; init; }
    public double? AvgDiskWriteMbPerSec { get; init; }
    public int SampleCount { get; init; }
}

public sealed record SessionSummary
{
    public required string SessionId { get; init; }
    public required string TargetName { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public int SampleCount { get; init; }
    public int EventCount { get; init; }
    public int SpikeCount { get; init; }
    public int ThresholdBreachCount { get; init; }
    public int HangSuspectedCount { get; init; }
    public bool CrashLikeExit { get; init; }
    public int? LongStartupMs { get; init; }
    public SessionExitKind ExitKind { get; init; } = SessionExitKind.Unknown;
    public string? ExitReason { get; init; }
    public StabilityStatus StabilityStatus { get; init; } = StabilityStatus.Stable;
    public string? StabilityReason { get; init; }
    public IReadOnlyList<MetricSummary> Metrics { get; init; } = [];
    public SelfOverheadSummary? Overhead { get; init; }
}

public sealed record SessionRecord
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
    public required SessionSummary Summary { get; init; }
    public string? Notes { get; init; }
    public int SchemaVersion { get; init; } = 1;
}

public sealed record MetricComparison
{
    public MetricKey Key { get; init; }
    public required string Label { get; init; }
    public required string Unit { get; init; }
    public ComparisonDirection Direction { get; init; }
    public MetricSummary? Left { get; init; }
    public MetricSummary? Right { get; init; }
    public double? AvgDelta { get; init; }
    public double? AvgDeltaPercent { get; init; }
    public double? MaxDelta { get; init; }
    public required string Winner { get; init; }
}

public sealed record SessionComparisonResult
{
    public required string LeftSessionId { get; init; }
    public required string RightSessionId { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public TimeSpan DurationDelta { get; init; }
    public int EventDelta { get; init; }
    public int SpikeDelta { get; init; }
    public int ThresholdBreachDelta { get; init; }
    public int HangDelta { get; init; }
    public IReadOnlyList<MetricComparison> MetricComparisons { get; init; } = [];
}
