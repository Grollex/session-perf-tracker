namespace SessionPerfTracker.Domain.Models;

public sealed record CpuRamThresholdSettings
{
    public double CpuThresholdPercent { get; init; } = 80;
    public double RamThresholdMb { get; init; } = 4096;
    public double DiskReadThresholdMbPerSec { get; init; } = 180;
    public double DiskWriteThresholdMbPerSec { get; init; } = 120;
    public string SelectedProfileId { get; init; } = ThresholdProfileDefaults.BrowsersChatsId;
    public List<ThresholdProfile> Profiles { get; init; } = ThresholdProfileDefaults.CreateProfiles();
    public Dictionary<string, string> AppProfileAssignments { get; init; } = ThresholdProfileDefaults.CreateDefaultAssignments();
    public AntiNoiseSettings AntiNoise { get; init; } = new();
    public CaptureSettings Capture { get; init; } = new();
    public RetentionSettings Retention { get; init; } = new();
    public ExportSettings Export { get; init; } = new();
    public ProfileRecommendationSettings Recommendations { get; init; } = new();
    public GlobalWatchJournalSettings WatchJournal { get; init; } = new();
    public SuspiciousWatchlistSettings SuspiciousWatchlist { get; init; } = new();
    public ProcessBanSettings ProcessBans { get; init; } = new();
    public AppUpdateSettings Updates { get; init; } = new();
    public AppBehaviorSettings Behavior { get; init; } = new();

    public ThresholdLimitValues ToGlobalLimits() => new()
    {
        CpuThresholdPercent = CpuThresholdPercent,
        RamThresholdMb = RamThresholdMb,
        DiskReadThresholdMbPerSec = DiskReadThresholdMbPerSec,
        DiskWriteThresholdMbPerSec = DiskWriteThresholdMbPerSec
    };
}

public sealed record ThresholdProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public ThresholdLimitValues Limits { get; init; } = new();
    public bool IsPreset { get; init; } = true;
}

public sealed record ThresholdLimitValues
{
    public double CpuThresholdPercent { get; init; } = 80;
    public double RamThresholdMb { get; init; } = 4096;
    public double DiskReadThresholdMbPerSec { get; init; } = 180;
    public double DiskWriteThresholdMbPerSec { get; init; } = 120;
}

public sealed record AntiNoiseSettings
{
    public int StartupGraceSeconds { get; init; } = 15;
    public int EventCooldownSeconds { get; init; } = 30;
    public int GroupingWindowSeconds { get; init; } = 2;
    public int SnapshotSuppressionSeconds { get; init; } = 3;
}

public sealed record CaptureSettings
{
    public bool CaptureCpu { get; init; } = true;
    public bool CaptureRam { get; init; } = true;
    public bool CaptureDiskRead { get; init; } = true;
    public bool CaptureDiskWrite { get; init; } = true;
}

public sealed record RetentionSettings
{
    public int? RetentionDays { get; init; } = 30;
}

public sealed record ExportSettings
{
    public string? ExportDirectory { get; init; }
}

public sealed record AppUpdateSettings
{
    public const string DefaultManifestUrl = "https://github.com/Grollex/session-perf-tracker/releases/latest/download/version.json";

    public bool AutomaticallyCheckForUpdates { get; init; }
    public string? ManifestUrl { get; init; } = DefaultManifestUrl;
    public DateTimeOffset? LastCheckedAt { get; init; }
}

public sealed record AppBehaviorSettings
{
    public bool MinimizeToTrayOnClose { get; init; } = true;
}

public sealed record UpdateManifest
{
    public required string Version { get; init; }
    public required string InstallerUrl { get; init; }
    public string? ReleaseNotes { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Sha256 { get; init; }
}

public sealed record UpdateCheckResult
{
    public required string CurrentVersion { get; init; }
    public string? LatestVersion { get; init; }
    public bool IsUpdateAvailable { get; init; }
    public UpdateManifest? Manifest { get; init; }
    public required string Status { get; init; }
}

public sealed record ProfileRecommendationSettings
{
    public int TriggerWarningCount { get; init; } = 5;
    public int TriggerWindowMinutes { get; init; } = 10;
    public List<ProfileRecommendationRecord> Active { get; init; } = [];
    public List<DeniedProfileRecommendation> Denied { get; init; } = [];
    public List<ProfileRecommendationHistoryEntry> History { get; init; } = [];
}

public sealed record GlobalWatchJournalSettings
{
    public int MaxEntries { get; init; } = 500;
    public List<GlobalWatchJournalEntry> Entries { get; init; } = [];
}

public sealed record GlobalWatchJournalEntry
{
    public required string Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public required string ExeName { get; init; }
    public string? DisplayName { get; init; }
    public int? ProcessId { get; init; }
    public required string WatchMode { get; init; }
    public required string ProfileSource { get; init; }
    public required string HealthState { get; init; }
    public required string Reason { get; init; }
    public double? CpuPercent { get; init; }
    public double? MemoryMb { get; init; }
    public double? DiskReadMbPerSec { get; init; }
    public double? DiskWriteMbPerSec { get; init; }
    public string? RecommendationId { get; init; }
}

public sealed record SuspiciousWatchlistSettings
{
    public int MaxLaunchHistory { get; init; } = 500;
    public List<SuspiciousWatchItem> Items { get; init; } = [];
    public List<SuspiciousLaunchEntry> LaunchHistory { get; init; } = [];
}

public sealed record SuspiciousWatchItem
{
    public required string NormalizedPath { get; init; }
    public required string ExeName { get; init; }
    public string? ProductName { get; init; }
    public string? CompanyName { get; init; }
    public string? SignerStatus { get; init; }
    public DateTimeOffset MarkedAt { get; init; }
    public string? Note { get; init; }
}

public sealed record SuspiciousLaunchEntry
{
    public required string Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public required string NormalizedPath { get; init; }
    public required string ExeName { get; init; }
    public string? ProductName { get; init; }
    public string? CompanyName { get; init; }
    public string? SignerStatus { get; init; }
    public required string WatchMode { get; init; }
    public int? ParentProcessId { get; init; }
    public string? ParentProcessName { get; init; }
}

public sealed record ProcessBanSettings
{
    public int MaxHistory { get; init; } = 500;
    public List<ProcessBanRule> Active { get; init; } = [];
    public List<ProcessBanEvent> History { get; init; } = [];
}

public sealed record ProcessBanRule
{
    public required string Id { get; init; }
    public required string NormalizedPath { get; init; }
    public required string ExeName { get; init; }
    public string? ProductName { get; init; }
    public string? CompanyName { get; init; }
    public string? SignerStatus { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public required string DurationLabel { get; init; }
    public string? Reason { get; init; }
}

public sealed record ProcessBanEvent
{
    public required string Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public required string NormalizedPath { get; init; }
    public required string ExeName { get; init; }
    public string? ProductName { get; init; }
    public string? CompanyName { get; init; }
    public string? SignerStatus { get; init; }
    public required string Action { get; init; }
    public required string DurationLabel { get; init; }
    public int? ProcessId { get; init; }
    public string? ProcessName { get; init; }
    public int TerminatedCount { get; init; }
    public string? Details { get; init; }
}

public sealed record ProfileRecommendationRecord
{
    public required string Id { get; init; }
    public required string ExeName { get; init; }
    public required string CurrentProfileId { get; init; }
    public required string CurrentProfileName { get; init; }
    public required string CurrentProfileSource { get; init; }
    public required string SuggestedProfileId { get; init; }
    public required string SuggestedProfileName { get; init; }
    public int WarningCount { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; init; }
}

public sealed record DeniedProfileRecommendation
{
    public required string ExeName { get; init; }
    public required string SuggestedProfileId { get; init; }
    public required string SuggestedProfileName { get; init; }
    public DateTimeOffset DeniedAt { get; init; }
    public required string Reason { get; init; }
}

public sealed record ProfileRecommendationHistoryEntry
{
    public required string Kind { get; init; }
    public required string ExeName { get; init; }
    public required string SuggestedProfileId { get; init; }
    public required string SuggestedProfileName { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public required string Reason { get; init; }
}

public static class ThresholdProfileDefaults
{
    public const string LightBackgroundId = "light-background";
    public const string BrowsersChatsId = "browsers-chats";
    public const string GamesId = "games";
    public const string HardcoreId = "hardcore";
    public const string CustomId = "custom";

    public static CpuRamThresholdSettings CreateSettings() => new()
    {
        Profiles = CreateProfiles(),
        AppProfileAssignments = CreateDefaultAssignments()
    };

    public static List<ThresholdProfile> CreateProfiles() =>
    [
        CreateProfile(LightBackgroundId, "Light background", 60, 2500, 60, 40),
        CreateProfile(BrowsersChatsId, "Browsers / chats", 75, 4000, 180, 120),
        CreateProfile(GamesId, "Games", 85, 7000, 400, 250),
        CreateProfile(HardcoreId, "Hardcore", 90, 10000, 700, 400),
        CreateProfile(CustomId, "Custom", 80, 4096, 180, 120, isPreset: false)
    ];

    public static Dictionary<string, string> CreateDefaultAssignments() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["opera.exe"] = BrowsersChatsId,
            ["discord.exe"] = BrowsersChatsId,
            ["telegram.exe"] = BrowsersChatsId,
            ["HorizonForbiddenWest.exe"] = GamesId
        };

    private static ThresholdProfile CreateProfile(
        string id,
        string name,
        double cpu,
        double ram,
        double diskRead,
        double diskWrite,
        bool isPreset = true) => new()
        {
            Id = id,
            Name = name,
            IsPreset = isPreset,
            Limits = new ThresholdLimitValues
            {
                CpuThresholdPercent = cpu,
                RamThresholdMb = ram,
                DiskReadThresholdMbPerSec = diskRead,
                DiskWriteThresholdMbPerSec = diskWrite
            }
        };
}
