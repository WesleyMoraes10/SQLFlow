using SQLFlow.Data.Abstractions;

namespace SQLFlow.Services;

/// <summary>Guarda a senha de cada perfil de conexão no SecureStorage do MAUI (criptografado pelo SO).</summary>
public class SecureCredentialStore : ICredentialStore
{
    private static string KeyFor(string profileId) => $"conn_password_{profileId}";

    public Task SavePasswordAsync(string profileId, string password)
        => SecureStorage.Default.SetAsync(KeyFor(profileId), password);

    public Task<string?> GetPasswordAsync(string profileId)
        => SecureStorage.Default.GetAsync(KeyFor(profileId));

    public Task RemovePasswordAsync(string profileId)
    {
        SecureStorage.Default.Remove(KeyFor(profileId));
        return Task.CompletedTask;
    }
}
