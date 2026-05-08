using System.Diagnostics;
using System.IO;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Metrics;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.App.ViewModels;

public sealed class SessionListItemViewModel
{
    public SessionListItemViewModel(SessionRecord session)
    {
        Session = session;
    }

    public SessionRecord Session { get; }
    public string Id => Session.Id;
    public string DisplayName => Session.Target.DisplayName;
    public string AppName => GetAppName(Session);
    public string StartedText => Session.StartedAt.ToLocalTime().ToString("MMM dd, HH:mm");
    public string StartedFullText => Session.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string DurationText => FormatDuration(Session.Summary.Duration);
    public string ChildTrackingText => Session.Target.IncludeChildProcesses ? "child ON" : "child OFF";
    public string SamplingText => $"{Session.Sampling.IntervalMs} ms";
    public bool IsDemoSession => Session.Id.Contains("mock", StringComparison.OrdinalIgnoreCase)
        || (Session.Notes?.Contains("Mock", StringComparison.OrdinalIgnoreCase) ?? false);
    public bool IsShortSession => !IsDemoSession
        && (Session.Summary.SampleCount < 3 || Session.Summary.Duration < TimeSpan.FromSeconds(5));
    public bool IsPrimarySession => !IsDemoSession && !IsShortSession;
    public string QualityTag => IsDemoSession ? "DEMO" : IsShortSession ? "SHORT" : string.Empty;
    public string QualityHint => IsDemoSession
        ? "Sample data"
        : IsShortSession
            ? "Very short"
            : "Recorded session";
    public string StabilityText => $"Stability: {Session.Summary.StabilityStatus}";
    public string StabilityReason => Session.Summary.StabilityReason ?? "No stability summary.";
    public string StartupText => Session.Summary.LongStartupMs is null
        ? "Startup: no long startup event"
        : $"Startup: {TimeSpan.FromMilliseconds(Session.Summary.LongStartupMs.Value).TotalSeconds:N1}s";
    public string ExitText => Session.Summary.CrashLikeExit
        ? "Exit: crash-like"
        : Session.Summary.ExitKind switch
        {
            SessionExitKind.NormalStop => "Exit: stopped in utility",
            SessionExitKind.ExternalClose => "Exit: external close / graceful",
            SessionExitKind.UnexpectedExit => "Exit: unexpected",
            SessionExitKind.CrashLikeExit => "Exit: crash-like",
            SessionExitKind.Running => "Exit: still running",
            SessionExitKind.Completed => "Exit: completed",
            _ => "Exit: unknown"
        };
    public string SessionLabel => $"{AppName} - {StartedFullText} - {DurationText} - {ChildTrackingText} - {SamplingText}";
    public string CompareLabel => SessionLabel;
    public string StatusText => Session.Status switch
    {
        SessionStatus.Completed => "Completed",
        SessionStatus.Running => "Recording",
        SessionStatus.Stopped => "Stopped by user",
        SessionStatus.ExternalExit => "Closed externally",
        SessionStatus.UnexpectedExit => "Ended unexpectedly",
        SessionStatus.CrashLikeExit => "Crash-like exit",
        SessionStatus.Planned => "Not recorded",
        _ => Session.Status.ToString()
    };
    public string EventCountText => Session.Summary.EventCount.ToString("N0");
    public string SpikeCountText => Session.Summary.SpikeCount.ToString("N0");
    public string BreachCountText => Session.Summary.ThresholdBreachCount.ToString("N0");
    public string HangCountText => Session.Summary.HangSuspectedCount.ToString("N0");
    public string SampleCountText => Session.Summary.SampleCount.ToString("N0");

    public override string ToString() => SessionLabel;

    private static string FormatDuration(TimeSpan duration) =>
        $"{(int)duration.TotalMinutes}m {duration.Seconds:00}s";

    private static string GetAppName(SessionRecord session)
    {
        if (!string.IsNullOrWhiteSpace(session.Target.ExecutablePath))
        {
            return Path.GetFileName(session.Target.ExecutablePath);
        }

        var displayName = session.Target.DisplayName;
        var pidMarkerIndex = displayName.IndexOf(" (", StringComparison.Ordinal);
        return pidMarkerIndex > 0 ? displayName[..pidMarkerIndex] : displayName;
    }
}

public sealed class MetricValueViewModel
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public required string Reliability { get; init; }
}

public sealed class TargetOptionViewModel
{
    public TargetOptionViewModel(SessionPerfTracker.Domain.Models.TargetDescriptor target)
    {
        Target = target;
    }

    public SessionPerfTracker.Domain.Models.TargetDescriptor Target { get; }
    public int ProcessId => Target.ProcessId ?? 0;
    public string DisplayName => Target.DisplayName;

    public override string ToString() => DisplayName;
}

public sealed class SamplingOptionViewModel
{
    public SamplingOptionViewModel(int intervalMs)
    {
        IntervalMs = intervalMs;
    }

    public int IntervalMs { get; }
    public string Label => $"{IntervalMs} ms";

    public override string ToString() => Label;
}

public sealed class RetentionOptionViewModel
{
    public RetentionOptionViewModel(int? days, string label)
    {
        Days = days;
        Label = label;
    }

    public int? Days { get; }
    public string Label { get; }

    public override string ToString() => Label;
}

public sealed class SessionProfileFilterOptionViewModel
{
    public SessionProfileFilterOptionViewModel(string label, string? profileId = null, bool isGlobalFallback = false)
    {
        Label = label;
        ProfileId = profileId;
        IsGlobalFallback = isGlobalFallback;
    }

    public string Label { get; }
    public string? ProfileId { get; }
    public bool IsAll => ProfileId is null && !IsGlobalFallback;
    public bool IsGlobalFallback { get; }

    public override string ToString() => Label;
}

public sealed class ExportFileItemViewModel
{
    public ExportFileItemViewModel(SessionPerfTracker.Domain.Abstractions.ExportFileDescriptor file)
    {
        Path = file.Path;
        FileName = file.FileName;
        LastWriteText = file.LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        SizeText = FormatSize(file.SizeBytes);
        DisplayText = $"{FileName} - {LastWriteText} - {SizeText}";
    }

    public string Path { get; }
    public string FileName { get; }
    public string LastWriteText { get; }
    public string SizeText { get; }
    public string DisplayText { get; }

    public override string ToString() => DisplayText;

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var kb = bytes / 1024d;
        if (kb < 1024)
        {
            return $"{kb:N1} KB";
        }

        return $"{kb / 1024d:N1} MB";
    }
}

public sealed class ThresholdProfileOptionViewModel
{
    public ThresholdProfileOptionViewModel(ThresholdProfile profile)
    {
        Id = profile.Id;
        Name = profile.Name;
        DisplayName = profile.IsPreset ? profile.Name : $"{profile.Name} (editable)";
    }

    public string Id { get; }
    public string Name { get; }
    public string DisplayName { get; }

    public override string ToString() => DisplayName;
}

public sealed class SessionProfileOptionViewModel
{
    public SessionProfileOptionViewModel()
    {
        IsAuto = true;
        DisplayName = "Auto";
    }

    public SessionProfileOptionViewModel(ThresholdProfile profile)
    {
        ProfileId = profile.Id;
        ProfileName = profile.Name;
        DisplayName = profile.IsPreset ? profile.Name : $"{profile.Name} (editable)";
        Limits = profile.Limits;
    }

    public bool IsAuto { get; }
    public string? ProfileId { get; }
    public string? ProfileName { get; }
    public string DisplayName { get; }
    public ThresholdLimitValues? Limits { get; }

    public override string ToString() => DisplayName;
}

public sealed class AppProfileAssignmentViewModel
{
    public AppProfileAssignmentViewModel(string exeName, string profileName)
    {
        ExeName = exeName;
        ProfileName = profileName;
        DisplayText = $"{exeName} -> {profileName}";
    }

    public string ExeName { get; }
    public string ProfileName { get; }
    public string DisplayText { get; }

    public override string ToString() => DisplayText;
}

public sealed class AssignedTargetOptionViewModel
{
    public AssignedTargetOptionViewModel(
        string exeName,
        string profileId,
        string profileName,
        int? runningProcessId,
        string? runningDisplayName)
    {
        ExeName = exeName;
        ProfileId = profileId;
        ProfileName = profileName;
        RunningProcessId = runningProcessId;
        RunningDisplayName = runningDisplayName;
        RunningStatus = runningProcessId is null ? "Not running" : $"Running (PID {runningProcessId})";
        DisplayText = $"{ExeName} - {ProfileName} - {RunningStatus}";
    }

    public string ExeName { get; }
    public string ProfileId { get; }
    public string ProfileName { get; }
    public int? RunningProcessId { get; }
    public string? RunningDisplayName { get; }
    public bool IsRunning => RunningProcessId is not null;
    public string RunningStatus { get; }
    public string DisplayText { get; }

    public override string ToString() => DisplayText;
}

public sealed class GlobalProcessRowViewModel
{
    private const string StateOk = "OK";
    private const string StateNear = "Near limit";
    private const string StateOver = "Over limit";
    private const string StateCritical = "Critical";

    public GlobalProcessRowViewModel(
        GlobalProcessSnapshot process,
        string exeName,
        string profileId,
        string profileName,
        bool isAssignedProfile,
        ThresholdLimitValues profileLimits)
    {
        Process = process;
        ExeName = exeName;
        AppName = Prefer(process.ProductName, process.FileDescription, process.Name);
        IsGroup = false;
        InstanceCount = 1;
        SelectionKey = $"pid:{process.ProcessId}";
        IncludedProcesses = [process];
        ProcessId = process.ProcessId;
        Name = process.Name;
        FullPath = string.IsNullOrWhiteSpace(process.FullPath) ? "Unavailable" : process.FullPath;
        NormalizedFullPath = NormalizeFullPath(process.FullPath);
        FileDescription = Prefer(process.FileDescription, "Unavailable");
        ProductName = Prefer(process.ProductName, AppName);
        CompanyName = Prefer(process.CompanyName, "Unavailable");
        SignerStatus = Prefer(process.SignerStatus, "Unknown");
        Version = Prefer(process.Version, "Unavailable");
        OriginalFileName = Prefer(process.OriginalFileName, "Unavailable");
        ParentProcessText = process.ParentProcessId is null
            ? "None"
            : $"{Prefer(process.ParentProcessName, "Unknown")} ({process.ParentProcessId})";
        DescendantCountText = process.DescendantProcessCount.ToString("N0");
        DisplayName = string.IsNullOrWhiteSpace(process.WindowTitle)
            ? $"{process.Name} ({process.ProcessId})"
            : $"{process.Name} ({process.ProcessId}) - {process.WindowTitle}";
        ProcessIdText = process.ProcessId.ToString();
        IncludedProcessRows = [new GlobalProcessMemberViewModel(process)];
        PidOrCountText = ProcessIdText;
        CpuPercent = process.CpuPercent;
        MemoryMb = process.MemoryMb;
        DiskReadMbPerSec = process.DiskReadMbPerSec;
        DiskWriteMbPerSec = process.DiskWriteMbPerSec;
        DiskTotalMbPerSec = (process.DiskReadMbPerSec ?? 0) + (process.DiskWriteMbPerSec ?? 0);
        CpuText = FormatPercent(process.CpuPercent);
        MemoryText = FormatMb(process.MemoryMb);
        DiskReadText = FormatMbPerSec(process.DiskReadMbPerSec);
        DiskWriteText = FormatMbPerSec(process.DiskWriteMbPerSec);
        MemoryDeltaText = FormatSignedMb(process.MemoryDeltaMb);
        StateText = process.IsNewSincePreviousScan
            ? "new"
            : process.StartedRecently ? "recent" : "running";
        ProfileId = profileId;
        ProfileName = profileName;
        IsAssignedProfile = isAssignedProfile;
        IsUnassigned = !isAssignedProfile;
        ProfileLimits = profileLimits;
        ProfileSourceText = isAssignedProfile
            ? profileName
            : $"Global fallback ({profileName})";
        ProfileBadgeText = FormatProfileBadge(profileId, profileName, isAssignedProfile);
        var evaluation = EvaluateProfileState(process, profileLimits, isAssignedProfile);
        ProfileState = evaluation.State;
        HealthBadgeText = FormatHealthBadge(evaluation.State);
        ProfileReason = evaluation.Reason;
        RecommendationReason = evaluation.RecommendationReason;
        IsNearLimit = evaluation.State == StateNear;
        IsOverLimit = evaluation.State == StateOver;
        IsCritical = evaluation.State == StateCritical;
        IsOutOfProfile = IsOverLimit || IsCritical;
        ProfileSeverityRank = GetProfileSeverityRank(evaluation.State);
        ProfileStatusText = $"{ProfileState} - {ProfileReason}";
        DiskTotalText = $"{DiskTotalMbPerSec:N1} MB/s";
        DiskReadWriteText = $"{DiskReadText} / {DiskWriteText}";
        TopCpuText = $"{Name} - {CpuText}";
        TopRamText = $"{Name} - {MemoryText}";
        TopDiskText = $"{Name} - {DiskTotalText}";
        IncludedProcessSummaryText = $"{Name} ({ProcessIdText})";
    }

    private GlobalProcessRowViewModel(
        IReadOnlyList<GlobalProcessRowViewModel> rows,
        string exeName,
        string profileId,
        string profileName,
        bool isAssignedProfile,
        ThresholdLimitValues profileLimits)
    {
        var orderedRows = rows
            .OrderByDescending(row => row.CpuPercent ?? 0)
            .ThenByDescending(row => row.MemoryMb ?? 0)
            .ToArray();
        var sameExeProcessIds = orderedRows
            .Select(row => row.ProcessId)
            .ToHashSet();
        var primary = orderedRows
            .Where(row => row.Process.ParentProcessId is null || !sameExeProcessIds.Contains(row.Process.ParentProcessId.Value))
            .OrderBy(row => row.Process.StartedAt ?? DateTimeOffset.MaxValue)
            .ThenByDescending(row => row.CpuPercent ?? 0)
            .FirstOrDefault()
            ?? orderedRows.First();
        Process = primary.Process;
        ExeName = exeName;
        AppName = Prefer(primary.Process.ProductName, primary.Process.FileDescription, exeName);
        IsGroup = true;
        InstanceCount = orderedRows.Length;
        SelectionKey = $"group:{exeName}";
        IncludedProcesses = orderedRows.SelectMany(row => row.IncludedProcesses).ToArray();
        ProcessId = primary.ProcessId;
        Name = exeName;
        FullPath = string.IsNullOrWhiteSpace(primary.Process.FullPath) ? "Unavailable" : primary.Process.FullPath;
        NormalizedFullPath = NormalizeFullPath(primary.Process.FullPath);
        FileDescription = Prefer(primary.Process.FileDescription, "Unavailable");
        ProductName = Prefer(primary.Process.ProductName, AppName);
        CompanyName = Prefer(primary.Process.CompanyName, "Unavailable");
        SignerStatus = Prefer(primary.Process.SignerStatus, "Unknown");
        Version = Prefer(primary.Process.Version, "Unavailable");
        OriginalFileName = Prefer(primary.Process.OriginalFileName, "Unavailable");
        ParentProcessText = "Application group";
        DescendantCountText = orderedRows.Sum(row => row.Process.DescendantProcessCount).ToString("N0");
        DisplayName = AppName;
        ProcessIdText = primary.ProcessIdText;
        IncludedProcessRows = orderedRows
            .Select(row => new GlobalProcessMemberViewModel(row.Process))
            .ToArray();
        PidOrCountText = $"{InstanceCount:N0}x";
        CpuPercent = orderedRows.Sum(row => row.CpuPercent ?? 0);
        MemoryMb = orderedRows.Sum(row => row.MemoryMb ?? 0);
        DiskReadMbPerSec = orderedRows.Sum(row => row.DiskReadMbPerSec ?? 0);
        DiskWriteMbPerSec = orderedRows.Sum(row => row.DiskWriteMbPerSec ?? 0);
        DiskTotalMbPerSec = (DiskReadMbPerSec ?? 0) + (DiskWriteMbPerSec ?? 0);
        CpuText = FormatPercent(CpuPercent);
        MemoryText = FormatMb(MemoryMb);
        DiskReadText = FormatMbPerSec(DiskReadMbPerSec);
        DiskWriteText = FormatMbPerSec(DiskWriteMbPerSec);
        MemoryDeltaText = FormatSignedMb(orderedRows.Sum(row => row.Process.MemoryDeltaMb ?? 0));
        StateText = orderedRows.Any(row => row.Process.IsNewSincePreviousScan)
            ? "new"
            : orderedRows.Any(row => row.Process.StartedRecently) ? "recent" : "running";
        ProfileId = profileId;
        ProfileName = profileName;
        IsAssignedProfile = isAssignedProfile;
        IsUnassigned = !isAssignedProfile;
        ProfileLimits = profileLimits;
        ProfileSourceText = isAssignedProfile
            ? profileName
            : $"Global fallback ({profileName})";
        ProfileBadgeText = FormatProfileBadge(profileId, profileName, isAssignedProfile);
        var aggregateSnapshot = new GlobalProcessSnapshot
        {
            ProcessId = primary.ProcessId,
            ParentProcessId = primary.Process.ParentProcessId,
            Name = exeName,
            StartedRecently = orderedRows.Any(row => row.Process.StartedRecently),
            IsNewSincePreviousScan = orderedRows.Any(row => row.Process.IsNewSincePreviousScan),
            CpuPercent = CpuPercent,
            MemoryMb = MemoryMb,
            MemoryDeltaMb = orderedRows.Sum(row => row.Process.MemoryDeltaMb ?? 0),
            DiskReadMbPerSec = DiskReadMbPerSec,
            DiskWriteMbPerSec = DiskWriteMbPerSec
        };
        var evaluation = EvaluateProfileState(aggregateSnapshot, profileLimits, isAssignedProfile);
        ProfileState = evaluation.State;
        HealthBadgeText = FormatHealthBadge(evaluation.State);
        ProfileReason = evaluation.Reason;
        RecommendationReason = evaluation.RecommendationReason;
        IsNearLimit = evaluation.State == StateNear;
        IsOverLimit = evaluation.State == StateOver;
        IsCritical = evaluation.State == StateCritical;
        IsOutOfProfile = IsOverLimit || IsCritical;
        ProfileSeverityRank = GetProfileSeverityRank(evaluation.State);
        ProfileStatusText = $"{ProfileState} - {ProfileReason}";
        DiskTotalText = $"{DiskTotalMbPerSec:N1} MB/s";
        DiskReadWriteText = $"{DiskReadText} / {DiskWriteText}";
        TopCpuText = $"{Name} - {CpuText}";
        TopRamText = $"{Name} - {MemoryText}";
        TopDiskText = $"{Name} - {DiskTotalText}";
        IncludedProcessSummaryText = $"Live root candidate: {primary.Name} ({primary.ProcessId}); "
            + string.Join("; ", orderedRows.Take(5).Select(row => $"{row.Name} ({row.ProcessId})"));
        if (orderedRows.Length > 5)
        {
            IncludedProcessSummaryText += $"; +{orderedRows.Length - 5:N0} more";
        }
    }

    public static GlobalProcessRowViewModel CreateGroup(IReadOnlyList<GlobalProcessRowViewModel> rows)
    {
        var first = rows.First();
        return new GlobalProcessRowViewModel(
            rows,
            first.ExeName,
            first.ProfileId,
            first.ProfileName,
            first.IsAssignedProfile,
            first.ProfileLimits);
    }

    public GlobalProcessSnapshot Process { get; }
    public string ExeName { get; }
    public string AppName { get; }
    public bool IsGroup { get; }
    public int InstanceCount { get; }
    public string SelectionKey { get; }
    public IReadOnlyList<GlobalProcessSnapshot> IncludedProcesses { get; }
    public int ProcessId { get; }
    public string ProcessIdText { get; }
    public IReadOnlyList<GlobalProcessMemberViewModel> IncludedProcessRows { get; }
    public string PidOrCountText { get; }
    public string Name { get; }
    public string DisplayName { get; }
    public string FullPath { get; }
    public string NormalizedFullPath { get; }
    public string FileDescription { get; }
    public string ProductName { get; }
    public string CompanyName { get; }
    public string SignerStatus { get; }
    public string Version { get; }
    public string OriginalFileName { get; }
    public string ParentProcessText { get; }
    public string DescendantCountText { get; }
    public string ProfileId { get; }
    public string ProfileName { get; }
    public string ProfileSourceText { get; }
    public string ProfileBadgeText { get; }
    public ThresholdLimitValues ProfileLimits { get; }
    public bool IsAssignedProfile { get; }
    public bool IsUnassigned { get; }
    public double? CpuPercent { get; }
    public double? MemoryMb { get; }
    public double? DiskReadMbPerSec { get; }
    public double? DiskWriteMbPerSec { get; }
    public double DiskTotalMbPerSec { get; }
    public string CpuText { get; }
    public string MemoryText { get; }
    public string MemoryDeltaText { get; }
    public string DiskReadText { get; }
    public string DiskWriteText { get; }
    public string DiskTotalText { get; }
    public string DiskReadWriteText { get; }
    public string StateText { get; }
    public string ProfileState { get; }
    public string HealthBadgeText { get; }
    public string ProfileReason { get; }
    public string ProfileStatusText { get; }
    public string RecommendationReason { get; }
    public int ProfileSeverityRank { get; }
    public bool IsNearLimit { get; }
    public bool IsOverLimit { get; }
    public bool IsCritical { get; }
    public bool IsOutOfProfile { get; }
    public string TopCpuText { get; }
    public string TopRamText { get; }
    public string TopDiskText { get; }
    public string IncludedProcessSummaryText { get; }

    public override string ToString() => DisplayName;

    private static ProfileStateEvaluation EvaluateProfileState(
        GlobalProcessSnapshot process,
        ThresholdLimitValues limits,
        bool isAssignedProfile)
    {
        var candidates = new[]
            {
                CreateMetricRatio("CPU", process.CpuPercent, limits.CpuThresholdPercent),
                CreateMetricRatio("RAM", process.MemoryMb, limits.RamThresholdMb),
                CreateMetricRatio("Disk Read", process.DiskReadMbPerSec, limits.DiskReadThresholdMbPerSec),
                CreateMetricRatio("Disk Write", process.DiskWriteMbPerSec, limits.DiskWriteThresholdMbPerSec)
            }
            .Where(item => item is not null)
            .Cast<MetricRatio>()
            .OrderByDescending(item => item.Ratio)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new ProfileStateEvaluation(
                "OK",
                "Waiting for scan metrics",
                "Waiting for scan metrics");
        }

        var top = candidates[0];
        var overCount = candidates.Count(item => item.Ratio >= 1.0);
        var limitLabel = isAssignedProfile ? "profile limit" : "fallback limit";
        var state = top.Ratio switch
        {
            >= 1.5 => StateCritical,
            >= 1.15 when overCount >= 2 => StateCritical,
            >= 1.0 => StateOver,
            >= 0.85 => StateNear,
            _ => StateOk
        };

        var reason = state switch
        {
            "Critical" => $"{top.Label} critical over {limitLabel}",
            "Over limit" => $"{top.Label} over {limitLabel}",
            "Near limit" => $"{top.Label} near {limitLabel}",
            _ => "Within profile"
        };

        return new ProfileStateEvaluation(state, reason, $"{top.Label} over {limitLabel}");
    }

    private static MetricRatio? CreateMetricRatio(string label, double? value, double limit)
    {
        if (value is null || limit <= 0)
        {
            return null;
        }

        return new MetricRatio(label, value.Value / limit);
    }

    private sealed record MetricRatio(string Label, double Ratio);
    private sealed record ProfileStateEvaluation(string State, string Reason, string RecommendationReason);

    private static string FormatProfileBadge(string profileId, string profileName, bool isAssignedProfile)
    {
        if (!isAssignedProfile)
        {
            return "Fallback";
        }

        return profileId switch
        {
            ThresholdProfileDefaults.LightBackgroundId => "Light",
            ThresholdProfileDefaults.BrowsersChatsId => "Browser",
            ThresholdProfileDefaults.GamesId => "Game",
            ThresholdProfileDefaults.HardcoreId => "Hardcore",
            ThresholdProfileDefaults.CustomId => "Custom",
            _ => profileName
        };
    }

    private static string FormatHealthBadge(string state) => state switch
    {
        StateNear => "Near",
        StateOver => "Over",
        StateCritical => "Critical",
        _ => "OK"
    };

    private static int GetProfileSeverityRank(string state) => state switch
    {
        StateCritical => 3,
        StateOver => 2,
        StateNear => 1,
        _ => 0
    };

    private static string FormatPercent(double? value) =>
        value is null ? "warming" : $"{value.Value:N1}%";

    private static string FormatMb(double? value) =>
        value is null ? "n/a" : $"{value.Value:N1} MB";

    private static string FormatMbPerSec(double? value) =>
        value is null ? "warming" : $"{value.Value:N1} MB/s";

    private static string FormatSignedMb(double? value)
    {
        if (value is null)
        {
            return "n/a";
        }

        return Math.Abs(value.Value) < 0.05
            ? "0.0 MB"
            : $"{value.Value:+0.0;-0.0} MB";
    }

    private static string Prefer(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return "Unavailable";
    }

    private static string NormalizeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || string.Equals(path.Trim(), "Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }
        catch
        {
            return path.Trim().ToLowerInvariant();
        }
    }
}

public sealed class GlobalProcessMemberViewModel
{
    public GlobalProcessMemberViewModel(GlobalProcessSnapshot process)
    {
        Name = process.Name;
        ProcessIdText = process.ProcessId.ToString();
        ParentText = process.ParentProcessId is null
            ? "root"
            : $"{(string.IsNullOrWhiteSpace(process.ParentProcessName) ? "parent" : process.ParentProcessName)} ({process.ParentProcessId})";
        CpuText = process.CpuPercent is null ? "warming" : $"{process.CpuPercent.Value:N1}%";
        RamText = process.MemoryMb is null ? "n/a" : $"{process.MemoryMb.Value:N1} MB";
        var read = process.DiskReadMbPerSec ?? 0;
        var write = process.DiskWriteMbPerSec ?? 0;
        DiskText = $"R {read:N1} / W {write:N1} MB/s";
        RoleText = process.DescendantProcessCount > 0
            ? $"{process.DescendantProcessCount:N0} descendants"
            : process.ParentProcessId is null ? "root candidate" : "helper/subprocess";
    }

    public string Name { get; }
    public string ProcessIdText { get; }
    public string ParentText { get; }
    public string CpuText { get; }
    public string RamText { get; }
    public string DiskText { get; }
    public string RoleText { get; }
}

public sealed class ProcessInspectorTargetViewModel
{
    public ProcessInspectorTargetViewModel(
        string title,
        string sourceText,
        string modeText,
        string exeName,
        string displayName,
        string fullPath,
        string normalizedFullPath,
        string productName,
        string fileDescription,
        string companyName,
        string signerStatus,
        string version,
        string originalFileName,
        string parentText,
        string descendantsText,
        string pidText,
        string cpuText,
        string ramText,
        string diskText,
        string statusText,
        string profileText,
        string healthText,
        string reasonText,
        string includedText,
        string relationText,
        string appearsBecauseText,
        string commandLineText,
        bool isGroup,
        bool isRunning,
        GlobalProcessRowViewModel? activeRow = null,
        IReadOnlyList<GlobalProcessMemberViewModel>? includedProcessRows = null)
    {
        Title = title;
        SourceText = sourceText;
        ModeText = modeText;
        ExeName = string.IsNullOrWhiteSpace(exeName) ? "n/a" : exeName;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Title : displayName;
        FullPath = string.IsNullOrWhiteSpace(fullPath) ? "Unavailable" : fullPath;
        NormalizedFullPath = NormalizePath(normalizedFullPath, fullPath);
        ProductName = string.IsNullOrWhiteSpace(productName) ? "Unknown product" : productName;
        FileDescription = string.IsNullOrWhiteSpace(fileDescription) ? "Unavailable" : fileDescription;
        CompanyName = string.IsNullOrWhiteSpace(companyName) ? "Unknown company" : companyName;
        SignerStatus = string.IsNullOrWhiteSpace(signerStatus) ? "Unknown signer" : signerStatus;
        Version = string.IsNullOrWhiteSpace(version) ? "Unavailable" : version;
        OriginalFileName = string.IsNullOrWhiteSpace(originalFileName) ? "Unavailable" : originalFileName;
        ParentText = string.IsNullOrWhiteSpace(parentText) ? "n/a" : parentText;
        DescendantsText = string.IsNullOrWhiteSpace(descendantsText) ? "n/a" : descendantsText;
        PidText = string.IsNullOrWhiteSpace(pidText) ? "n/a" : pidText;
        CpuText = string.IsNullOrWhiteSpace(cpuText) ? "n/a" : cpuText;
        RamText = string.IsNullOrWhiteSpace(ramText) ? "n/a" : ramText;
        DiskText = string.IsNullOrWhiteSpace(diskText) ? "n/a" : diskText;
        StatusText = string.IsNullOrWhiteSpace(statusText) ? "not running" : statusText;
        ProfileText = string.IsNullOrWhiteSpace(profileText) ? "n/a" : profileText;
        HealthText = string.IsNullOrWhiteSpace(healthText) ? "n/a" : healthText;
        ReasonText = string.IsNullOrWhiteSpace(reasonText) ? "No watch reason available." : reasonText;
        IncludedText = string.IsNullOrWhiteSpace(includedText) ? "n/a" : includedText;
        RelationText = string.IsNullOrWhiteSpace(relationText) ? "No live process tree is available." : relationText;
        AppearsBecauseText = string.IsNullOrWhiteSpace(appearsBecauseText) ? "Loaded from saved watch data." : appearsBecauseText;
        CommandLineText = string.IsNullOrWhiteSpace(commandLineText) ? "Not captured in lightweight scan." : commandLineText;
        IsGroup = isGroup;
        IsRunning = isRunning;
        ActiveRow = activeRow;
        IncludedProcessRows = includedProcessRows ?? [];
    }

    public string Title { get; }
    public string SourceText { get; }
    public string ModeText { get; }
    public string ExeName { get; }
    public string DisplayName { get; }
    public string FullPath { get; }
    public string NormalizedFullPath { get; }
    public string ProductName { get; }
    public string FileDescription { get; }
    public string CompanyName { get; }
    public string SignerStatus { get; }
    public string Version { get; }
    public string OriginalFileName { get; }
    public string ParentText { get; }
    public string DescendantsText { get; }
    public string PidText { get; }
    public string CpuText { get; }
    public string RamText { get; }
    public string DiskText { get; }
    public string StatusText { get; }
    public string ProfileText { get; }
    public string HealthText { get; }
    public string ReasonText { get; }
    public string IncludedText { get; }
    public string RelationText { get; }
    public string AppearsBecauseText { get; }
    public string CommandLineText { get; }
    public bool IsGroup { get; }
    public bool IsRunning { get; }
    public GlobalProcessRowViewModel? ActiveRow { get; }
    public IReadOnlyList<GlobalProcessMemberViewModel> IncludedProcessRows { get; }
    public bool HasUsablePath => !string.IsNullOrWhiteSpace(NormalizedFullPath)
        && !string.Equals(FullPath, "Unavailable", StringComparison.OrdinalIgnoreCase);

    public static ProcessInspectorTargetViewModel FromGlobalProcess(
        GlobalProcessRowViewModel row,
        string sourceText,
        bool isApplicationsMode)
    {
        var title = row.IsGroup ? row.AppName : row.DisplayName;
        var modeText = row.IsGroup || isApplicationsMode
            ? "Applications mode: aggregated application-level view"
            : "Processes mode: individual process detail view";
        var descendantsText = row.IsGroup
            ? $"{row.InstanceCount:N0} instances; {row.IncludedProcesses.Count:N0} included processes"
            : $"{row.DescendantCountText} descendants";
        var relationText = row.IsGroup
            ? $"Application group for {row.ExeName}; contains {row.InstanceCount:N0} running instance(s)."
            : row.Process.ParentProcessId is not null
                ? $"Likely helper/subprocess under {row.ParentProcessText}."
                : row.Process.DescendantProcessCount > 0
                    ? $"Root or broker candidate with {row.Process.DescendantProcessCount:N0} descendant(s)."
                    : "Standalone/root process candidate; no descendants were visible in the last scan.";
        var appearsBecauseText = row.IsGroup
            ? "Shown because Global Watch groups currently running processes by exe name in Applications mode."
            : "Shown because this PID was present in the last lightweight Global Watch scan.";

        return new ProcessInspectorTargetViewModel(
            title,
            sourceText,
            modeText,
            row.ExeName,
            row.DisplayName,
            row.FullPath,
            row.NormalizedFullPath,
            row.ProductName,
            row.FileDescription,
            row.CompanyName,
            row.SignerStatus,
            row.Version,
            row.OriginalFileName,
            row.ParentProcessText,
            descendantsText,
            row.IsGroup ? $"{row.InstanceCount:N0} instances" : row.ProcessIdText,
            row.CpuText,
            $"{row.MemoryText} ({row.MemoryDeltaText})",
            row.DiskReadWriteText,
            row.StateText,
            row.ProfileSourceText,
            row.HealthBadgeText,
            row.ProfileReason,
            row.IncludedProcessSummaryText,
            relationText,
            appearsBecauseText,
            "Not captured in lightweight scan.",
            row.IsGroup,
            isRunning: true,
            row,
            row.IncludedProcessRows);
    }

    public static ProcessInspectorTargetViewModel FromSuspiciousItem(
        SuspiciousWatchItem item,
        GlobalProcessRowViewModel? activeRow = null)
    {
        if (activeRow is not null)
        {
            return FromGlobalProcess(activeRow, "Suspicious watchlist - currently running", activeRow.IsGroup);
        }

        var fileInfo = TryReadFileVersionInfo(item.NormalizedPath);
        var productName = FirstUseful(item.ProductName, fileInfo?.ProductName, item.ExeName);
        var companyName = FirstUseful(item.CompanyName, fileInfo?.CompanyName, "Unknown company");
        var fileDescription = FirstUseful(fileInfo?.FileDescription, productName, "Unavailable while process is not running");
        var version = FirstUseful(fileInfo?.FileVersion, fileInfo?.ProductVersion, "Unavailable");
        var originalFileName = FirstUseful(fileInfo?.OriginalFilename, "Unavailable");
        var note = string.IsNullOrWhiteSpace(item.Note)
            ? "Manually marked suspicious."
            : item.Note;

        return new ProcessInspectorTargetViewModel(
            productName,
            "Suspicious watchlist - saved path entry",
            "Offline details: process is not running in the latest scan",
            item.ExeName,
            productName,
            item.NormalizedPath,
            item.NormalizedPath,
            productName,
            fileDescription,
            companyName,
            item.SignerStatus ?? "Unknown signer",
            version,
            originalFileName,
            "n/a",
            "n/a",
            "not running",
            "n/a",
            "n/a",
            "n/a",
            "not running",
            "n/a",
            "Suspicious",
            note,
            "Saved path entry",
            "No live process tree is available until it starts again.",
            "Saved because this executable path was manually marked suspicious.",
            "Not captured.",
            isGroup: false,
            isRunning: false);
    }

    public static ProcessInspectorTargetViewModel FromRecommendation(
        ProfileRecommendationRecord recommendation,
        GlobalProcessRowViewModel? activeRow = null)
    {
        if (activeRow is not null)
        {
            return FromGlobalProcess(activeRow, "Profile recommendation - currently running", activeRow.IsGroup);
        }

        return new ProcessInspectorTargetViewModel(
            recommendation.ExeName,
            "Profile recommendation - saved recommendation",
            "Offline details: process is not running in the latest scan",
            recommendation.ExeName,
            recommendation.ExeName,
            "Unavailable",
            string.Empty,
            recommendation.ExeName,
            "Unavailable while process is not running",
            "Unknown company",
            "Unknown signer",
            "Unavailable",
            "Unavailable",
            "n/a",
            "n/a",
            "not running",
            "n/a",
            "n/a",
            "not running",
            "not running",
            $"{recommendation.CurrentProfileSource}: {recommendation.CurrentProfileName}",
            "Recommendation",
            recommendation.Reason,
            $"{recommendation.WarningCount:N0} grouped warning(s)",
            "Loaded from persisted Global Watch recommendation.",
            $"Recommendation suggests {recommendation.SuggestedProfileName}; first seen {recommendation.FirstSeen.ToLocalTime():yyyy-MM-dd HH:mm:ss}.",
            "Not captured.",
            isGroup: true,
            isRunning: false);
    }

    public static ProcessInspectorTargetViewModel FromJournalGroup(
        GlobalWatchJournalGroupViewModel group,
        GlobalProcessRowViewModel? activeRow = null)
    {
        if (activeRow is not null)
        {
            return FromGlobalProcess(activeRow, "Watch Journal - currently running", activeRow.IsGroup);
        }

        return new ProcessInspectorTargetViewModel(
            group.IdentityText,
            "Watch Journal - saved watch entry",
            $"Offline details: {group.ModeText}",
            group.ExeName,
            group.IdentityText,
            "Unavailable",
            string.Empty,
            group.IdentityText,
            "Unavailable while process is not running",
            "Unknown company",
            "Unknown signer",
            "Unavailable",
            "Unavailable",
            "n/a",
            "n/a",
            group.ProcessId is null ? "not running" : group.ProcessId.Value.ToString(),
            FormatPercent(group.Entry.CpuPercent),
            FormatMb(group.Entry.MemoryMb),
            $"R {FormatMbPerSec(group.Entry.DiskReadMbPerSec)} / W {FormatMbPerSec(group.Entry.DiskWriteMbPerSec)}",
            "journal entry",
            group.ProfileText,
            group.HealthText,
            group.Reason,
            $"{group.CountText}; latest {group.LatestText}",
            "Loaded from grouped Global Watch journal history.",
            $"This branch was recorded {group.CountText} with health {group.HealthText}.",
            "Not captured.",
            isGroup: group.ProcessId is null || group.ModeText.Contains("Applications", StringComparison.OrdinalIgnoreCase),
            isRunning: false);
    }

    private static string FormatPercent(double? value) =>
        value is null ? "n/a" : $"{value.Value:N1}%";

    private static string FormatMb(double? value) =>
        value is null ? "n/a" : $"{value.Value:N1} MB";

    private static string FormatMbPerSec(double? value) =>
        value is null ? "n/a" : $"{value.Value:N1} MB/s";

    private static string NormalizePath(string normalizedFullPath, string fullPath)
    {
        if (!string.IsNullOrWhiteSpace(normalizedFullPath))
        {
            return normalizedFullPath;
        }

        if (string.IsNullOrWhiteSpace(fullPath)
            || string.Equals(fullPath, "Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(fullPath.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();
        }
        catch
        {
            return fullPath.Trim().ToLowerInvariant();
        }
    }

    private static FileVersionInfo? TryReadFileVersionInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(path);
        }
        catch
        {
            return null;
        }
    }

    private static string FirstUseful(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }
}

public sealed class ProfileRecommendationViewModel
{
    public ProfileRecommendationViewModel(ProfileRecommendationRecord recommendation)
    {
        Recommendation = recommendation;
        Id = recommendation.Id;
        ExeName = recommendation.ExeName;
        CurrentProfileText = $"{recommendation.CurrentProfileSource}: {recommendation.CurrentProfileName}";
        SuggestedProfileText = recommendation.SuggestedProfileName;
        WarningCountText = recommendation.WarningCount.ToString("N0");
        Reason = recommendation.Reason;
        FirstSeenText = recommendation.FirstSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        LastSeenText = recommendation.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        DisplayText = $"{ExeName} -> {SuggestedProfileText} ({WarningCountText} warnings)";
    }

    public ProfileRecommendationRecord Recommendation { get; }
    public string Id { get; }
    public string ExeName { get; }
    public string CurrentProfileText { get; }
    public string SuggestedProfileText { get; }
    public string WarningCountText { get; }
    public string Reason { get; }
    public string FirstSeenText { get; }
    public string LastSeenText { get; }
    public string DisplayText { get; }

    public override string ToString() => DisplayText;
}

public sealed class DeniedProfileRecommendationViewModel
{
    public DeniedProfileRecommendationViewModel(DeniedProfileRecommendation denied)
    {
        Denied = denied;
        ExeName = denied.ExeName;
        SuggestedProfileText = denied.SuggestedProfileName;
        DeniedAtText = denied.DeniedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Reason = denied.Reason;
        DisplayText = $"{ExeName} denied {SuggestedProfileText} - {DeniedAtText}";
    }

    public DeniedProfileRecommendation Denied { get; }
    public string ExeName { get; }
    public string SuggestedProfileText { get; }
    public string DeniedAtText { get; }
    public string Reason { get; }
    public string DisplayText { get; }

    public override string ToString() => DisplayText;
}

public sealed class GlobalWatchJournalEntryViewModel
{
    public GlobalWatchJournalEntryViewModel(GlobalWatchJournalEntry entry)
    {
        Entry = entry;
        TimestampText = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        IdentityText = string.IsNullOrWhiteSpace(entry.DisplayName)
            ? entry.ExeName
            : entry.DisplayName;
        ModeText = entry.WatchMode;
        HealthText = entry.HealthState;
        ProfileText = entry.ProfileSource;
        Reason = entry.Reason;
        MetricsText = $"CPU {FormatPercent(entry.CpuPercent)} | RAM {FormatMb(entry.MemoryMb)} | Disk R {FormatMbPerSec(entry.DiskReadMbPerSec)} / W {FormatMbPerSec(entry.DiskWriteMbPerSec)}";
        RecommendationText = string.IsNullOrWhiteSpace(entry.RecommendationId)
            ? string.Empty
            : "recommendation linked";
    }

    public GlobalWatchJournalEntry Entry { get; }
    public string TimestampText { get; }
    public string IdentityText { get; }
    public string ModeText { get; }
    public string HealthText { get; }
    public string ProfileText { get; }
    public string Reason { get; }
    public string MetricsText { get; }
    public string RecommendationText { get; }

    private static string FormatPercent(double? value) =>
        value is null ? "n/a" : $"{value.Value:N1}%";

    private static string FormatMb(double? value) =>
        value is null ? "n/a" : $"{value.Value:N1} MB";

    private static string FormatMbPerSec(double? value) =>
        value is null ? "n/a" : $"{value.Value:N1} MB/s";
}

public sealed class GlobalWatchJournalGroupViewModel
{
    public GlobalWatchJournalGroupViewModel(IReadOnlyList<GlobalWatchJournalEntry> entries)
    {
        var ordered = entries
            .OrderByDescending(entry => entry.Timestamp)
            .ToArray();
        Entry = ordered.First();
        Entries = ordered
            .Select(entry => new GlobalWatchJournalEntryViewModel(entry))
            .ToArray();
        ExeName = Entry.ExeName;
        ProcessId = Entry.ProcessId;
        IdentityText = string.IsNullOrWhiteSpace(Entry.DisplayName)
            ? Entry.ExeName
            : Entry.DisplayName;
        LatestText = Entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        CountText = $"{Entries.Count:N0}x";
        ModeText = Entry.WatchMode;
        HealthText = Entry.HealthState;
        ProfileText = Entry.ProfileSource;
        Reason = Entry.Reason;
        MetricsText = Entries[0].MetricsText;
        HasMultipleEntries = Entries.Count > 1;
        ExpandHint = HasMultipleEntries
            ? "Expand to review repeated branches."
            : "Single watch entry.";
        RecommendationText = string.IsNullOrWhiteSpace(Entry.RecommendationId)
            ? "No linked recommendation"
            : "Recommendation linked";
        HeaderText = $"{IdentityText} - {HealthText} - {CountText}";
    }

    public GlobalWatchJournalEntry Entry { get; }
    public IReadOnlyList<GlobalWatchJournalEntryViewModel> Entries { get; }
    public string ExeName { get; }
    public int? ProcessId { get; }
    public string IdentityText { get; }
    public string LatestText { get; }
    public string CountText { get; }
    public string ModeText { get; }
    public string HealthText { get; }
    public string ProfileText { get; }
    public string Reason { get; }
    public string MetricsText { get; }
    public bool HasMultipleEntries { get; }
    public string ExpandHint { get; }
    public string RecommendationText { get; }
    public string HeaderText { get; }

    public override string ToString() => HeaderText;
}

public sealed class SuspiciousWatchItemViewModel
{
    public SuspiciousWatchItemViewModel(SuspiciousWatchItem item)
    {
        Item = item;
        ExeName = item.ExeName;
        PathText = item.NormalizedPath;
        ProductText = string.IsNullOrWhiteSpace(item.ProductName) ? "Unknown product" : item.ProductName;
        CompanyText = string.IsNullOrWhiteSpace(item.CompanyName) ? "Unknown company" : item.CompanyName;
        SignerText = string.IsNullOrWhiteSpace(item.SignerStatus) ? "Unknown signer" : item.SignerStatus;
        MarkedAtText = item.MarkedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        NoteText = string.IsNullOrWhiteSpace(item.Note) ? "No note" : item.Note;
        DisplayText = $"{ExeName} - {CompanyText} - {SignerText}";
    }

    public SuspiciousWatchItem Item { get; }
    public string ExeName { get; }
    public string PathText { get; }
    public string ProductText { get; }
    public string CompanyText { get; }
    public string SignerText { get; }
    public string MarkedAtText { get; }
    public string NoteText { get; }
    public string DisplayText { get; }

    public override string ToString() => DisplayText;
}

public sealed class SuspiciousLaunchEntryViewModel
{
    public SuspiciousLaunchEntryViewModel(SuspiciousLaunchEntry entry)
    {
        Entry = entry;
        TimestampText = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        ExeName = entry.ExeName;
        PathText = entry.NormalizedPath;
        ProductText = string.IsNullOrWhiteSpace(entry.ProductName) ? "Unknown product" : entry.ProductName;
        CompanyText = string.IsNullOrWhiteSpace(entry.CompanyName) ? "Unknown company" : entry.CompanyName;
        SignerText = string.IsNullOrWhiteSpace(entry.SignerStatus) ? "Unknown signer" : entry.SignerStatus;
        WatchModeText = entry.WatchMode;
        ParentText = entry.ParentProcessId is null
            ? "Parent unknown"
            : $"{(string.IsNullOrWhiteSpace(entry.ParentProcessName) ? "Unknown" : entry.ParentProcessName)} ({entry.ParentProcessId})";
        DisplayText = $"{TimestampText} - {ExeName} - {ParentText}";
    }

    public SuspiciousLaunchEntry Entry { get; }
    public string TimestampText { get; }
    public string ExeName { get; }
    public string PathText { get; }
    public string ProductText { get; }
    public string CompanyText { get; }
    public string SignerText { get; }
    public string WatchModeText { get; }
    public string ParentText { get; }
    public string DisplayText { get; }

    public override string ToString() => DisplayText;
}

public sealed class ProcessBanDurationOptionViewModel
{
    public ProcessBanDurationOptionViewModel(string label, TimeSpan? duration)
    {
        Label = label;
        Duration = duration;
    }

    public string Label { get; }
    public TimeSpan? Duration { get; }
    public bool IsPermanent => Duration is null;

    public override string ToString() => Label;
}

public sealed class ProcessBanRuleViewModel
{
    public ProcessBanRuleViewModel(ProcessBanRule rule)
    {
        Rule = rule;
        ExeName = rule.ExeName;
        PathText = rule.NormalizedPath;
        ProductText = string.IsNullOrWhiteSpace(rule.ProductName) ? "Unknown product" : rule.ProductName;
        CompanyText = string.IsNullOrWhiteSpace(rule.CompanyName) ? "Unknown company" : rule.CompanyName;
        SignerText = string.IsNullOrWhiteSpace(rule.SignerStatus) ? "Unknown signer" : rule.SignerStatus;
        CreatedAtText = rule.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        ExpiresText = rule.ExpiresAt is null
            ? "Permanent"
            : rule.ExpiresAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        DurationText = rule.DurationLabel;
        DisplayText = $"{ExeName} - {DurationText} - {ExpiresText}";
    }

    public ProcessBanRule Rule { get; }
    public string ExeName { get; }
    public string PathText { get; }
    public string ProductText { get; }
    public string CompanyText { get; }
    public string SignerText { get; }
    public string CreatedAtText { get; }
    public string ExpiresText { get; }
    public string DurationText { get; }
    public string DisplayText { get; }

    public override string ToString() => DisplayText;
}

public sealed class ProcessBanEventViewModel
{
    public ProcessBanEventViewModel(ProcessBanEvent entry)
    {
        Entry = entry;
        TimestampText = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        ExeName = entry.ExeName;
        PathText = entry.NormalizedPath;
        ActionText = entry.Action;
        DurationText = entry.DurationLabel;
        ProcessText = entry.ProcessId is null
            ? entry.ProcessName ?? "n/a"
            : $"{(string.IsNullOrWhiteSpace(entry.ProcessName) ? entry.ExeName : entry.ProcessName)} ({entry.ProcessId})";
        TerminatedText = entry.TerminatedCount.ToString("N0");
        DetailsText = string.IsNullOrWhiteSpace(entry.Details) ? string.Empty : entry.Details;
        DisplayText = $"{TimestampText} - {ActionText} - {ExeName}";
    }

    public ProcessBanEvent Entry { get; }
    public string TimestampText { get; }
    public string ExeName { get; }
    public string PathText { get; }
    public string ActionText { get; }
    public string DurationText { get; }
    public string ProcessText { get; }
    public string TerminatedText { get; }
    public string DetailsText { get; }
    public string DisplayText { get; }

    public override string ToString() => DisplayText;
}

public sealed class MetricSummaryRowViewModel
{
    public MetricSummaryRowViewModel(MetricSummary summary)
    {
        Label = summary.Label;
        MinText = FormatMetric(summary.Min, summary.Unit);
        AvgText = FormatMetric(summary.Avg, summary.Unit);
        MaxText = FormatMetric(summary.Max, summary.Unit);
        SpikeText = summary.Spikes.ToString("N0");
        BreachText = summary.ThresholdBreaches.ToString("N0");
        SamplesText = summary.Samples.ToString("N0");
        MinLabel = $"Min {MinText}";
        AvgLabel = $"Avg {AvgText}";
        MaxLabel = $"Max {MaxText}";
        SpikeLabel = $"Spikes {SpikeText}";
        BreachLabel = $"Breaches {BreachText}";
        Reliability = FormatReliability(summary.Reliability);
    }

    public string Label { get; }
    public string MinText { get; }
    public string AvgText { get; }
    public string MaxText { get; }
    public string SpikeText { get; }
    public string BreachText { get; }
    public string SamplesText { get; }
    public string MinLabel { get; }
    public string AvgLabel { get; }
    public string MaxLabel { get; }
    public string SpikeLabel { get; }
    public string BreachLabel { get; }
    public string Reliability { get; }

    private static string FormatMetric(double value, string unit) => $"{value:N1} {unit}";

    private static string FormatReliability(MetricReliability reliability) => reliability switch
    {
        MetricReliability.Stable => "Recorded",
        MetricReliability.BestEffort => "Recorded, best effort",
        MetricReliability.Unavailable => "Not supported",
        _ => reliability.ToString()
    };
}

public sealed class EventRowViewModel
{
    public EventRowViewModel(PerformanceEvent performanceEvent)
    {
        Title = performanceEvent.Title;
        TimestampText = performanceEvent.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Kind = FormatKindText(performanceEvent);
        IsGrouped = performanceEvent.GroupedKinds.Count > 1;
        GroupedText = IsGrouped
            ? $"Grouped event: {string.Join(" + ", performanceEvent.GroupedKinds.Select(FormatKind))}"
            : string.Empty;
        Metric = FormatMetricKey(performanceEvent.MetricKey);
        Severity = performanceEvent.Severity.ToString();
        ElapsedText = $"+{TimeSpan.FromMilliseconds(performanceEvent.ElapsedMs):mm\\:ss}";
        ObservedText = FormatObserved(performanceEvent);
        ThresholdText = FormatThreshold(performanceEvent);
        Details = performanceEvent.Details;
        ContextSummaryText = FormatContext(performanceEvent.Context);
        HasContext = performanceEvent.Context is not null;
        ContextCapturedText = performanceEvent.Context is null
            ? string.Empty
            : $"Captured {performanceEvent.Context.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} by {performanceEvent.Context.CaptureProvider ?? "best-effort provider"}";
        ContextNoteText = performanceEvent.Context?.Note ?? "Best-effort process context, not a continuous trace.";
        TopCpuContext = performanceEvent.Context?.TopProcessesByCpu
            .Take(3)
            .Select(process => new ContextProcessRowViewModel(process))
            .ToArray()
            ?? [];
        TopRamContext = performanceEvent.Context?.TopProcessesByMemory
            .Take(3)
            .Select(process => new ContextProcessRowViewModel(process))
            .ToArray()
            ?? [];
        TopDiskContext = performanceEvent.Context?.TopProcessesByDisk
            .Take(3)
            .Select(process => new ContextProcessRowViewModel(process))
            .ToArray()
            ?? [];
        NewProcessContext = performanceEvent.Context?.NewProcessNames.Take(6).ToArray() ?? [];
        HasNewProcessContext = NewProcessContext.Count > 0;
    }

    public string Title { get; }
    public string TimestampText { get; }
    public string Kind { get; }
    public bool IsGrouped { get; }
    public string GroupedText { get; }
    public string Metric { get; }
    public string Severity { get; }
    public string ElapsedText { get; }
    public string ObservedText { get; }
    public string ThresholdText { get; }
    public string Details { get; }
    public string ContextSummaryText { get; }
    public bool HasContext { get; }
    public string ContextCapturedText { get; }
    public string ContextNoteText { get; }
    public IReadOnlyList<ContextProcessRowViewModel> TopCpuContext { get; }
    public IReadOnlyList<ContextProcessRowViewModel> TopRamContext { get; }
    public IReadOnlyList<ContextProcessRowViewModel> TopDiskContext { get; }
    public IReadOnlyList<string> NewProcessContext { get; }
    public bool HasNewProcessContext { get; }

    private static string FormatKindText(PerformanceEvent performanceEvent)
    {
        if (performanceEvent.GroupedKinds.Count > 1)
        {
            return $"Grouped: {string.Join(" + ", performanceEvent.GroupedKinds.Select(FormatKind))}";
        }

        return FormatKind(performanceEvent.Kind);
    }

    private static string FormatKind(EventKind kind) => kind switch
    {
        EventKind.ThresholdBreach => "Threshold breach",
        EventKind.Spike => "Spike",
        EventKind.HangSuspected => "Hang suspected",
        EventKind.LongStartup => "Long startup",
        EventKind.ExternalExit => "External close",
        EventKind.UnexpectedExit => "Unexpected exit",
        EventKind.CrashLikeExit => "Crash-like exit",
        _ => kind.ToString()
    };

    private static string FormatMetricKey(MetricKey? key) => key switch
    {
        MetricKey.CpuPercent => "CPU",
        MetricKey.MemoryMb => "RAM",
        MetricKey.GpuPercent => "GPU",
        MetricKey.DiskReadMbPerSec => "Disk read",
        MetricKey.DiskWriteMbPerSec => "Disk write",
        MetricKey.TemperatureC => "Temperature",
        _ => "Session"
    };

    private static string FormatObserved(PerformanceEvent performanceEvent)
    {
        if (performanceEvent.ObservedValue is null)
        {
            return performanceEvent.Severity.ToString();
        }

        var unit = performanceEvent.MetricKey switch
        {
            MetricKey.CpuPercent => "%",
            MetricKey.MemoryMb => "MB",
            MetricKey.GpuPercent => "%",
            MetricKey.DiskReadMbPerSec => "MB/s",
            MetricKey.DiskWriteMbPerSec => "MB/s",
            MetricKey.TemperatureC => "C",
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(unit)
            ? $"{performanceEvent.ObservedValue.Value:N1}"
            : $"{performanceEvent.ObservedValue.Value:N1} {unit}";
    }

    private static string FormatThreshold(PerformanceEvent performanceEvent)
    {
        if (performanceEvent.ThresholdValue is null)
        {
            return string.Empty;
        }

        var unit = performanceEvent.MetricKey switch
        {
            MetricKey.CpuPercent => "%",
            MetricKey.MemoryMb => "MB",
            MetricKey.GpuPercent => "%",
            MetricKey.DiskReadMbPerSec => "MB/s",
            MetricKey.DiskWriteMbPerSec => "MB/s",
            MetricKey.TemperatureC => "C",
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(unit)
            ? $"Limit {performanceEvent.ThresholdValue.Value:N1}"
            : $"Limit {performanceEvent.ThresholdValue.Value:N1} {unit}";
    }

    private static string FormatContext(SpikeContextSnapshot? context)
    {
        if (context is null)
        {
            return string.Empty;
        }

        var lines = new List<string>
        {
            "Context around event (best effort, not a full system trace):"
        };

        AddTopProcesses(lines, "CPU", context.TopProcessesByCpu, process =>
            process.CpuPercent is null ? null : $"{process.Name} ({process.ProcessId}) {process.CpuPercent.Value:N1}%");
        AddTopProcesses(lines, "RAM", context.TopProcessesByMemory, process =>
            process.MemoryMb is null ? null : $"{process.Name} ({process.ProcessId}) {process.MemoryMb.Value:N0} MB");
        AddTopProcesses(lines, "Disk", context.TopProcessesByDisk, process =>
        {
            var read = process.DiskReadMbPerSec ?? 0;
            var write = process.DiskWriteMbPerSec ?? 0;
            if (read <= 0 && write <= 0)
            {
                return null;
            }

            return $"{process.Name} ({process.ProcessId}) R {read:N1} / W {write:N1} MB/s";
        });

        if (context.NewProcessNames.Count > 0)
        {
            lines.Add($"New near event: {string.Join(", ", context.NewProcessNames.Take(4))}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AddTopProcesses(
        List<string> lines,
        string label,
        IReadOnlyList<ContextProcessSnapshot> processes,
        Func<ContextProcessSnapshot, string?> formatter)
    {
        var formatted = processes
            .Select(formatter)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(3)
            .ToArray();

        if (formatted.Length > 0)
        {
            lines.Add($"{label}: {string.Join("; ", formatted)}");
        }
    }
}

public sealed class ContextProcessRowViewModel
{
    public ContextProcessRowViewModel(ContextProcessSnapshot process)
    {
        Name = process.Name;
        ProcessIdText = process.ProcessId.ToString();
        CpuText = process.CpuPercent is null ? "CPU n/a" : $"CPU {process.CpuPercent.Value:N1}%";
        RamText = process.MemoryMb is null ? "RAM n/a" : $"RAM {process.MemoryMb.Value:N0} MB";
        var read = process.DiskReadMbPerSec ?? 0;
        var write = process.DiskWriteMbPerSec ?? 0;
        DiskText = $"R {read:N1} / W {write:N1} MB/s";
        SummaryText = $"{Name} ({ProcessIdText}) - {CpuText} - {RamText} - {DiskText}";
    }

    public string Name { get; }
    public string ProcessIdText { get; }
    public string CpuText { get; }
    public string RamText { get; }
    public string DiskText { get; }
    public string SummaryText { get; }
}

public sealed class SessionDetailFactViewModel
{
    public SessionDetailFactViewModel(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public string Value { get; }
}

public sealed class ComparisonMetricRowViewModel
{
    public ComparisonMetricRowViewModel(MetricComparison comparison)
    {
        Metric = comparison.Label;
        LeftAvg = comparison.Left is null ? "n/a" : FormatMetric(comparison.Left.Avg, comparison.Unit);
        RightAvg = comparison.Right is null ? "n/a" : FormatMetric(comparison.Right.Avg, comparison.Unit);
        Delta = comparison.AvgDeltaPercent is null ? "n/a" : $"{comparison.AvgDeltaPercent.Value:+0.0;-0.0;0.0}%";
        MaxDelta = comparison.MaxDelta is null ? "n/a" : FormatMetric(comparison.MaxDelta.Value, comparison.Unit);
        Winner = comparison.Winner;
    }

    public string Metric { get; }
    public string LeftAvg { get; }
    public string RightAvg { get; }
    public string Delta { get; }
    public string MaxDelta { get; }
    public string Winner { get; }

    private static string FormatMetric(double value, string unit) => $"{value:N1} {unit}";
}

public sealed class UnavailableComparisonMetricViewModel
{
    public UnavailableComparisonMetricViewModel(MetricComparison comparison)
    {
        Metric = comparison.Label;
        Reason = "not recorded in one or both sessions";
        DisplayText = $"{Metric} - {Reason}";
    }

    public string Metric { get; }
    public string Reason { get; }
    public string DisplayText { get; }
}

public static class MetricDisplayFactory
{
    public static IReadOnlyList<MetricValueViewModel> CreateLatestMetricRows(SessionRecord? session)
    {
        var latest = session?.Samples.LastOrDefault();
        if (latest is null)
        {
            return [];
        }

        return MetricCatalog.All
            .Where(metric => metric.Key != MetricKey.TemperatureC)
            .Select(metric =>
            {
                var hasValue = latest.Values.TryGetValue(metric.Key, out var value);
                var reliability = latest.SourceReliability.TryGetValue(metric.Key, out var sourceReliability)
                    ? sourceReliability
                    : session!.Capabilities.For(metric.Key);

                if (!hasValue && reliability == MetricReliability.Unavailable)
                {
                    return null;
                }

                return new MetricValueViewModel
                {
                    Label = metric.Label,
                    Value = hasValue ? $"{value:N1} {metric.Unit}" : "n/a",
                    Reliability = FormatReliability(reliability)
                };
            })
            .Where(metric => metric is not null)
            .Cast<MetricValueViewModel>()
            .ToArray();
    }

    public static IReadOnlyList<MetricValueViewModel> CreateMetricRows(
        MetricSample sample,
        MetricCapabilities capabilities)
    {
        return MetricCatalog.All
            .Where(metric => metric.Key is MetricKey.CpuPercent
                or MetricKey.MemoryMb
                or MetricKey.DiskReadMbPerSec
                or MetricKey.DiskWriteMbPerSec)
            .Select(metric =>
            {
                var hasValue = sample.Values.TryGetValue(metric.Key, out var value);
                var reliability = sample.SourceReliability.TryGetValue(metric.Key, out var sourceReliability)
                    ? sourceReliability
                    : capabilities.For(metric.Key);

                if (!hasValue && reliability == MetricReliability.Unavailable)
                {
                    return null;
                }

                return new MetricValueViewModel
                {
                    Label = metric.Label,
                    Value = hasValue ? $"{value:N1} {metric.Unit}" : "n/a",
                    Reliability = FormatReliability(reliability)
                };
            })
            .Where(metric => metric is not null)
            .Cast<MetricValueViewModel>()
            .ToArray();
    }

    private static string FormatReliability(MetricReliability reliability) => reliability switch
    {
        MetricReliability.Stable => "Recorded",
        MetricReliability.BestEffort => "Recorded, best effort",
        MetricReliability.Unavailable => "Not supported",
        _ => reliability.ToString()
    };
}
