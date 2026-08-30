using SessionPerfTracker.App.Services;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.UnitTests;

public sealed class ViewModelCoordinationServiceTests
{
    [Theory]
    [InlineData("opera", "opera.exe")]
    [InlineData("Opera.EXE", "opera.exe")]
    [InlineData(@"C:\Tools\MyApp", "myapp.exe")]
    public void Executable_identity_normalizes_assignment_names(string input, string expected)
    {
        Assert.Equal(expected, ExecutableIdentity.NormalizeExeNameForAssignment(input));
    }

    [Fact]
    public void Global_watch_journal_trimming_keeps_newest_entries_and_clamps_limit()
    {
        var coordinator = new GlobalWatchJournalCoordinator();
        var now = DateTimeOffset.UtcNow;
        var entries = Enumerable.Range(0, 80)
            .Select(index => new GlobalWatchJournalEntry
            {
                Id = $"watch_{index}",
                Timestamp = now.AddMinutes(-index),
                ExeName = $"app{index}.exe",
                WatchMode = "Included processes",
                ProfileSource = "Global fallback",
                HealthState = "Warning",
                Reason = "test"
            })
            .ToArray();

        var trimmed = coordinator.TrimEntries(entries, 10);

        Assert.Equal(50, trimmed.Count);
        Assert.Equal("watch_0", trimmed[0].Id);
        Assert.Equal("watch_49", trimmed[^1].Id);
    }

    [Fact]
    public void Profile_recommendation_coordinator_resolves_next_heavier_profile()
    {
        var settings = ThresholdProfileDefaults.CreateSettings();

        var next = ProfileRecommendationCoordinator.ResolveNextProfile(
            ThresholdProfileDefaults.BrowsersChatsId,
            settings);

        Assert.NotNull(next);
        Assert.Equal(ThresholdProfileDefaults.GamesId, next.Id);
    }

    [Fact]
    public void Profile_recommendation_ids_are_stable_and_normalized()
    {
        var id = ProfileRecommendationCoordinator.CreateRecommendationId(
            "Discord",
            ThresholdProfileDefaults.BrowsersChatsId,
            ThresholdProfileDefaults.GamesId);

        Assert.Equal("discord.exe|browsers-chats|games", id);
    }

    [Fact]
    public void Process_ban_active_requires_path_and_non_expired_rule()
    {
        var now = DateTimeOffset.UtcNow;
        var active = new ProcessBanRule
        {
            Id = "ban_active",
            NormalizedPath = @"c:\apps\app.exe",
            ExeName = "app.exe",
            CreatedAt = now.AddMinutes(-1),
            ExpiresAt = now.AddMinutes(1),
            DurationLabel = "2 minutes"
        };
        var expired = active with { Id = "ban_expired", ExpiresAt = now.AddSeconds(-1) };
        var missingPath = active with { Id = "ban_missing_path", NormalizedPath = string.Empty };

        Assert.True(ProcessBanCoordinator.IsActive(active, now));
        Assert.False(ProcessBanCoordinator.IsActive(expired, now));
        Assert.False(ProcessBanCoordinator.IsActive(missingPath, now));
    }

    [Fact]
    public void Update_status_formatter_blocks_manifest_without_sha()
    {
        var formatter = new UpdateStatusFormatter(
            key => key,
            (key, args) => $"{key}:{string.Join("|", args.Select(item => item?.ToString()))}");
        var result = new UpdateCheckResult
        {
            CurrentVersion = "1.0.0",
            LatestVersion = "1.0.1",
            IsUpdateAvailable = true,
            Manifest = new UpdateManifest
            {
                Version = "1.0.1",
                InstallerUrl = "https://example.invalid/app.exe"
            },
            Status = "Update available."
        };

        var status = formatter.FormatCheckStatus(result);

        Assert.Contains("Ui_UpdateShaMissingDownloadBlocked", status);
    }
}
