using SessionPerfTracker.App.Services;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Domain.Services;
using SessionPerfTracker.Infrastructure.Settings;

namespace SessionPerfTracker.UnitTests;

public sealed class TrackedExecutableTests
{
    private static readonly ExecutableFingerprint DiscordV1 = new()
    {
        ProductName = "Discord",
        CompanyName = "Discord Inc.",
        OriginalFileName = "Discord.exe",
        FileVersion = "1.0.9013",
        SignerSubject = "Discord Inc.",
        FileSizeBytes = 150_000,
        Sha256 = "aaa"
    };

    [Fact]
    public void Updated_version_with_same_publisher_metadata_reaches_relocation_threshold()
    {
        var item = CreateBan(@"c:\apps\discord\app-1.0.9013\discord.exe", DiscordV1);
        var candidate = new ExecutableCandidate(
            @"c:\apps\discord\app-1.0.9014\discord.exe",
            "Discord.exe",
            DiscordV1 with { FileVersion = "1.0.9014", Sha256 = "bbb", FileSizeBytes = 151_000 });

        var score = TrackedExecutableMatcher.Score(item, candidate);

        Assert.InRange(score, TrackedExecutableMatcher.RelocationThreshold, TrackedExecutableMatcher.MaxMetadataScore);
    }

    [Fact]
    public void Same_name_without_matching_metadata_is_not_enough()
    {
        var item = CreateBan(@"c:\tools\updater.exe", new ExecutableFingerprint { ProductName = "Tool A", CompanyName = "A Corp" });
        var candidate = new ExecutableCandidate(@"c:\other\updater.exe", "updater.exe", new ExecutableFingerprint { ProductName = "Tool B", CompanyName = "B Corp" });

        Assert.True(TrackedExecutableMatcher.Score(item, candidate) < TrackedExecutableMatcher.RelocationThreshold);
    }

    [Fact]
    public void Only_an_identical_hash_counts_as_the_same_binary()
    {
        var item = CreateBan(@"c:\apps\discord.exe", DiscordV1);
        var copy = new ExecutableCandidate(@"d:\copy\renamed.exe", "renamed.exe", DiscordV1);
        var metadataTwin = new ExecutableCandidate(@"d:\copy\discord.exe", "discord.exe", DiscordV1 with { Sha256 = "ccc" });

        Assert.Equal(TrackedExecutableMatcher.SameBinaryScore, TrackedExecutableMatcher.Score(item, copy));
        Assert.True(TrackedExecutableMatcher.Score(item, metadataTwin) < TrackedExecutableMatcher.SameBinaryScore);
    }

    [Fact]
    public async Task Verification_relocates_a_missing_ban_to_the_updated_executable()
    {
        var oldPath = @"c:\apps\discord\app-1.0.9013\discord.exe";
        var newPath = @"c:\apps\discord\app-1.0.9014\discord.exe";
        var store = await CreateStoreAsync(CreateBan(oldPath, DiscordV1));
        var identity = new FakeIdentityProvider(new Dictionary<string, ExecutableFingerprint>(StringComparer.OrdinalIgnoreCase)
        {
            [newPath] = DiscordV1 with { FileVersion = "1.0.9014", Sha256 = "bbb", FileSizeBytes = 151_000 }
        });
        var coordinator = new TrackedExecutableCoordinator(
            store,
            identity,
            new FakeLocator(newPath),
            path => string.Equals(path, newPath, StringComparison.OrdinalIgnoreCase));

        var result = await coordinator.VerifyPathsAsync(forceSearch: true, CancellationToken.None);

        var update = Assert.Single(result.Updates);
        Assert.Equal(newPath, update.NewPath);
        var rule = Assert.Single(store.Current.ProcessBans.Active);
        Assert.Equal(newPath, rule.NormalizedPath);
        Assert.Equal(TrackedPathStatus.Relocated, rule.PathStatus);
        Assert.Equal(oldPath, Assert.Single(rule.PathHistory).PreviousPath);
        Assert.Contains(store.Current.ProcessBans.History, entry => entry.Action == "ban_path_updated");
    }

    [Fact]
    public async Task Verification_keeps_entry_missing_when_only_unrelated_candidates_exist()
    {
        var oldPath = @"c:\apps\discord\discord.exe";
        var strangerPath = @"c:\other\discord.exe";
        var store = await CreateStoreAsync(CreateBan(oldPath, DiscordV1));
        var identity = new FakeIdentityProvider(new Dictionary<string, ExecutableFingerprint>(StringComparer.OrdinalIgnoreCase)
        {
            [strangerPath] = new() { ProductName = "Not Discord", CompanyName = "Someone" }
        });
        var coordinator = new TrackedExecutableCoordinator(store, identity, new FakeLocator(strangerPath), _ => false);

        var result = await coordinator.VerifyPathsAsync(forceSearch: true, CancellationToken.None);

        Assert.Empty(result.Updates);
        var rule = Assert.Single(store.Current.ProcessBans.Active);
        Assert.Equal(oldPath, rule.NormalizedPath);
        Assert.Equal(TrackedPathStatus.Missing, rule.PathStatus);
    }

    private static ProcessBanRule CreateBan(string path, ExecutableFingerprint fingerprint) => new()
    {
        Id = $"ban_{Guid.NewGuid():N}",
        NormalizedPath = path,
        ExeName = Path.GetFileName(path),
        ProductName = fingerprint.ProductName,
        CompanyName = fingerprint.CompanyName,
        CreatedAt = DateTimeOffset.UtcNow,
        DurationLabel = "Permanent",
        Fingerprint = fingerprint
    };

    private static async Task<JsonThresholdSettingsStore> CreateStoreAsync(ProcessBanRule rule)
    {
        var directory = Path.Combine(Path.GetTempPath(), "spt-tracked-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonThresholdSettingsStore(Path.Combine(directory, "settings.json"));
        await store.LoadAsync();
        await store.UpdateAsync(settings => settings with
        {
            ProcessBans = settings.ProcessBans with { Active = [rule] }
        });
        return store;
    }

    private sealed class FakeIdentityProvider(IReadOnlyDictionary<string, ExecutableFingerprint> fingerprints) : IExecutableIdentityProvider
    {
        public Task<ExecutableFingerprint?> DescribeAsync(
            string path,
            bool includeSignature,
            bool includeHash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(fingerprints.TryGetValue(path, out var fingerprint) ? fingerprint : null);
    }

    private sealed class FakeLocator(params string[] candidates) : IExecutableLocator
    {
        public Task<IReadOnlyList<string>> FindCandidatePathsAsync(
            string missingPath,
            string exeName,
            string? productName,
            int maxResults,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(candidates);
    }
}
