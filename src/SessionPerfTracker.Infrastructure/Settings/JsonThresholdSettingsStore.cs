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

    public JsonThresholdSettingsStore(string filePath)
    {
        _filePath = filePath;
    }

    public CpuRamThresholdSettings Current { get; private set; } = ThresholdProfileDefaults.CreateSettings();

    public async Task<CpuRamThresholdSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        if (!File.Exists(_filePath))
        {
            await SaveAsync(Current, cancellationToken);
            return Current;
        }

        await using var stream = File.OpenRead(_filePath);
        Current = await JsonSerializer.DeserializeAsync<CpuRamThresholdSettings>(
            stream,
            SerializerOptions,
            cancellationToken) ?? new CpuRamThresholdSettings();

        Current = Normalize(Current);
        return Current;
    }

    public async Task SaveAsync(CpuRamThresholdSettings settings, CancellationToken cancellationToken = default)
    {
        Current = Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, Current, SerializerOptions, cancellationToken);
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
            Updates = NormalizeUpdates(settings.Updates)
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
                Limits = NormalizeLimits(profile.Limits)
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

    private static ThresholdLimitValues NormalizeLimits(ThresholdLimitValues? limits)
    {
        limits ??= new ThresholdLimitValues();
        return limits with
        {
            CpuThresholdPercent = Math.Clamp(limits.CpuThresholdPercent, 1, 100),
            RamThresholdMb = Math.Clamp(limits.RamThresholdMb, 1, 1_048_576),
            DiskReadThresholdMbPerSec = Math.Clamp(limits.DiskReadThresholdMbPerSec, 0.1, 1_048_576),
            DiskWriteThresholdMbPerSec = Math.Clamp(limits.DiskWriteThresholdMbPerSec, 0.1, 1_048_576)
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
        var automaticallyCheck = settings.AutomaticallyCheckForUpdates || settings.LastCheckedAt is null;
        var skippedVersion = string.IsNullOrWhiteSpace(settings.SkippedVersion)
            ? null
            : settings.SkippedVersion.Trim().TrimStart('v', 'V');
        return settings with
        {
            AutomaticallyCheckForUpdates = automaticallyCheck,
            ManifestUrl = manifestUrl,
            SkippedVersion = skippedVersion
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
                .Select(item => item with { ExeName = NormalizeExeName(item.ExeName) })
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
                .Select(item => item with { ExeName = NormalizeExeName(item.ExeName) })
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
                .Select(item => item with
                {
                    NormalizedPath = NormalizeFullPath(item.NormalizedPath),
                    ExeName = NormalizeExeName(string.IsNullOrWhiteSpace(item.ExeName)
                        ? Path.GetFileName(item.NormalizedPath)
                        : item.ExeName)
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
                .Select(item => item with
                {
                    Id = string.IsNullOrWhiteSpace(item.Id) ? $"ban_{Guid.NewGuid():N}" : item.Id,
                    NormalizedPath = NormalizeFullPath(item.NormalizedPath),
                    ExeName = NormalizeExeName(string.IsNullOrWhiteSpace(item.ExeName)
                        ? Path.GetFileName(item.NormalizedPath)
                        : item.ExeName),
                    DurationLabel = string.IsNullOrWhiteSpace(item.DurationLabel)
                        ? item.ExpiresAt is null ? "Permanent" : "Timed"
                        : item.DurationLabel
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
