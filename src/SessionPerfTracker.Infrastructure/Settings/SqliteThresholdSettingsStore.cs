using System.Text.Json;
using Microsoft.Data.Sqlite;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Settings;

public sealed class SqliteThresholdSettingsStore : IThresholdSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dbPath;
    private readonly string? _legacyJsonPath;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly Dictionary<string, string> _persistedJson = new(StringComparer.Ordinal);
    private bool _initialized;
    private bool _currentLoaded;

    public SqliteThresholdSettingsStore(string dbPath, string? legacyJsonPath = null)
    {
        _dbPath = dbPath;
        _legacyJsonPath = legacyJsonPath;
    }

    public CpuRamThresholdSettings Current { get; private set; } = ThresholdProfileDefaults.CreateSettings();

    /// <summary>Number of settings rows written since startup; used to verify no-op updates stay cheap.</summary>
    internal int RowsWritten { get; private set; }

    public async Task<CpuRamThresholdSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _settingsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await LoadIntoCurrentAsync(connection, cancellationToken).ConfigureAwait(false);
            return Current;
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task SaveAsync(CpuRamThresholdSettings settings, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _settingsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalized = JsonThresholdSettingsStore.Normalize(settings);
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await SaveCoreAsync(connection, normalized, cancellationToken).ConfigureAwait(false);
            Current = normalized;
            _currentLoaded = true;
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    /// <summary>
    /// Serialized read-modify-write against the in-memory state (this app is single-instance).
    /// Returning the same instance from <paramref name="update"/> skips the write entirely, and
    /// otherwise only the settings sections whose JSON changed are written.
    /// </summary>
    public async Task<CpuRamThresholdSettings> UpdateAsync(
        Func<CpuRamThresholdSettings, CpuRamThresholdSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _settingsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SqliteConnection? connection = null;
            try
            {
                if (!_currentLoaded)
                {
                    connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await LoadIntoCurrentAsync(connection, cancellationToken).ConfigureAwait(false);
                }

                var current = Current;
                var updated = update(current);
                if (ReferenceEquals(updated, current))
                {
                    return current;
                }

                var normalized = JsonThresholdSettingsStore.Normalize(updated);
                connection ??= await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await SaveCoreAsync(connection, normalized, cancellationToken).ConfigureAwait(false);
                Current = normalized;
                return Current;
            }
            finally
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private async Task LoadIntoCurrentAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var loaded = await LoadCoreAsync(connection, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
        {
            loaded = JsonThresholdSettingsStore.Normalize(await LoadLegacyOrDefaultAsync(cancellationToken).ConfigureAwait(false));
            await SaveCoreAsync(connection, loaded, cancellationToken).ConfigureAwait(false);
        }

        Current = JsonThresholdSettingsStore.Normalize(loaded);
        _currentLoaded = true;
    }

    public ThresholdLimitValues ResolveThresholds(TargetDescriptor target)
    {
        var exeName = GetExecutableName(target);
        if (!string.IsNullOrWhiteSpace(exeName)
            && Current.AppProfileAssignments.TryGetValue(JsonThresholdSettingsStore.NormalizeExeName(exeName), out var profileId)
            && Current.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase)) is { } profile)
        {
            return profile.Limits;
        }

        return Current.ToGlobalLimits();
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
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<CpuRamThresholdSettings> LoadLegacyOrDefaultAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_legacyJsonPath) && File.Exists(_legacyJsonPath))
        {
            var legacyStore = new JsonThresholdSettingsStore(_legacyJsonPath);
            return await legacyStore.LoadAsync(cancellationToken);
        }

        return ThresholdProfileDefaults.CreateSettings();
    }

    private async Task<CpuRamThresholdSettings?> LoadCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var settingsRows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var settingsCommand = connection.CreateCommand())
        {
            settingsCommand.CommandText = "SELECT key, value_json FROM settings;";
            await using var reader = await settingsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                settingsRows[reader.GetString(0)] = reader.GetString(1);
            }
        }

        _persistedJson.Clear();
        foreach (var (key, json) in settingsRows)
        {
            _persistedJson[key] = json;
        }

        var profiles = new List<ThresholdProfile>();
        await using (var profileCommand = connection.CreateCommand())
        {
            profileCommand.CommandText = "SELECT id, name, is_preset, limits_json FROM profiles ORDER BY sort_order, name;";
            await using var reader = await profileCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                profiles.Add(new ThresholdProfile
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    IsPreset = reader.GetInt32(2) != 0,
                    Limits = Deserialize<ThresholdLimitValues>(reader.GetString(3)) ?? new ThresholdLimitValues()
                });
            }
        }

        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var assignmentCommand = connection.CreateCommand())
        {
            assignmentCommand.CommandText = "SELECT exe_name, profile_id FROM app_assignments ORDER BY exe_name;";
            await using var reader = await assignmentCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                assignments[reader.GetString(0)] = reader.GetString(1);
            }
        }

        if (settingsRows.Count == 0 && profiles.Count == 0 && assignments.Count == 0)
        {
            return null;
        }

        var global = settingsRows.TryGetValue("global_thresholds", out var globalJson)
            ? Deserialize<ThresholdLimitValues>(globalJson) ?? new ThresholdLimitValues()
            : new ThresholdLimitValues();
        var antiNoise = settingsRows.TryGetValue("anti_noise", out var antiNoiseJson)
            ? Deserialize<AntiNoiseSettings>(antiNoiseJson) ?? new AntiNoiseSettings()
            : new AntiNoiseSettings();
        var capture = settingsRows.TryGetValue("capture", out var captureJson)
            ? Deserialize<CaptureSettings>(captureJson) ?? new CaptureSettings()
            : new CaptureSettings();
        var retention = settingsRows.TryGetValue("retention", out var retentionJson)
            ? Deserialize<RetentionSettings>(retentionJson) ?? new RetentionSettings()
            : new RetentionSettings();
        var export = settingsRows.TryGetValue("export", out var exportJson)
            ? Deserialize<ExportSettings>(exportJson) ?? new ExportSettings()
            : new ExportSettings();
        var recommendations = settingsRows.TryGetValue("recommendations", out var recommendationsJson)
            ? Deserialize<ProfileRecommendationSettings>(recommendationsJson) ?? new ProfileRecommendationSettings()
            : new ProfileRecommendationSettings();
        var watchJournal = settingsRows.TryGetValue("watch_journal", out var watchJournalJson)
            ? Deserialize<GlobalWatchJournalSettings>(watchJournalJson) ?? new GlobalWatchJournalSettings()
            : new GlobalWatchJournalSettings();
        var suspiciousWatchlist = settingsRows.TryGetValue("suspicious_watchlist", out var suspiciousWatchlistJson)
            ? Deserialize<SuspiciousWatchlistSettings>(suspiciousWatchlistJson) ?? new SuspiciousWatchlistSettings()
            : new SuspiciousWatchlistSettings();
        var processBans = settingsRows.TryGetValue("process_bans", out var processBansJson)
            ? Deserialize<ProcessBanSettings>(processBansJson) ?? new ProcessBanSettings()
            : new ProcessBanSettings();
        var updates = settingsRows.TryGetValue("updates", out var updatesJson)
            ? Deserialize<AppUpdateSettings>(updatesJson) ?? new AppUpdateSettings()
            : new AppUpdateSettings();
        var behavior = settingsRows.TryGetValue("behavior", out var behaviorJson)
            ? Deserialize<AppBehaviorSettings>(behaviorJson) ?? new AppBehaviorSettings()
            : new AppBehaviorSettings();
        var language = settingsRows.TryGetValue("language", out var languageJson)
            ? Deserialize<AppLanguageSettings>(languageJson) ?? new AppLanguageSettings()
            : new AppLanguageSettings();
        var feedback = settingsRows.TryGetValue("feedback", out var feedbackJson)
            ? Deserialize<FeedbackSettings>(feedbackJson) ?? new FeedbackSettings()
            : new FeedbackSettings();
        var selectedProfileId = settingsRows.TryGetValue("selected_profile_id", out var selectedProfileJson)
            ? Deserialize<string>(selectedProfileJson) ?? ThresholdProfileDefaults.BrowsersChatsId
            : ThresholdProfileDefaults.BrowsersChatsId;

        return new CpuRamThresholdSettings
        {
            CpuThresholdPercent = global.CpuThresholdPercent,
            RamThresholdMb = global.RamThresholdMb,
            DiskReadThresholdMbPerSec = global.DiskReadThresholdMbPerSec,
            DiskWriteThresholdMbPerSec = global.DiskWriteThresholdMbPerSec,
            GpuThresholdPercent = global.GpuThresholdPercent ?? ThresholdProfileDefaults.DefaultGpuThresholdPercent,
            SelectedProfileId = selectedProfileId,
            Profiles = profiles,
            AppProfileAssignments = assignments,
            AntiNoise = antiNoise,
            Capture = capture,
            Retention = retention,
            Export = export,
            Recommendations = recommendations,
            WatchJournal = watchJournal,
            SuspiciousWatchlist = suspiciousWatchlist,
            ProcessBans = processBans,
            Updates = updates,
            Behavior = behavior,
            Language = language,
            Feedback = feedback
        };
    }

    private async Task SaveCoreAsync(
        SqliteConnection connection,
        CpuRamThresholdSettings settings,
        CancellationToken cancellationToken)
    {
        settings = JsonThresholdSettingsStore.Normalize(settings);
        var rows = new (string Key, string Json)[]
        {
            ("global_thresholds", Serialize(settings.ToGlobalLimits())),
            ("selected_profile_id", Serialize(settings.SelectedProfileId)),
            ("anti_noise", Serialize(settings.AntiNoise)),
            ("capture", Serialize(settings.Capture)),
            ("retention", Serialize(settings.Retention)),
            ("export", Serialize(settings.Export)),
            ("recommendations", Serialize(settings.Recommendations)),
            ("watch_journal", Serialize(settings.WatchJournal)),
            ("suspicious_watchlist", Serialize(settings.SuspiciousWatchlist)),
            ("process_bans", Serialize(settings.ProcessBans)),
            ("updates", Serialize(settings.Updates)),
            ("behavior", Serialize(settings.Behavior)),
            ("language", Serialize(settings.Language)),
            ("feedback", Serialize(settings.Feedback))
        };
        var orderedAssignments = settings.AppProfileAssignments
            .Select(item => new KeyValuePair<string, string>(JsonThresholdSettingsStore.NormalizeExeName(item.Key), item.Value))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var profilesJson = Serialize(settings.Profiles);
        var assignmentsJson = Serialize(orderedAssignments);

        var changedRows = rows
            .Where(row => !_persistedJson.TryGetValue(row.Key, out var persisted) || !string.Equals(persisted, row.Json, StringComparison.Ordinal))
            .ToArray();
        var profilesChanged = !_persistedJson.TryGetValue(ProfilesSignatureKey, out var persistedProfiles)
            || !string.Equals(persistedProfiles, profilesJson, StringComparison.Ordinal);
        var assignmentsChanged = !_persistedJson.TryGetValue(AssignmentsSignatureKey, out var persistedAssignments)
            || !string.Equals(persistedAssignments, assignmentsJson, StringComparison.Ordinal);
        if (changedRows.Length == 0 && !profilesChanged && !assignmentsChanged)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (key, json) in changedRows)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO settings (key, value_json) VALUES ($key, $value_json)
                ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value_json", json);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (profilesChanged)
        {
            await ExecuteInTransactionAsync(connection, transaction, "DELETE FROM profiles;", cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < settings.Profiles.Count; index++)
            {
                var profile = settings.Profiles[index];
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO profiles (id, name, is_preset, limits_json, sort_order)
                    VALUES ($id, $name, $is_preset, $limits_json, $sort_order);
                    """;
                command.Parameters.AddWithValue("$id", profile.Id);
                command.Parameters.AddWithValue("$name", profile.Name);
                command.Parameters.AddWithValue("$is_preset", profile.IsPreset ? 1 : 0);
                command.Parameters.AddWithValue("$limits_json", Serialize(profile.Limits));
                command.Parameters.AddWithValue("$sort_order", index);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (assignmentsChanged)
        {
            await ExecuteInTransactionAsync(connection, transaction, "DELETE FROM app_assignments;", cancellationToken).ConfigureAwait(false);
            foreach (var assignment in orderedAssignments)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO app_assignments (exe_name, profile_id)
                    VALUES ($exe_name, $profile_id);
                    """;
                command.Parameters.AddWithValue("$exe_name", assignment.Key);
                command.Parameters.AddWithValue("$profile_id", assignment.Value);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (key, json) in changedRows)
        {
            _persistedJson[key] = json;
        }

        _persistedJson[ProfilesSignatureKey] = profilesJson;
        _persistedJson[AssignmentsSignatureKey] = assignmentsJson;
        RowsWritten += changedRows.Length
            + (profilesChanged ? settings.Profiles.Count : 0)
            + (assignmentsChanged ? orderedAssignments.Length : 0);
    }

    private const string ProfilesSignatureKey = "@profiles";
    private const string AssignmentsSignatureKey = "@assignments";

    private static async Task ExecuteInTransactionAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS profiles (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                is_preset INTEGER NOT NULL,
                limits_json TEXT NOT NULL,
                sort_order INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_assignments (
                exe_name TEXT PRIMARY KEY,
                profile_id TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, SerializerOptions);

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, SerializerOptions);

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
}
