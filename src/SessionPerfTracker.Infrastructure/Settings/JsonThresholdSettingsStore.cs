using System.Text.Json;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Settings;

public sealed class JsonThresholdSettingsStore : IThresholdSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);

    public JsonThresholdSettingsStore(string filePath)
    {
        _filePath = filePath;
    }

    public CpuRamThresholdSettings Current { get; private set; } = ThresholdProfileDefaults.CreateSettings();

    public async Task<CpuRamThresholdSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _settingsLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            if (!File.Exists(_filePath))
            {
                Current = Normalize(Current);
                await WriteCoreAsync(Current, cancellationToken);
                return Current;
            }

            Current = await ReadCoreAsync(cancellationToken);
            return Current;
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task SaveAsync(CpuRamThresholdSettings settings, CancellationToken cancellationToken = default)
    {
        await _settingsLock.WaitAsync(cancellationToken);
        try
        {
            Current = Normalize(settings);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await WriteCoreAsync(Current, cancellationToken);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task<CpuRamThresholdSettings> UpdateAsync(
        Func<CpuRamThresholdSettings, CpuRamThresholdSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _settingsLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var current = File.Exists(_filePath)
                ? await ReadCoreAsync(cancellationToken)
                : Normalize(Current);
            var updated = update(current);
            if (ReferenceEquals(updated, current))
            {
                Current = current;
                return Current;
            }

            Current = Normalize(updated);
            await WriteCoreAsync(Current, cancellationToken);
            return Current;
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private async Task<CpuRamThresholdSettings> ReadCoreAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(_filePath);
        return Normalize(await JsonSerializer.DeserializeAsync<CpuRamThresholdSettings>(
            stream,
            SerializerOptions,
            cancellationToken) ?? new CpuRamThresholdSettings());
    }

    private async Task WriteCoreAsync(CpuRamThresholdSettings settings, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
    }

    public ThresholdLimitValues ResolveThresholds(TargetDescriptor target)
    {
        var exeName = GetExecutableName(target);
        if (!string.IsNullOrWhiteSpace(exeName)
            && Current.AppProfileAssignments.TryGetValue(NormalizeExeName(exeName), out var profileId)
            && Current.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)) is { } profile)
        {
            return profile.Limits;
        }

        return Current.ToGlobalLimits();
    }

    internal static CpuRamThresholdSettings Normalize(CpuRamThresholdSettings settings)
    {
        var normalizedProfiles = MergeProfiles(settings.Profiles).ToArray();
        var selectedProfileId = normalizedProfiles.Any(profile => string.Equals(profile.Id, settings.SelectedProfileId, StringComparison.OrdinalIgnoreCase))
            ? settings.SelectedProfileId
            : ThresholdProfileDefaults.BrowsersChatsId;

        return settings with
        {
            CpuThresholdPercent = Math.Clamp(settings.CpuThresholdPercent, 1, 100),
            RamThresholdMb = Math.Clamp(settings.RamThresholdMb, 1, 1_048_576),
            DiskReadThresholdMbPerSec = Math.Clamp(settings.DiskReadThresholdMbPerSec, 0.1, 1_048_576),
            DiskWriteThresholdMbPerSec = Math.Clamp(settings.DiskWriteThresholdMbPerSec, 0.1, 1_048_576),
            GpuThresholdPercent = Math.Clamp(
                double.IsFinite(settings.GpuThresholdPercent) && settings.GpuThresholdPercent > 0
                    ? settings.GpuThresholdPercent
                    : ThresholdProfileDefaults.DefaultGpuThresholdPercent,
                1,
                100),
            SelectedProfileId = selectedProfileId,
            Profiles = normalizedProfiles.ToList(),
            AppProfileAssignments = NormalizeAssignments(settings.AppProfileAssignments),
            AntiNoise = NormalizeAntiNoise(settings.AntiNoise),
            Capture = NormalizeCapture(settings.Capture),
            Retention = NormalizeRetention(settings.Retention),
            Export = NormalizeExport(settings.Export),
            Recommendations = NormalizeRecommendations(settings.Recommendations),
            WatchJournal = NormalizeWatchJournal(settings.WatchJournal),
            SuspiciousWatchlist = NormalizeSuspiciousWatchlist(settings.SuspiciousWatchlist),
            ProcessBans = NormalizeProcessBans(settings.ProcessBans),
            Updates = NormalizeUpdates(settings.Updates),
            Behavior = NormalizeBehavior(settings.Behavior),
            Language = NormalizeLanguage(settings.Language),
            Feedback = NormalizeFeedback(settings.Feedback)
        };
    }

    private static IEnumerable<ThresholdProfile> MergeProfiles(IReadOnlyList<ThresholdProfile>? profiles)
    {
        var defaults = ThresholdProfileDefaults.CreateProfiles()
            .ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles ?? [])
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                continue;
            }

            var fallback = defaults.GetValueOrDefault(profile.Id);
            defaults[profile.Id] = new ThresholdProfile
            {
                Id = profile.Id,
                Name = string.IsNullOrWhiteSpace(profile.Name) ? fallback?.Name ?? profile.Id : profile.Name,
                IsPreset = fallback?.IsPreset ?? profile.IsPreset,
                Limits = NormalizeLimits(
                    profile.Limits,
                    fallback?.Limits.GpuThresholdPercent ?? ThresholdProfileDefaults.DefaultGpuThresholdPercent)
            };
        }

        return defaults.Values.OrderBy(profile => GetProfileOrder(profile.Id));
    }

    private static Dictionary<string, string> NormalizeAssignments(IReadOnlyDictionary<string, string>? assignments)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assignment in assignments ?? ThresholdProfileDefaults.CreateDefaultAssignments())
        {
            var exeName = NormalizeExeName(assignment.Key);
            if (!string.IsNullOrWhiteSpace(exeName) && !string.IsNullOrWhiteSpace(assignment.Value))
            {
                normalized[exeName] = assignment.Value;
            }
        }

        return normalized;
    }

    internal static ThresholdLimitValues NormalizeLimits(ThresholdLimitValues? limits, double defaultGpuThresholdPercent)
    {
        limits ??= new ThresholdLimitValues();
        var gpu = limits.GpuThresholdPercent is double value && double.IsFinite(value) && value > 0
            ? value
            : defaultGpuThresholdPercent;
        return limits with
        {
            CpuThresholdPercent = Math.Clamp(limits.CpuThresholdPercent, 1, 100),
            RamThresholdMb = Math.Clamp(limits.RamThresholdMb, 1, 1_048_576),
            DiskReadThresholdMbPerSec = Math.Clamp(limits.DiskReadThresholdMbPerSec, 0.1, 1_048_576),
            DiskWriteThresholdMbPerSec = Math.Clamp(limits.DiskWriteThresholdMbPerSec, 0.1, 1_048_576),
            GpuThresholdPercent = Math.Clamp(gpu, 1, 100)
        };
    }

    private static AntiNoiseSettings NormalizeAntiNoise(AntiNoiseSettings? settings)
    {
        settings ??= new AntiNoiseSettings();
        return settings with
        {
            StartupGraceSeconds = Math.Clamp(settings.StartupGraceSeconds, 0, 300),
            EventCooldownSeconds = Math.Clamp(settings.EventCooldownSeconds, 0, 3600),
            GroupingWindowSeconds = Math.Clamp(settings.GroupingWindowSeconds, 0, 60),
            SnapshotSuppressionSeconds = Math.Clamp(settings.SnapshotSuppressionSeconds, 0, 60)
        };
    }

    private static CaptureSettings NormalizeCapture(CaptureSettings? settings)
    {
        settings ??= new CaptureSettings();
        return settings;
    }

    private static RetentionSettings NormalizeRetention(RetentionSettings? settings)
    {
        settings ??= new RetentionSettings();
        return settings with
        {
            RetentionDays = settings.RetentionDays is null
                ? null
                : Math.Clamp(settings.RetentionDays.Value, 1, 3650)
        };
    }

    private static ExportSettings NormalizeExport(ExportSettings? settings)
    {
        settings ??= new ExportSettings();
        var directory = string.IsNullOrWhiteSpace(settings.ExportDirectory)
            ? null
            : settings.ExportDirectory.Trim();
        return settings with { ExportDirectory = directory };
    }

    private static AppUpdateSettings NormalizeUpdates(AppUpdateSettings? settings)
    {
        settings ??= new AppUpdateSettings();
        var manifestUrl = string.IsNullOrWhiteSpace(settings.ManifestUrl)
            ? AppUpdateSettings.DefaultManifestUrl
            : settings.ManifestUrl.Trim();
        var skippedVersion = string.IsNullOrWhiteSpace(settings.SkippedVersion)
            ? null
            : settings.SkippedVersion.Trim().TrimStart('v', 'V');
        return settings with
        {
            AutomaticallyCheckForUpdates = settings.AutomaticallyCheckForUpdates,
            AutomaticallyInstallUpdatesOnStartup = settings.AutomaticallyInstallUpdatesOnStartup,
            ManifestUrl = manifestUrl,
            SkippedVersion = skippedVersion
        };
    }

    private static AppBehaviorSettings NormalizeBehavior(AppBehaviorSettings? settings)
    {
        settings ??= new AppBehaviorSettings();
        return settings;
    }

    private static AppLanguageSettings NormalizeLanguage(AppLanguageSettings? settings)
    {
        settings ??= new AppLanguageSettings();
        var languageCode = string.IsNullOrWhiteSpace(settings.LanguageCode)
            ? AppLanguageSettings.DefaultLanguageCode
            : settings.LanguageCode.Trim();

        languageCode = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase)
            ? "en-US"
            : languageCode.Equals("ru", StringComparison.OrdinalIgnoreCase)
                ? "ru-RU"
                : languageCode;

        return settings with
        {
            LanguageCode = languageCode.Equals("ru-RU", StringComparison.OrdinalIgnoreCase)
                ? "ru-RU"
                : AppLanguageSettings.DefaultLanguageCode
        };
    }

    private static FeedbackSettings NormalizeFeedback(FeedbackSettings? settings)
    {
        settings ??= new FeedbackSettings();
        return settings with
        {
            MaxLocalReports = Math.Clamp(settings.MaxLocalReports, 1, 500),
            MaxLocalReportAgeDays = Math.Clamp(settings.MaxLocalReportAgeDays, 1, 3650)
        };
    }

    private static ProfileRecommendationSettings NormalizeRecommendations(ProfileRecommendationSettings? settings)
    {
        settings ??= new ProfileRecommendationSettings();
        return settings with
        {
            TriggerWarningCount = Math.Clamp(settings.TriggerWarningCount, 1, 100),
            TriggerWindowMinutes = Math.Clamp(settings.TriggerWindowMinutes, 1, 1440),
            Active = settings.Active
                .Where(item => !string.IsNullOrWhiteSpace(item.ExeName)
                    && !string.IsNullOrWhiteSpace(item.SuggestedProfileId))
                .Select(item => item with
                {
                    ExeName = NormalizeExeName(item.ExeName),
                    FullPath = string.IsNullOrWhiteSpace(item.FullPath) ? null : NormalizeFullPath(item.FullPath),
                    WarningCount = Math.Max(0, item.WarningCount)
                })
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.LastSeen).First())
                .OrderByDescending(item => item.LastSeen)
                .ToList(),
            Denied = settings.Denied
                .Where(item => !string.IsNullOrWhiteSpace(item.ExeName)
                    && !string.IsNullOrWhiteSpace(item.SuggestedProfileId))
                .Select(item => item with { ExeName = NormalizeExeName(item.ExeName) })
                .GroupBy(item => $"{item.ExeName}|{item.SuggestedProfileId}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.DeniedAt).First())
                .OrderByDescending(item => item.DeniedAt)
                .ToList(),
            History = settings.History
                .Where(item => !string.IsNullOrWhiteSpace(item.ExeName)
                    && !string.IsNullOrWhiteSpace(item.Kind))
                .Select(item => item with
                {
                    ExeName = NormalizeExeName(item.ExeName),
                    FullPath = string.IsNullOrWhiteSpace(item.FullPath) ? null : NormalizeFullPath(item.FullPath)
                })
                .OrderByDescending(item => item.Timestamp)
                .Take(500)
                .ToList()
        };
    }

    private static GlobalWatchJournalSettings NormalizeWatchJournal(GlobalWatchJournalSettings? settings)
    {
        settings ??= new GlobalWatchJournalSettings();
        var maxEntries = Math.Clamp(settings.MaxEntries, 50, 5000);
        return settings with
        {
            MaxEntries = maxEntries,
            Entries = settings.Entries
                .Where(item => !string.IsNullOrWhiteSpace(item.ExeName)
                    && !string.IsNullOrWhiteSpace(item.HealthState))
                .Select(item => item with
                {
                    ExeName = NormalizeExeName(item.ExeName),
                    FullPath = string.IsNullOrWhiteSpace(item.FullPath) ? null : NormalizeFullPath(item.FullPath)
                })
                .OrderByDescending(item => item.Timestamp)
                .Take(maxEntries)
                .ToList()
        };
    }

    private static SuspiciousWatchlistSettings NormalizeSuspiciousWatchlist(SuspiciousWatchlistSettings? settings)
    {
        settings ??= new SuspiciousWatchlistSettings();
        var maxLaunchHistory = Math.Clamp(settings.MaxLaunchHistory, 50, 5000);
        return settings with
        {
            MaxLaunchHistory = maxLaunchHistory,
            Items = settings.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.NormalizedPath))
                .Select(item =>
                {
                    var normalizedPath = NormalizeFullPath(item.NormalizedPath);
                    return item with
                    {
                        NormalizedPath = normalizedPath,
                        ExeName = NormalizeExeName(string.IsNullOrWhiteSpace(item.ExeName)
                            ? Path.GetFileName(item.NormalizedPath)
                            : item.ExeName),
                        AlternatePaths = NormalizeAlternatePaths(item.AlternatePaths, normalizedPath),
                        PathHistory = NormalizePathHistory(item.PathHistory)
                    };
                })
                .GroupBy(item => item.NormalizedPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.MarkedAt).First())
                .OrderByDescending(item => item.MarkedAt)
                .ToList(),
            LaunchHistory = settings.LaunchHistory
                .Where(item => !string.IsNullOrWhiteSpace(item.NormalizedPath))
                .Select(item => item with
                {
                    NormalizedPath = NormalizeFullPath(item.NormalizedPath),
                    ExeName = NormalizeExeName(string.IsNullOrWhiteSpace(item.ExeName)
                        ? Path.GetFileName(item.NormalizedPath)
                        : item.ExeName)
                })
                .OrderByDescending(item => item.Timestamp)
                .Take(maxLaunchHistory)
                .ToList()
        };
    }

    private static ProcessBanSettings NormalizeProcessBans(ProcessBanSettings? settings)
    {
        settings ??= new ProcessBanSettings();
        var maxHistory = Math.Clamp(settings.MaxHistory, 50, 5000);
        var now = DateTimeOffset.UtcNow;
        return settings with
        {
            MaxHistory = maxHistory,
            Active = settings.Active
                .Where(item => !string.IsNullOrWhiteSpace(item.NormalizedPath)
                    && (item.ExpiresAt is null || item.ExpiresAt > now))
                .Select(item =>
                {
                    var normalizedPath = NormalizeFullPath(item.NormalizedPath);
                    return item with
                    {
                        Id = string.IsNullOrWhiteSpace(item.Id) ? $"ban_{Guid.NewGuid():N}" : item.Id,
                        NormalizedPath = normalizedPath,
                        ExeName = NormalizeExeName(string.IsNullOrWhiteSpace(item.ExeName)
                            ? Path.GetFileName(item.NormalizedPath)
                            : item.ExeName),
                        DurationLabel = string.IsNullOrWhiteSpace(item.DurationLabel)
                            ? item.ExpiresAt is null ? "Permanent" : "Timed"
                            : item.DurationLabel,
                        AlternatePaths = NormalizeAlternatePaths(item.AlternatePaths, normalizedPath),
                        PathHistory = NormalizePathHistory(item.PathHistory)
                    };
                })
                .GroupBy(item => item.NormalizedPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.CreatedAt).First())
                .OrderByDescending(item => item.CreatedAt)
                .ToList(),
            History = settings.History
                .Where(item => !string.IsNullOrWhiteSpace(item.NormalizedPath)
                    && !string.IsNullOrWhiteSpace(item.Action))
                .Select(item => item with
                {
                    Id = string.IsNullOrWhiteSpace(item.Id) ? $"ban_event_{Guid.NewGuid():N}" : item.Id,
                    NormalizedPath = NormalizeFullPath(item.NormalizedPath),
                    ExeName = NormalizeExeName(string.IsNullOrWhiteSpace(item.ExeName)
                        ? Path.GetFileName(item.NormalizedPath)
                        : item.ExeName),
                    DurationLabel = string.IsNullOrWhiteSpace(item.DurationLabel) ? "n/a" : item.DurationLabel
                })
                .OrderByDescending(item => item.Timestamp)
                .Take(maxHistory)
                .ToList()
        };
    }

    private static List<string> NormalizeAlternatePaths(IEnumerable<string>? paths, string primaryPath) =>
        (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeFullPath)
            .Where(path => !string.Equals(path, primaryPath, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

    private static List<TrackedPathChange> NormalizePathHistory(IEnumerable<TrackedPathChange>? history) =>
        (history ?? [])
            .Where(change => !string.IsNullOrWhiteSpace(change.NewPath))
            .OrderByDescending(change => change.ChangedAt)
            .Take(20)
            .ToList();

    private static string? GetExecutableName(TargetDescriptor target)
    {
        if (!string.IsNullOrWhiteSpace(target.ExecutablePath))
        {
            return Path.GetFileName(target.ExecutablePath);
        }

        var displayName = target.DisplayName;
        var pidMarkerIndex = displayName.IndexOf(" (", StringComparison.Ordinal);
        if (pidMarkerIndex > 0)
        {
            displayName = displayName[..pidMarkerIndex];
        }

        return string.IsNullOrWhiteSpace(displayName)
            ? null
            : displayName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? displayName
                : $"{displayName}.exe";
    }

    public static string NormalizeExeName(string exeName)
    {
        exeName = Path.GetFileName(exeName.Trim());
        if (string.IsNullOrWhiteSpace(exeName))
        {
            return string.Empty;
        }

        return exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exeName.ToLowerInvariant()
            : $"{exeName}.exe".ToLowerInvariant();
    }

    public static string NormalizeFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
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

    private static int GetProfileOrder(string id) => id switch
    {
        ThresholdProfileDefaults.LightBackgroundId => 0,
        ThresholdProfileDefaults.BrowsersChatsId => 1,
        ThresholdProfileDefaults.GamesId => 2,
        ThresholdProfileDefaults.HardcoreId => 3,
        ThresholdProfileDefaults.CustomId => 4,
        _ => 10
    };
}
