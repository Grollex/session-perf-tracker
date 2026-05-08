namespace SessionPerfTracker.Domain.Models;

public enum TargetSelectionKind
{
    Executable,
    Process,
    Window
}

public enum TargetLifecycleMode
{
    LaunchAndTrack,
    AttachToRunning
}

public enum ProcessScopeMode
{
    RootOnly,
    IncludeChildProcesses
}

public enum SessionStatus
{
    Planned,
    Running,
    Completed,
    Stopped,
    ExternalExit,
    UnexpectedExit,
    CrashLikeExit
}

public enum SessionExitKind
{
    Unknown,
    Running,
    NormalStop,
    Completed,
    ExternalClose,
    UnexpectedExit,
    CrashLikeExit
}

public enum StabilityStatus
{
    Stable,
    Degraded,
    Hung,
    Crashed
}

public enum MetricKey
{
    CpuPercent,
    MemoryMb,
    GpuPercent,
    DiskReadMbPerSec,
    DiskWriteMbPerSec,
    TemperatureC
}

public enum MetricReliability
{
    Stable,
    BestEffort,
    Unavailable
}

public enum EventKind
{
    ThresholdBreach,
    Spike,
    HangSuspected,
    LongStartup,
    ExternalExit,
    UnexpectedExit,
    CrashLikeExit
}

public enum EventSeverity
{
    Info,
    Warning,
    Critical
}

public enum ComparisonDirection
{
    LowerIsBetter,
    HigherIsBetter,
    Neutral
}
