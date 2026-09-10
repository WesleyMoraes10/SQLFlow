namespace DevelopTi.Data.Models;

/// <summary>
/// Perfil de conexão persistido (sem senha — a senha fica no ICredentialStore).
/// </summary>
public class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public DatabaseKind Kind { get; set; }
    public string Host { get; set; } = string.Empty;

    /// <summary>Nula para SQL Server = deixa o SQL Server Browser (UDP 1434) resolver a porta de
    /// instâncias nomeadas (ex: "SERVIDOR\INSTANCIA") em vez de forçar uma porta TCP fixa.</summary>
    public int? Port { get; set; }

    /// <summary>Service name/SID (Oracle) ou nome do banco (SQL Server/MySQL).</summary>
    public string Database { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public static int DefaultPort(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Oracle => 1521,
        DatabaseKind.SqlServer => 1433,
        DatabaseKind.MySql => 3306,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
