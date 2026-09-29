using System.Text.Json;
using SQLFlow.Data.Abstractions;
using SQLFlow.Data.Models;

namespace SQLFlow.Data.Services;

/// <summary>Histórico de execuções persistido em JSON local, com teto de entradas mais antigas descartadas.</summary>
public class JsonFileQueryHistoryStore : IQueryHistoryStore
{
    private const int MaxEntries = 200;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileQueryHistoryStore(string storageDirectory)
    {
        Directory.CreateDirectory(storageDirectory);
        _filePath = Path.Combine(storageDirectory, "query_history.json");
    }

    public async Task AddAsync(QueryHistoryEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await LoadAsync();
            all.Insert(0, entry);
            if (all.Count > MaxEntries)
            {
                all.RemoveRange(MaxEntries, all.Count - MaxEntries);
            }
            await PersistAsync(all);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<QueryHistoryEntry>> GetRecentAsync(string connectionProfileId, int take = 50)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await LoadAsync();
            return all
                .Where(e => e.ConnectionProfileId == connectionProfileId)
                .OrderByDescending(e => e.ExecutedAtUtc)
                .Take(take)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<QueryHistoryEntry>> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return new List<QueryHistoryEntry>();

        await using var stream = File.OpenRead(_filePath);
        var entries = await JsonSerializer.DeserializeAsync<List<QueryHistoryEntry>>(stream, JsonOptions);
        return entries ?? new List<QueryHistoryEntry>();
    }

    private async Task PersistAsync(List<QueryHistoryEntry> entries)
    {
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, entries, JsonOptions);
    }
}
