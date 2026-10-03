using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Domain.Services;
using SessionPerfTracker.Infrastructure.Settings;
using SessionPerfTracker.Infrastructure.Storage;

namespace SessionPerfTracker.UnitTests;

public sealed class SessionStoragePerformanceTests
{
    [Fact]
    public async Task Running_session_saves_append_and_reload_every_sample_in_order()
    {
        var store = new SqliteSessionStore(CreateDbPath(), new SessionSummaryService());
        var samples = new List<MetricSample>();
        var events = new List<PerformanceEvent>();

        for (var batch = 0; batch < 5; batch++)
        {
            for (var index = 0; index < 10; index++)
            {
                samples.Add(CreateSample("s1", samples.Count));
            }

            if (batch == 2)
            {
                events.Add(CreateEvent("s1", "s1_event_a", samples.Count));
            }

            await store.SaveSessionAsync(CreateSession("s1", SessionStatus.Running, samples, events));
        }

        events.Add(CreateEvent("s1", "s1_event_b", samples.Count));
        await store.SaveSessionAsync(CreateSession("s1", SessionStatus.Stopped, samples, events));

        var loaded = await store.GetSessionAsync("s1");
        Assert.NotNull(loaded);
        Assert.Equal(50, loaded.Samples.Count);
        Assert.Equal(Enumerable.Range(0, 50).Select(index => index * 1000L), loaded.Samples.Select(sample => sample.ElapsedMs));
        Assert.Equal(["s1_event_a", "s1_event_b"], loaded.Events.Select(item => item.Id).Order().ToArray());
        Assert.Equal(SessionStatus.Stopped, loaded.Status);
    }

    [Fact]
    public async Task Resaving_a_shorter_session_replaces_rows_instead_of_appending()
    {
        var store = new SqliteSessionStore(CreateDbPath(), new SessionSummaryService());
        var samples = Enumerable.Range(0, 20).Select(index => CreateSample("s2", index)).ToList();
        await store.SaveSessionAsync(CreateSession("s2", SessionStatus.Running, samples, []));

        // e.g. an edited/imported copy with fewer samples must not keep stale rows
        await store.SaveSessionAsync(CreateSession("s2", SessionStatus.Completed, samples.Take(5).ToList(), []));

        var loaded = await store.GetSessionAsync("s2");
        Assert.Equal(5, loaded!.Samples.Count);
    }

    [Fact]
    public async Task Session_headers_carry_summary_without_loading_samples()
    {
        var store = new SqliteSessionStore(CreateDbPath(), new SessionSummaryService());
        var samples = Enumerable.Range(0, 30).Select(index => CreateSample("s3", index)).ToList();
        await store.SaveSessionAsync(CreateSession("s3", SessionStatus.Completed, samples, []));

        var headers = await store.ListSessionHeadersAsync();

        var header = Assert.Single(headers);
        Assert.Empty(header.Samples);
        Assert.Equal(30, header.Summary.SampleCount);
        Assert.Contains(header.Summary.Metrics, metric => metric.Key == MetricKey.GpuPercent);
    }

    [Fact]
    public async Task Interrupted_running_sessions_are_finalized_on_recovery()
    {
        var dbPath = CreateDbPath();
        var store = new SqliteSessionStore(dbPath, new SessionSummaryService());
        var samples = Enumerable.Range(0, 12).Select(index => CreateSample("s4", index)).ToList();
        await store.SaveSessionAsync(CreateSession("s4", SessionStatus.Running, samples, []));

        var restarted = new SqliteSessionStore(dbPath, new SessionSummaryService());
        Assert.Equal(1, await restarted.RecoverInterruptedSessionsAsync());

        var recovered = await restarted.GetSessionAsync("s4");
        Assert.Equal(SessionStatus.UnexpectedExit, recovered!.Status);
        Assert.Equal(samples[^1].Timestamp, recovered.EndedAt);
        Assert.Contains(recovered.Events, item => item.Id == "s4_stability_interrupted");
        Assert.Equal(0, await restarted.RecoverInterruptedSessionsAsync());
    }

    [Fact]
    public async Task Settings_updates_that_change_nothing_do_not_write()
    {
        var store = new SqliteThresholdSettingsStore(CreateDbPath());
        await store.LoadAsync();
        await store.UpdateAsync(settings => settings with { CpuThresholdPercent = 70 });
        var rowsAfterRealChange = store.RowsWritten;

        await store.UpdateAsync(settings => settings);

        Assert.Equal(rowsAfterRealChange, store.RowsWritten);
    }

    [Fact]
    public async Task Settings_update_writes_only_the_changed_section()
    {
        var dbPath = CreateDbPath();
        var store = new SqliteThresholdSettingsStore(dbPath);
        await store.LoadAsync();
        await store.UpdateAsync(settings => settings with { CpuThresholdPercent = 70 });
        var before = store.RowsWritten;

        await store.UpdateAsync(settings => settings with
        {
            WatchJournal = settings.WatchJournal with { MaxEntries = 321 }
        });

        Assert.Equal(before + 1, store.RowsWritten);
        var reloaded = new SqliteThresholdSettingsStore(dbPath);
        var settings = await reloaded.LoadAsync();
        Assert.Equal(321, settings.WatchJournal.MaxEntries);
        Assert.Equal(70, settings.CpuThresholdPercent);
    }

    [Fact]
    public void Legacy_profiles_without_gpu_limits_get_profile_defaults()
    {
        var legacyProfiles = ThresholdProfileDefaults.CreateProfiles()
            .Select(profile => profile with { Limits = profile.Limits with { GpuThresholdPercent = null } })
            .ToList();

        var normalized = JsonThresholdSettingsStore.Normalize(ThresholdProfileDefaults.CreateSettings() with { Profiles = legacyProfiles });

        Assert.Equal(100, normalized.Profiles.Single(profile => profile.Id == ThresholdProfileDefaults.GamesId).Limits.GpuThresholdPercent);
        Assert.Equal(30, normalized.Profiles.Single(profile => profile.Id == ThresholdProfileDefaults.LightBackgroundId).Limits.GpuThresholdPercent);
    }

    private static string CreateDbPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spt-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "sessionperftracker.db");
    }

    private static readonly DateTimeOffset StartedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static MetricSample CreateSample(string sessionId, int index) => new()
    {
        Id = $"{sessionId}_sample_{index * 1000}",
        SessionId = sessionId,
        Timestamp = StartedAt.AddSeconds(index),
        ElapsedMs = index * 1000L,
        RootProcessId = 1234,
        ProcessCount = 2,
        Values = new Dictionary<MetricKey, double>
        {
            [MetricKey.CpuPercent] = 10 + index % 7,
            [MetricKey.MemoryMb] = 500 + index,
            [MetricKey.GpuPercent] = 30 + index % 5,
            [MetricKey.GpuMemoryMb] = 900
        },
        SourceReliability = new Dictionary<MetricKey, MetricReliability>
        {
            [MetricKey.CpuPercent] = MetricReliability.Stable,
            [MetricKey.GpuPercent] = MetricReliability.BestEffort
        }
    };

    private static PerformanceEvent CreateEvent(string sessionId, string id, int sampleCount) => new()
    {
        Id = id,
        SessionId = sessionId,
        Kind = EventKind.Spike,
        MetricKey = MetricKey.CpuPercent,
        Timestamp = StartedAt.AddSeconds(sampleCount),
        ElapsedMs = sampleCount * 1000L,
        Severity = EventSeverity.Warning,
        Title = "CPU spike",
        Details = "test",
        DetectionProvider = "test"
    };

    private static SessionRecord CreateSession(
        string id,
        SessionStatus status,
        IReadOnlyList<MetricSample> samples,
        IReadOnlyList<PerformanceEvent> events)
    {
        var withoutSummary = new SessionRecordWithoutSummary
        {
            Id = id,
            Target = new TargetDescriptor { Id = "target", DisplayName = "game.exe (1234)", ExecutablePath = @"C:\Games\game.exe" },
            Status = status,
            StartedAt = StartedAt,
            EndedAt = status == SessionStatus.Running ? null : samples.LastOrDefault()?.Timestamp ?? StartedAt,
            Samples = samples.ToArray(),
            Events = events.ToArray()
        };

        return withoutSummary.WithSummary(new SessionSummaryService().Summarize(withoutSummary));
    }
}
