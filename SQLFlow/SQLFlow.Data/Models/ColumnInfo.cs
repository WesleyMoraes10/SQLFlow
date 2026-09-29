namespace SQLFlow.Data.Models;

public class ColumnInfo
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool Nullable { get; set; }
    public bool IsPrimaryKey { get; set; }

    /// <summary>Gerada automaticamente pelo banco (IDENTITY/AUTO_INCREMENT/GENERATED AS IDENTITY) — não
    /// deve receber valor explícito num INSERT.</summary>
    public bool IsIdentity { get; set; }

    public int OrdinalPosition { get; set; }
}
