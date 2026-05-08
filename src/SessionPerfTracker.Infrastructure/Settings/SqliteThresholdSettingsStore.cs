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
    private bool _initialized;

    public SqliteThresholdSettingsStore(string dbPath, string? legacyJsonPath = null)
    {
        _dbPath = dbPath;
        _legacyJsonPath = legacyJsonPath;
    }

    public CpuRamThresholdSettings Current { get; private set; } = ThresholdProfileDefaults.CreateSettings();

    public async Task<CpuRamThresholdSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var loaded = await LoadCoreAsync(connection, cancellationToken);
        if (loaded is null)
        {
            loaded = await LoadLegacyOrDefaultAsync(cancellationToken);
            await SaveCoreAsync(connection, loaded, cancellationToken);
        }

        Current = JsonThresholdSettingsStore.Normalize(loaded);
        return Current;
    }

    public async Task SaveAsync(CpuRamThresholdSettings settings, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        Current = JsonThresholdSettingsStore.Normalize(settings);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await SaveCoreAsync(connection, Current, cancellationToken);
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

    private static async Task<CpuRamThresholdSettings?> LoadCoreAsync(
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
        var selectedProfileId = settingsRows.TryGetValue("selected_profile_id", out var selectedProfileJson)
            ? Deserialize<string>(selectedProfileJson) ?? ThresholdProfileDefaults.BrowsersChatsId
            : ThresholdProfileDefaults.BrowsersChatsId;

        return new CpuRamThresholdSettings
        {
            CpuThresholdPercent = global.CpuThresholdPercent,
            RamThresholdMb = global.RamThresholdMb,
            DiskReadThresholdMbPerSec = global.DiskReadThresholdMbPerSec,
            DiskWriteThresholdMbPerSec = global.DiskWriteThresholdMbPerSec,
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
            Updates = updates
        };
    }

    private static async Task SaveCoreAsync(
        SqliteConnection connection,
        CpuRamThresholdSettings settings,
        CancellationToken cancellationToken)
    {
        settings = JsonThresholdSettingsStore.Normalize(settings);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var table in new[] { "settings", "profiles", "app_assignments" })
        {
            await using var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = (SqliteTransaction)transaction;
            deleteCommand.CommandText = $"DELETE FROM {table};";
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertSettingAsync(connection, transaction, "global_thresholds", settings.ToGlobalLimits(), cancellationToken);
        await InsertSettingAsync(connection, transaction, "selected_profile_id", settings.SelectedProfileId, cancellationToken);
        await InsertSettingAsync(connection, transaction, "anti_noise", settings.AntiNoise, cancellationToken);
        await InsertSettingAsync(connection, transaction, "capture", settings.Capture, cancellationToken);
        await InsertSettingAsync(connection, transaction, "retention", settings.Retention, cancellationToken);
        await InsertSettingAsync(connection, transaction, "export", settings.Export, cancellationToken);
        await InsertSettingAsync(connection, transaction, "recommendations", settings.Recommendations, cancellationToken);
        await InsertSettingAsync(connection, transaction, "watch_journal", settings.WatchJournal, cancellationToken);
        await InsertSettingAsync(connection, transaction, "suspicious_watchlist", settings.SuspiciousWatchlist, cancellationToken);
        await InsertSettingAsync(connection, transaction, "process_bans", settings.ProcessBans, cancellationToken);
        await InsertSettingAsync(connection, transaction, "updates", settings.Updates, cancellationToken);

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
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var assignment in settings.AppProfileAssignments.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO app_assignments (exe_name, profile_id)
                VALUES ($exe_name, $profile_id);
                """;
            command.Parameters.AddWithValue("$exe_name", JsonThresholdSettingsStore.NormalizeExeName(assignment.Key));
            command.Parameters.AddWithValue("$profile_id", assignment.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task InsertSettingAsync<T>(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string key,
        T value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO settings (key, value_json) VALUES ($key, $value_json);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value_json", Serialize(value));
        await command.ExecuteNonQueryAsync(cancellationToken);
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
