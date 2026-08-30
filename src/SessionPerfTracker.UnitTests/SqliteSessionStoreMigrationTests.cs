using System.Text.Json;
using System.Text.Json.Serialization;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Domain.Services;
using SessionPerfTracker.Infrastructure.Storage;

namespace SessionPerfTracker.UnitTests;

public sealed class SqliteSessionStoreMigrationTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Legacy_import_runs_once_even_after_all_imported_sessions_are_deleted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spt-sqlite-migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "sessionperftracker.db");
        var legacyPath = Path.Combine(directory, "sessions.json");
        await WriteLegacySessionsAsync(legacyPath, [CreateSession("legacy-1")]);

        var store = new SqliteSessionStore(dbPath, new SessionSummaryService(), legacyPath);
        var imported = await store.ListSessionsAsync();

        Assert.Single(imported);
        Assert.Equal(1, await store.DeleteAllSessionsAsync());

        var freshStore = new SqliteSessionStore(dbPath, new SessionSummaryService(), legacyPath);
        var afterRestart = await freshStore.ListSessionsAsync();

        Assert.Empty(afterRestart);
    }

    [Fact]
    public async Task Broken_legacy_json_does_not_prevent_session_store_startup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spt-sqlite-migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "sessionperftracker.db");
        var legacyPath = Path.Combine(directory, "sessions.json");
        await File.WriteAllTextAsync(legacyPath, "{ broken legacy json");

        var store = new SqliteSessionStore(dbPath, new SessionSummaryService(), legacyPath);
        var sessions = await store.ListSessionsAsync();

        Assert.Empty(sessions);
    }

    private static async Task WriteLegacySessionsAsync(string path, IReadOnlyList<SessionRecord> sessions)
    {
        var json = JsonSerializer.Serialize(new LegacySessionStoreFile { Sessions = sessions.ToList() }, SerializerOptions);
        await File.WriteAllTextAsync(path, json);
    }

    private static SessionRecord CreateSession(string id)
    {
        var startedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var withoutSummary = new SessionRecordWithoutSummary
        {
            Id = id,
            Target = new TargetDescriptor
            {
                Id = "target-1",
                DisplayName = "Legacy Target",
                ExecutablePath = @"C:\Tools\legacy.exe"
            },
            Status = SessionStatus.Completed,
            StartedAt = startedAt,
            EndedAt = startedAt.AddMinutes(1),
            Sampling = new SamplingSettings(),
            Capabilities = new MetricCapabilities()
        };

        return withoutSummary.WithSummary(new SessionSummaryService().Summarize(withoutSummary));
    }

    private sealed class LegacySessionStoreFile
    {
        public List<SessionRecord> Sessions { get; init; } = [];
    }
}
