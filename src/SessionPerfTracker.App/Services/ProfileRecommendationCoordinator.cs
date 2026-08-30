using SessionPerfTracker.App.ViewModels;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.App.Services;

public sealed class ProfileRecommendationCoordinator
{
    private readonly GlobalWatchJournalCoordinator _journalCoordinator;
    private readonly Dictionary<string, List<DateTimeOffset>> _overLimitWarnings = new(StringComparer.OrdinalIgnoreCase);

    public ProfileRecommendationCoordinator(GlobalWatchJournalCoordinator journalCoordinator)
    {
        _journalCoordinator = journalCoordinator;
    }

    public void ResetWarnings(string exeName)
    {
        _overLimitWarnings.Remove(exeName);
    }

    public ProfileRecommendationUpdate Update(
        CpuRamThresholdSettings settings,
        IReadOnlyList<GlobalProcessRowViewModel> rows,
        DateTimeOffset capturedAt,
        string applicationWatchMode,
        string processWatchMode)
    {
        var recommendationSettings = settings.Recommendations;
        var active = recommendationSettings.Active.ToList();
        var history = recommendationSettings.History.ToList();
        var watchJournal = settings.WatchJournal;
        var journalEntries = watchJournal.Entries.ToList();
        var changed = false;
        var window = TimeSpan.FromMinutes(recommendationSettings.TriggerWindowMinutes);
        var cutoff = capturedAt - window;

        foreach (var group in rows
                     .Where(row => row.IsOutOfProfile && !string.IsNullOrWhiteSpace(row.ExeName))
                     .GroupBy(row => row.ExeName, StringComparer.OrdinalIgnoreCase))
        {
            var row = group
                .OrderByDescending(item => item.IsCritical)
                .ThenByDescending(item => item.IsOverLimit)
                .ThenByDescending(item => item.CpuPercent ?? 0)
                .ThenByDescending(item => item.MemoryMb ?? 0)
                .First();
            var suggestedProfile = ResolveNextProfile(row.ProfileId, settings);
            if (suggestedProfile is null || IsDenied(recommendationSettings, row.ExeName, suggestedProfile.Id))
            {
                continue;
            }

            if (!_overLimitWarnings.TryGetValue(row.ExeName, out var warnings))
            {
                warnings = [];
                _overLimitWarnings[row.ExeName] = warnings;
            }

            warnings.RemoveAll(timestamp => timestamp < cutoff);
            warnings.Add(capturedAt);

            if (warnings.Count < recommendationSettings.TriggerWarningCount)
            {
                continue;
            }

            var recommendationId = CreateRecommendationId(row.ExeName, row.ProfileId, suggestedProfile.Id);
            var existingIndex = active.FindIndex(item => string.Equals(item.Id, recommendationId, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                var existing = active[existingIndex];
                if ((capturedAt - existing.LastSeen).TotalSeconds >= 30)
                {
                    active[existingIndex] = existing with
                    {
                        WarningCount = Math.Max(existing.WarningCount, warnings.Count),
                        LastSeen = capturedAt,
                        Reason = row.RecommendationReason,
                        FullPath = ExecutableIdentity.IsUnavailablePath(row.FullPath) ? existing.FullPath : row.FullPath
                    };
                    changed = true;
                }

                continue;
            }

            var reason = $"{row.ExeName} repeatedly exceeded {row.ProfileSourceText}; suggested {suggestedProfile.Name}.";
            active.Add(new ProfileRecommendationRecord
            {
                Id = recommendationId,
                ExeName = row.ExeName,
                FullPath = ExecutableIdentity.IsUnavailablePath(row.FullPath) ? null : row.FullPath,
                CurrentProfileId = row.ProfileId,
                CurrentProfileName = row.ProfileName,
                CurrentProfileSource = row.IsAssignedProfile ? "Assigned profile" : "Global fallback",
                SuggestedProfileId = suggestedProfile.Id,
                SuggestedProfileName = suggestedProfile.Name,
                WarningCount = warnings.Count,
                Reason = row.RecommendationReason,
                FirstSeen = warnings.Min(),
                LastSeen = capturedAt
            });
            journalEntries.Insert(0, _journalCoordinator.CreateEntry(
                row,
                capturedAt,
                applicationWatchMode,
                processWatchMode,
                "Recommendation",
                reason,
                recommendationId));
            history = AddHistory(
                history,
                "recommendation_created",
                row.ExeName,
                suggestedProfile.Id,
                suggestedProfile.Name,
                reason,
                row.FullPath);
            changed = true;
        }

        foreach (var key in _overLimitWarnings.Keys.ToArray())
        {
            _overLimitWarnings[key].RemoveAll(timestamp => timestamp < cutoff);
            if (_overLimitWarnings[key].Count == 0)
            {
                _overLimitWarnings.Remove(key);
            }
        }

        if (!changed)
        {
            return new ProfileRecommendationUpdate(false, recommendationSettings, watchJournal);
        }

        return new ProfileRecommendationUpdate(
            true,
            recommendationSettings with
            {
                Active = active
                    .OrderByDescending(item => item.LastSeen)
                    .ToList(),
                History = history
            },
            watchJournal with
            {
                Entries = _journalCoordinator.TrimEntries(journalEntries, watchJournal.MaxEntries)
            });
    }

    public static ThresholdProfile? ResolveNextProfile(string profileId, CpuRamThresholdSettings settings)
    {
        var nextProfileId = profileId switch
        {
            ThresholdProfileDefaults.LightBackgroundId => ThresholdProfileDefaults.BrowsersChatsId,
            ThresholdProfileDefaults.BrowsersChatsId => ThresholdProfileDefaults.GamesId,
            ThresholdProfileDefaults.GamesId => ThresholdProfileDefaults.HardcoreId,
            _ => null
        };

        return nextProfileId is null
            ? null
            : settings.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, nextProfileId, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsDenied(
        ProfileRecommendationSettings recommendationSettings,
        string exeName,
        string suggestedProfileId) =>
        recommendationSettings.Denied.Any(item => string.Equals(item.ExeName, exeName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.SuggestedProfileId, suggestedProfileId, StringComparison.OrdinalIgnoreCase));

    public static string CreateRecommendationId(string exeName, string currentProfileId, string suggestedProfileId) =>
        $"{ExecutableIdentity.NormalizeExeNameForAssignment(exeName)}|{currentProfileId}|{suggestedProfileId}".ToLowerInvariant();

    public static List<ProfileRecommendationHistoryEntry> AddHistory(
        IReadOnlyList<ProfileRecommendationHistoryEntry> history,
        string kind,
        string exeName,
        string suggestedProfileId,
        string suggestedProfileName,
        string reason,
        string? fullPath = null)
    {
        return new[]
            {
                new ProfileRecommendationHistoryEntry
                {
                    Kind = kind,
                    ExeName = ExecutableIdentity.NormalizeExeNameForAssignment(exeName),
                    FullPath = ExecutableIdentity.IsUnavailablePath(fullPath) ? null : fullPath,
                    SuggestedProfileId = suggestedProfileId,
                    SuggestedProfileName = suggestedProfileName,
                    Timestamp = DateTimeOffset.UtcNow,
                    Reason = reason
                }
            }
            .Concat(history)
            .Take(500)
            .ToList();
    }
}

public sealed record ProfileRecommendationUpdate(
    bool Changed,
    ProfileRecommendationSettings Recommendations,
    GlobalWatchJournalSettings WatchJournal);
