namespace DevelopTi.Data.Models;

public class IndexInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsUnique { get; set; }
    public List<string> Columns { get; set; } = new();
}
