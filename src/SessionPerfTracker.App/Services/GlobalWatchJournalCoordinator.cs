using SessionPerfTracker.App.ViewModels;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.App.Services;

public sealed class GlobalWatchJournalCoordinator
{
    private static readonly TimeSpan JournalCooldown = TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, DateTimeOffset> _lastEntryByKey = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GlobalWatchJournalEntry> CreateThresholdEntries(
        IReadOnlyList<GlobalProcessRowViewModel> rows,
        string selectedWatchMode,
        string applicationWatchMode,
        string processWatchMode,
        DateTimeOffset capturedAt)
    {
        var entries = new List<GlobalWatchJournalEntry>();
        foreach (var row in rows.Where(row => row.IsNearLimit || row.IsOutOfProfile))
        {
            var key = $"{selectedWatchMode}|{row.SelectionKey}|{row.HealthBadgeText}|{row.ProfileReason}";
            if (_lastEntryByKey.TryGetValue(key, out var lastEntryAt)
                && capturedAt - lastEntryAt < JournalCooldown)
            {
                continue;
            }

            _lastEntryByKey[key] = capturedAt;
            entries.Add(CreateEntry(
                row,
                capturedAt,
                applicationWatchMode,
                processWatchMode,
                row.HealthBadgeText,
                row.ProfileReason));
        }

        return entries;
    }

    public GlobalWatchJournalEntry CreateEntry(
        GlobalProcessRowViewModel row,
        DateTimeOffset capturedAt,
        string applicationWatchMode,
        string processWatchMode,
        string healthState,
        string reason,
        string? recommendationId = null)
    {
        return new GlobalWatchJournalEntry
        {
            Id = $"watch_{Guid.NewGuid():N}",
            Timestamp = capturedAt,
            ExeName = ExecutableIdentity.NormalizeExeNameForAssignment(row.ExeName),
            DisplayName = row.DisplayName,
            ProcessId = row.IsGroup ? null : row.ProcessId,
            FullPath = ExecutableIdentity.IsUnavailablePath(row.FullPath) ? null : row.FullPath,
            WatchMode = row.IsGroup ? applicationWatchMode : processWatchMode,
            ProfileSource = row.ProfileSourceText,
            HealthState = healthState,
            Reason = reason,
            CpuPercent = row.CpuPercent,
            MemoryMb = row.MemoryMb,
            DiskReadMbPerSec = row.DiskReadMbPerSec,
            DiskWriteMbPerSec = row.DiskWriteMbPerSec,
            RecommendationId = recommendationId
        };
    }

    /// <summary>
    /// Journal entries store the localized mode label of the build that wrote them; accept current and older labels.
    /// </summary>
    public static bool IsApplicationsMode(string? watchMode) =>
        !string.IsNullOrWhiteSpace(watchMode)
        && (string.Equals(watchMode, ViewModels.UiText.Get("Ui_GlobalModeApplications"), StringComparison.OrdinalIgnoreCase)
            || string.Equals(watchMode, ViewModels.UiText.Get("Ui_AssignedAppsProfiles"), StringComparison.OrdinalIgnoreCase)
            || watchMode.Contains("Applications", StringComparison.OrdinalIgnoreCase)
            || watchMode.Contains("apps", StringComparison.OrdinalIgnoreCase));

    public List<GlobalWatchJournalEntry> TrimEntries(
        IEnumerable<GlobalWatchJournalEntry> entries,
        int maxEntries)
    {
        return entries
            .OrderByDescending(item => item.Timestamp)
            .Take(Math.Clamp(maxEntries, 50, 5000))
            .ToList();
    }
}
