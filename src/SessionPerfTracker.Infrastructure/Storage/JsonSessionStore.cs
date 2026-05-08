using System.Text.Json;
using System.Text.Json.Serialization;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Infrastructure.MockData;

namespace SessionPerfTracker.Infrastructure.Storage;

public sealed class JsonSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly ISessionSummaryService _summaryService;
    private SessionStoreFile? _cache;

    public JsonSessionStore(string filePath, ISessionSummaryService summaryService)
    {
        _filePath = filePath;
        _summaryService = summaryService;
    }

    public async Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var data = await LoadAsync(cancellationToken);
        return data.Sessions
            .OrderByDescending(session => session.StartedAt)
            .ToArray();
    }

    public async Task<SessionRecord?> GetSessionAsync(string id, CancellationToken cancellationToken = default)
    {
        var data = await LoadAsync(cancellationToken);
        return data.Sessions.FirstOrDefault(session => session.Id == id);
    }

    public async Task SaveSessionAsync(SessionRecord session, CancellationToken cancellationToken = default)
    {
        var data = await LoadAsync(cancellationToken);
        var index = data.Sessions.FindIndex(item => item.Id == session.Id);

        if (index >= 0)
        {
            data.Sessions[index] = session;
        }
        else
        {
            data.Sessions.Add(session);
        }

        await PersistAsync(data, cancellationToken);
    }

    public async Task DeleteSessionAsync(string id, CancellationToken cancellationToken = default)
    {
        var data = await LoadAsync(cancellationToken);
        data.Sessions.RemoveAll(session => session.Id == id);
        await PersistAsync(data, cancellationToken);
    }

    public async Task<int> DeleteSessionsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        var idSet = ids.ToHashSet(StringComparer.Ordinal);
        var data = await LoadAsync(cancellationToken);
        var deleted = data.Sessions.RemoveAll(session => idSet.Contains(session.Id));
        await PersistAsync(data, cancellationToken);
        return deleted;
    }

    public async Task<int> DeleteAllSessionsAsync(CancellationToken cancellationToken = default)
    {
        var data = await LoadAsync(cancellationToken);
        var deleted = data.Sessions.Count;
        data.Sessions.Clear();
        await PersistAsync(data, cancellationToken);
        return deleted;
    }

    public async Task<int> DeleteSessionsOlderThanAsync(TimeSpan age, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - age;
        var data = await LoadAsync(cancellationToken);
        var deleted = data.Sessions.RemoveAll(session => session.StartedAt < cutoff);
        await PersistAsync(data, cancellationToken);
        return deleted;
    }

    private async Task<SessionStoreFile> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        if (!File.Exists(_filePath))
        {
            _cache = new SessionStoreFile
            {
                Sessions = MockSessionFactory.Create(_summaryService).ToList()
            };
            await PersistAsync(_cache, cancellationToken);
            return _cache;
        }

        await using var stream = File.OpenRead(_filePath);
        _cache = await JsonSerializer.DeserializeAsync<SessionStoreFile>(stream, SerializerOptions, cancellationToken)
                 ?? new SessionStoreFile();

        return _cache;
    }

    private async Task PersistAsync(SessionStoreFile data, CancellationToken cancellationToken)
    {
        _cache = data;
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, data, SerializerOptions, cancellationToken);
    }

    private sealed class SessionStoreFile
    {
        public int SchemaVersion { get; init; } = 1;
        public List<SessionRecord> Sessions { get; set; } = [];
    }
}
