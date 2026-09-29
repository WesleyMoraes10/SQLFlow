namespace SQLFlow.Data.Models;

public class QueryHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ConnectionProfileId { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;
    public string Sql { get; set; } = string.Empty;
    public DateTime ExecutedAtUtc { get; set; }
    public double DurationMs { get; set; }
    public int? RowCount { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
