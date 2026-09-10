using System.Text.Json;
using DevelopTi.Data.Abstractions;
using DevelopTi.Data.Models;

namespace DevelopTi.Data.Services;

/// <summary>Persiste os perfis de conexão (sem senha) em um arquivo JSON local.</summary>
public class JsonFileConnectionProfileStore : IConnectionProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileConnectionProfileStore(string storageDirectory)
    {
        Directory.CreateDirectory(storageDirectory);
        _filePath = Path.Combine(storageDirectory, "connections.json");
    }

    public async Task<IReadOnlyList<ConnectionProfile>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await LoadAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ConnectionProfile?> GetByIdAsync(string id)
    {
        var all = await GetAllAsync();
        return all.FirstOrDefault(p => p.Id == id);
    }

    public async Task SaveAsync(ConnectionProfile profile)
    {
        await _lock.WaitAsync();
        try
        {
            var all = (await LoadAsync()).ToList();
            var index = all.FindIndex(p => p.Id == profile.Id);
            if (index >= 0) all[index] = profile; else all.Add(profile);
            await PersistAsync(all);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var all = (await LoadAsync()).Where(p => p.Id != id).ToList();
            await PersistAsync(all);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<ConnectionProfile>> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return new List<ConnectionProfile>();

        await using var stream = File.OpenRead(_filePath);
        var profiles = await JsonSerializer.DeserializeAsync<List<ConnectionProfile>>(stream, JsonOptions);
        return profiles ?? new List<ConnectionProfile>();
    }

    private async Task PersistAsync(List<ConnectionProfile> profiles)
    {
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, profiles, JsonOptions);
    }
}
