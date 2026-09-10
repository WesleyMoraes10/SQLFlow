using DevelopTi.Data.Models;

namespace DevelopTi.Data.Abstractions;

public interface IQueryHistoryStore
{
    Task AddAsync(QueryHistoryEntry entry);
    Task<IReadOnlyList<QueryHistoryEntry>> GetRecentAsync(string connectionProfileId, int take = 50);
}
