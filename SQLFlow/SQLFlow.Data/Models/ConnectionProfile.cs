namespace SQLFlow.Data.Models;

/// <summary>
/// Perfil de conexão persistido (sem senha — a senha fica no ICredentialStore).
/// </summary>
public class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public DatabaseKind Kind { get; set; }
    /// <summary>Para SQLite guarda o caminho do arquivo do banco em vez de um endereço de rede.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Nula para SQL Server (deixa o SQL Server Browser (UDP 1434) resolver a porta de
    /// instâncias nomeadas, ex: "SERVIDOR\INSTANCIA") e para SQLite (arquivo local, sem porta).</summary>
    public int? Port { get; set; }

    /// <summary>Service name/SID (Oracle) ou nome do banco (SQL Server/MySQL). Sem uso no SQLite —
    /// o arquivo (<see cref="Host"/>) já identifica o banco por completo.</summary>
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
