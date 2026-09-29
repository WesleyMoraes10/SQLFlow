namespace SQLFlow.Data.Models;

public class QueryResult
{
    public bool IsResultSet { get; set; }
    public List<string> Columns { get; set; } = new();
    public Dictionary<string, string> ColumnTypes { get; set; } = new();
    public List<Dictionary<string, object?>> Rows { get; set; } = new();
    public int? AffectedRows { get; set; }
    public TimeSpan Elapsed { get; set; }
}
