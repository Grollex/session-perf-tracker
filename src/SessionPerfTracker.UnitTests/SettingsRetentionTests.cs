using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Infrastructure.Settings;

namespace SessionPerfTracker.UnitTests;

public sealed class SettingsRetentionTests
{
    [Fact]
    public async Task Json_settings_store_trims_unbounded_histories_on_save()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spt-settings-retention-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonThresholdSettingsStore(Path.Combine(directory, "settings.json"));
        var now = DateTimeOffset.UtcNow;
        var settings = ThresholdProfileDefaults.CreateSettings() with
        {
            WatchJournal = new GlobalWatchJournalSettings
            {
                MaxEntries = 60,
                Entries = Enumerable.Range(0, 90)
                    .Select(index => CreateWatchJournalEntry(index, now.AddMinutes(-index)))
                    .ToList()
            },
            SuspiciousWatchlist = new SuspiciousWatchlistSettings
            {
                MaxLaunchHistory = 70,
                LaunchHistory = Enumerable.Range(0, 90)
                    .Select(index => CreateSuspiciousLaunchEntry(index, now.AddMinutes(-index)))
                    .ToList()
            },
            ProcessBans = new ProcessBanSettings
            {
                MaxHistory = 80,
                History = Enumerable.Range(0, 100)
                    .Select(index => CreateProcessBanEvent(index, now.AddMinutes(-index)))
                    .ToList()
            },
            Recommendations = new ProfileRecommendationSettings
            {
                History = Enumerable.Range(0, 700)
                    .Select(index => CreateRecommendationHistoryEntry(index, now.AddMinutes(-index)))
                    .ToList()
            }
        };

        await store.SaveAsync(settings);

        Assert.Equal(60, store.Current.WatchJournal.Entries.Count);
        Assert.Equal("app0.exe", store.Current.WatchJournal.Entries[0].ExeName);
        Assert.Equal("app59.exe", store.Current.WatchJournal.Entries[^1].ExeName);
        Assert.Equal(70, store.Current.SuspiciousWatchlist.LaunchHistory.Count);
        Assert.Equal(80, store.Current.ProcessBans.History.Count);
        Assert.Equal(500, store.Current.Recommendations.History.Count);
    }

    private static GlobalWatchJournalEntry CreateWatchJournalEntry(int index, DateTimeOffset timestamp) => new()
    {
        Id = $"watch_{index}",
        Timestamp = timestamp,
        ExeName = $"app{index}.exe",
        WatchMode = "Included processes",
        ProfileSource = "Global fallback",
        HealthState = "Warning",
        Reason = "test"
    };

    private static SuspiciousLaunchEntry CreateSuspiciousLaunchEntry(int index, DateTimeOffset timestamp) => new()
    {
        Id = $"suspicious_{index}",
        Timestamp = timestamp,
        NormalizedPath = $@"C:\Apps\app{index}.exe",
        ExeName = $"app{index}.exe",
        WatchMode = "Included processes"
    };

    private static ProcessBanEvent CreateProcessBanEvent(int index, DateTimeOffset timestamp) => new()
    {
        Id = $"ban_event_{index}",
        Timestamp = timestamp,
        NormalizedPath = $@"C:\Apps\app{index}.exe",
        ExeName = $"app{index}.exe",
        Action = "ban_enforced",
        DurationLabel = "Permanent"
    };

    private static ProfileRecommendationHistoryEntry CreateRecommendationHistoryEntry(int index, DateTimeOffset timestamp) => new()
    {
        Kind = "recommendation_created",
        ExeName = $"app{index}.exe",
        SuggestedProfileId = ThresholdProfileDefaults.GamesId,
        SuggestedProfileName = "Games / heavy apps",
        Timestamp = timestamp,
        Reason = "test"
    };
}
