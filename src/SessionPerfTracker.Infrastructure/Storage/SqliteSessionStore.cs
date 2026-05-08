using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Storage;

public sealed class SqliteSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dbPath;
    private readonly ISessionSummaryService _summaryService;
    private readonly string? _legacyJsonPath;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public SqliteSessionStore(string dbPath, ISessionSummaryService summaryService, string? legacyJsonPath = null)
    {
        _dbPath = dbPath;
        _summaryService = summaryService;
        _legacyJsonPath = legacyJsonPath;
    }

    public async Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var ids = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM sessions ORDER BY started_at DESC;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }
        }

        var sessions = new List<SessionRecord>(ids.Count);
        foreach (var id in ids)
        {
            var session = await LoadSessionAsync(connection, id, cancellationToken);
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        return sessions;
    }

    public async Task<SessionRecord?> GetSessionAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await LoadSessionAsync(connection, id, cancellationToken);
    }

    public async Task SaveSessionAsync(SessionRecord session, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SaveSessionCoreAsync(connection, transaction, session, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteSessionAsync(string id, CancellationToken cancellationToken = default)
    {
        await DeleteSessionsAsync([id], cancellationToken);
    }

    public async Task<int> DeleteSessionsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var deleted = 0;
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "DELETE FROM sessions WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            deleted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<int> DeleteAllSessionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions;";
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteSessionsOlderThanAsync(TimeSpan age, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var cutoff = DateTimeOffset.UtcNow - age;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE started_at < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", ToDbTimestamp(cutoff));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await CreateSchemaAsync(connection, cancellationToken);
            await ImportLegacyJsonIfNeededAsync(connection, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task ImportLegacyJsonIfNeededAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_legacyJsonPath) || !File.Exists(_legacyJsonPath))
        {
            return;
        }

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(*) FROM sessions;";
            var count = (long)(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
            if (count > 0)
            {
                return;
            }
        }

        await using var stream = File.OpenRead(_legacyJsonPath);
        var legacy = await JsonSerializer.DeserializeAsync<LegacySessionStoreFile>(
            stream,
            SerializerOptions,
            cancellationToken);
        if (legacy?.Sessions.Count is not > 0)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var session in legacy.Sessions)
        {
            await SaveSessionCoreAsync(connection, transaction, session, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SessionRecord?> LoadSessionAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        SessionRecord? header = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT target_json, status, started_at, ended_at, sampling_json, capabilities_json, summary_json, notes, schema_version
                FROM sessions
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var target = DeserializeRequired<TargetDescriptor>(reader.GetString(0));
            var status = Enum.Parse<SessionStatus>(reader.GetString(1));
            var startedAt = DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind);
            var endedAt = reader.IsDBNull(3)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind);
            var sampling = DeserializeRequired<SamplingSettings>(reader.GetString(4));
            var capabilities = DeserializeRequired<MetricCapabilities>(reader.GetString(5));
            var summary = DeserializeRequired<SessionSummary>(reader.GetString(6));
            var notes = reader.IsDBNull(7) ? null : reader.GetString(7);
            var schemaVersion = reader.GetInt32(8);

            header = new SessionRecord
            {
                Id = id,
                Target = target,
                Status = status,
                StartedAt = startedAt,
                EndedAt = endedAt,
                Sampling = sampling,
                Capabilities = capabilities,
                Summary = summary,
                Notes = notes,
                SchemaVersion = schemaVersion
            };
        }

        var samples = await LoadSamplesAsync(connection, id, cancellationToken);
        var events = await LoadEventsAsync(connection, id, cancellationToken);
        var withoutSummary = new SessionRecordWithoutSummary
        {
            Id = header.Id,
            Target = header.Target,
            Status = header.Status,
            StartedAt = header.StartedAt,
            EndedAt = header.EndedAt,
            Sampling = header.Sampling,
            Capabilities = header.Capabilities,
            Samples = samples,
            Events = events,
            Notes = header.Notes,
            SchemaVersion = header.SchemaVersion
        };

        return withoutSummary.WithSummary(_summaryService.Summarize(withoutSummary));
    }

    private static async Task<IReadOnlyList<MetricSample>> LoadSamplesAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var samples = new List<MetricSample>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sample_json
            FROM metric_samples
            WHERE session_id = $session_id
            ORDER BY sample_index ASC;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            samples.Add(DeserializeRequired<MetricSample>(reader.GetString(0)));
        }

        return samples;
    }

    private static async Task<IReadOnlyList<PerformanceEvent>> LoadEventsAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var contexts = new Dictionary<string, SpikeContextSnapshot>(StringComparer.Ordinal);
        await using (var contextCommand = connection.CreateCommand())
        {
            contextCommand.CommandText = """
                SELECT event_id, context_json
                FROM event_context_snapshots
                WHERE session_id = $session_id;
                """;
            contextCommand.Parameters.AddWithValue("$session_id", sessionId);
            await using var contextReader = await contextCommand.ExecuteReaderAsync(cancellationToken);
            while (await contextReader.ReadAsync(cancellationToken))
            {
                contexts[contextReader.GetString(0)] = DeserializeRequired<SpikeContextSnapshot>(contextReader.GetString(1));
            }
        }

        var events = new List<PerformanceEvent>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_json
            FROM events
            WHERE session_id = $session_id
            ORDER BY elapsed_ms ASC;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var performanceEvent = DeserializeRequired<PerformanceEvent>(reader.GetString(0));
            if (contexts.TryGetValue(performanceEvent.Id, out var context))
            {
                performanceEvent = performanceEvent with { Context = context };
            }

            events.Add(performanceEvent);
        }

        return events;
    }

    private static async Task SaveSessionCoreAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        SessionRecord session,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO sessions (
                    id, target_display_name, target_exe_name, status, started_at, ended_at,
                    target_json, sampling_json, capabilities_json, summary_json, notes, schema_version, session_profile_name
                )
                VALUES (
                    $id, $target_display_name, $target_exe_name, $status, $started_at, $ended_at,
                    $target_json, $sampling_json, $capabilities_json, $summary_json, $notes, $schema_version, $session_profile_name
                )
                ON CONFLICT(id) DO UPDATE SET
                    target_display_name = excluded.target_display_name,
                    target_exe_name = excluded.target_exe_name,
                    status = excluded.status,
                    started_at = excluded.started_at,
                    ended_at = excluded.ended_at,
                    target_json = excluded.target_json,
                    sampling_json = excluded.sampling_json,
                    capabilities_json = excluded.capabilities_json,
                    summary_json = excluded.summary_json,
                    notes = excluded.notes,
                    schema_version = excluded.schema_version,
                    session_profile_name = excluded.session_profile_name;
                """;
            command.Parameters.AddWithValue("$id", session.Id);
            command.Parameters.AddWithValue("$target_display_name", session.Target.DisplayName);
            command.Parameters.AddWithValue("$target_exe_name", GetExecutableName(session.Target) ?? string.Empty);
            command.Parameters.AddWithValue("$status", session.Status.ToString());
            command.Parameters.AddWithValue("$started_at", ToDbTimestamp(session.StartedAt));
            command.Parameters.AddWithValue("$ended_at", session.EndedAt is null ? DBNull.Value : ToDbTimestamp(session.EndedAt.Value));
            command.Parameters.AddWithValue("$target_json", Serialize(session.Target));
            command.Parameters.AddWithValue("$sampling_json", Serialize(session.Sampling));
            command.Parameters.AddWithValue("$capabilities_json", Serialize(session.Capabilities));
            command.Parameters.AddWithValue("$summary_json", Serialize(session.Summary));
            command.Parameters.AddWithValue("$notes", (object?)session.Notes ?? DBNull.Value);
            command.Parameters.AddWithValue("$schema_version", session.SchemaVersion);
            command.Parameters.AddWithValue("$session_profile_name", (object?)session.Sampling.SessionProfileName ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await DeleteChildrenAsync(connection, transaction, session.Id, cancellationToken);
        for (var index = 0; index < session.Samples.Count; index++)
        {
            var sample = session.Samples[index];
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO metric_samples (
                    id, session_id, sample_index, timestamp, elapsed_ms, root_process_id,
                    process_count, values_json, reliability_json, sample_json
                )
                VALUES (
                    $id, $session_id, $sample_index, $timestamp, $elapsed_ms, $root_process_id,
                    $process_count, $values_json, $reliability_json, $sample_json
                );
                """;
            command.Parameters.AddWithValue("$id", sample.Id);
            command.Parameters.AddWithValue("$session_id", session.Id);
            command.Parameters.AddWithValue("$sample_index", index);
            command.Parameters.AddWithValue("$timestamp", ToDbTimestamp(sample.Timestamp));
            command.Parameters.AddWithValue("$elapsed_ms", sample.ElapsedMs);
            command.Parameters.AddWithValue("$root_process_id", sample.RootProcessId is null ? DBNull.Value : sample.RootProcessId);
            command.Parameters.AddWithValue("$process_count", sample.ProcessCount);
            command.Parameters.AddWithValue("$values_json", Serialize(sample.Values));
            command.Parameters.AddWithValue("$reliability_json", Serialize(sample.SourceReliability));
            command.Parameters.AddWithValue("$sample_json", Serialize(sample));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var performanceEvent in session.Events)
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO events (
                        id, session_id, kind, metric_key, timestamp, elapsed_ms, severity,
                        title, details, observed_value, threshold_value, detection_provider,
                        grouped_kinds_json, noise_policy, event_json
                    )
                    VALUES (
                        $id, $session_id, $kind, $metric_key, $timestamp, $elapsed_ms, $severity,
                        $title, $details, $observed_value, $threshold_value, $detection_provider,
                        $grouped_kinds_json, $noise_policy, $event_json
                    );
                    """;
                command.Parameters.AddWithValue("$id", performanceEvent.Id);
                command.Parameters.AddWithValue("$session_id", session.Id);
                command.Parameters.AddWithValue("$kind", performanceEvent.Kind.ToString());
                command.Parameters.AddWithValue("$metric_key", performanceEvent.MetricKey?.ToString() ?? string.Empty);
                command.Parameters.AddWithValue("$timestamp", ToDbTimestamp(performanceEvent.Timestamp));
                command.Parameters.AddWithValue("$elapsed_ms", performanceEvent.ElapsedMs);
                command.Parameters.AddWithValue("$severity", performanceEvent.Severity.ToString());
                command.Parameters.AddWithValue("$title", performanceEvent.Title);
                command.Parameters.AddWithValue("$details", performanceEvent.Details);
                command.Parameters.AddWithValue("$observed_value", performanceEvent.ObservedValue is null ? DBNull.Value : performanceEvent.ObservedValue);
                command.Parameters.AddWithValue("$threshold_value", performanceEvent.ThresholdValue is null ? DBNull.Value : performanceEvent.ThresholdValue);
                command.Parameters.AddWithValue("$detection_provider", performanceEvent.DetectionProvider);
                command.Parameters.AddWithValue("$grouped_kinds_json", Serialize(performanceEvent.GroupedKinds));
                command.Parameters.AddWithValue("$noise_policy", (object?)performanceEvent.NoisePolicy ?? DBNull.Value);
                command.Parameters.AddWithValue("$event_json", Serialize(performanceEvent with { Context = null }));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (performanceEvent.Context is not null)
            {
                await using var contextCommand = connection.CreateCommand();
                contextCommand.Transaction = (SqliteTransaction)transaction;
                contextCommand.CommandText = """
                    INSERT INTO event_context_snapshots (event_id, session_id, context_json)
                    VALUES ($event_id, $session_id, $context_json);
                    """;
                contextCommand.Parameters.AddWithValue("$event_id", performanceEvent.Id);
                contextCommand.Parameters.AddWithValue("$session_id", session.Id);
                contextCommand.Parameters.AddWithValue("$context_json", Serialize(performanceEvent.Context));
                await contextCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task DeleteChildrenAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "event_context_snapshots", "events", "metric_samples" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = $"DELETE FROM {table} WHERE session_id = $session_id;";
            command.Parameters.AddWithValue("$session_id", sessionId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                target_display_name TEXT NOT NULL,
                target_exe_name TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT NULL,
                target_json TEXT NOT NULL,
                sampling_json TEXT NOT NULL,
                capabilities_json TEXT NOT NULL,
                summary_json TEXT NOT NULL,
                notes TEXT NULL,
                schema_version INTEGER NOT NULL,
                session_profile_name TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_sessions_started_at ON sessions(started_at);
            CREATE INDEX IF NOT EXISTS ix_sessions_target_exe_name ON sessions(target_exe_name);

            CREATE TABLE IF NOT EXISTS metric_samples (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                sample_index INTEGER NOT NULL,
                timestamp TEXT NOT NULL,
                elapsed_ms INTEGER NOT NULL,
                root_process_id INTEGER NULL,
                process_count INTEGER NOT NULL,
                values_json TEXT NOT NULL,
                reliability_json TEXT NOT NULL,
                sample_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_metric_samples_session_index ON metric_samples(session_id, sample_index);

            CREATE TABLE IF NOT EXISTS events (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,
                metric_key TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                elapsed_ms INTEGER NOT NULL,
                severity TEXT NOT NULL,
                title TEXT NOT NULL,
                details TEXT NOT NULL,
                observed_value REAL NULL,
                threshold_value REAL NULL,
                detection_provider TEXT NOT NULL,
                grouped_kinds_json TEXT NOT NULL,
                noise_policy TEXT NULL,
                event_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_events_session_elapsed ON events(session_id, elapsed_ms);

            CREATE TABLE IF NOT EXISTS event_context_snapshots (
                event_id TEXT PRIMARY KEY REFERENCES events(id) ON DELETE CASCADE,
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                context_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_event_context_session ON event_context_snapshots(session_id);
            """, cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        return connection;
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, SerializerOptions);

    private static T DeserializeRequired<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, SerializerOptions)
        ?? throw new InvalidDataException($"Could not deserialize {typeof(T).Name}.");

    private static string ToDbTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O");

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

    private sealed class LegacySessionStoreFile
    {
        public List<SessionRecord> Sessions { get; set; } = [];
    }
}
