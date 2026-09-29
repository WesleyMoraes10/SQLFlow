using SQLFlow.Data.Models;

namespace SQLFlow.Data.Abstractions;

public interface IQueryHistoryStore
{
    Task AddAsync(QueryHistoryEntry entry);
    Task<IReadOnlyList<QueryHistoryEntry>> GetRecentAsync(string connectionProfileId, int take = 50);
}
