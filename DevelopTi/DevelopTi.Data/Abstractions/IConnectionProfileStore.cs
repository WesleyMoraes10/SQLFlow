using DevelopTi.Data.Models;

namespace DevelopTi.Data.Abstractions;

/// <summary>Persistência dos perfis de conexão (sem senha) em disco.</summary>
public interface IConnectionProfileStore
{
    Task<IReadOnlyList<ConnectionProfile>> GetAllAsync();
    Task<ConnectionProfile?> GetByIdAsync(string id);
    Task SaveAsync(ConnectionProfile profile);
    Task DeleteAsync(string id);
}
