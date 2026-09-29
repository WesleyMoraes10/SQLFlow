namespace SQLFlow.Data.Abstractions;

/// <summary>
/// Armazenamento seguro da senha de um perfil de conexão. Implementação concreta fica no
/// projeto MAUI (usa SecureStorage), pois é uma API específica de plataforma.
/// </summary>
public interface ICredentialStore
{
    Task SavePasswordAsync(string profileId, string password);
    Task<string?> GetPasswordAsync(string profileId);
    Task RemovePasswordAsync(string profileId);
}
