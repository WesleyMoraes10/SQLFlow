using System.Data;
using System.Data.Common;
using SQLFlow.Data.Abstractions;
using SQLFlow.Data.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Oracle.ManagedDataAccess.Client;

namespace SQLFlow.Data.Providers;

public class DbConnectionFactory : IDbConnectionFactory
{
    public DbConnection CreateConnection(ConnectionProfile profile, string password)
    {
        return profile.Kind switch
        {
            DatabaseKind.Oracle => new OracleConnection(BuildOracleConnectionString(profile, password)),
            DatabaseKind.SqlServer => new SqlConnection(BuildSqlServerConnectionString(profile, password)),
            DatabaseKind.MySql => new MySqlConnection(BuildMySqlConnectionString(profile, password)),
            DatabaseKind.Sqlite => CreateSqliteConnection(profile),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Kind, "Tipo de banco não suportado")
        };
    }

    private static string BuildOracleConnectionString(ConnectionProfile profile, string password)
    {
        // EZ Connect: host:port/service_name
        var port = profile.Port ?? ConnectionProfile.DefaultPort(profile.Kind);
        var dataSource = $"{profile.Host}:{port}/{profile.Database}";
        return $"User Id={profile.Username};Password={password};Data Source={dataSource};";
    }

    private static string BuildSqlServerConnectionString(ConnectionProfile profile, string password)
    {
        // Sem porta: deixa em branco pro SQL Server Browser (UDP 1434) resolver a porta real de
        // instâncias nomeadas (ex: "SERVIDOR\INSTANCIA"), já que elas não usam 1433 por padrão.
        var dataSource = profile.Port.HasValue ? $"{profile.Host},{profile.Port.Value}" : profile.Host;
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = profile.Database,
            UserID = profile.Username,
            Password = password,
            TrustServerCertificate = true
        };
        return builder.ConnectionString;
    }

    private static string BuildMySqlConnectionString(ConnectionProfile profile, string password)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = profile.Host,
            Port = (uint)(profile.Port ?? ConnectionProfile.DefaultPort(profile.Kind)),
            Database = profile.Database,
            UserID = profile.Username,
            Password = password
        };
        return builder.ConnectionString;
    }

    // Sem servidor/porta/usuário/senha: o arquivo em profile.Host já identifica o banco por completo.
    // ReadWriteCreate cria o arquivo se ainda não existir — igual abrir um SQLite novo no DBeaver.
    private static string BuildSqliteConnectionString(ConnectionProfile profile)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = profile.Host,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        return builder.ConnectionString;
    }

    // Microsoft.Data.Sqlite não tem uma opção de connection string pra journal mode - só dá pra
    // setar rodando PRAGMA depois de aberta. Sem isso, o SQLite usa o rollback journal padrão, onde
    // uma transação aberta (mesmo só de leitura, ex.: uma aba em modo manual com um SELECT parado)
    // segura um lock que bloqueia QUALQUER outro processo de escrever no arquivo até a transação
    // fechar. Em WAL, leitores não bloqueiam escritores (nem entre processos diferentes), então o
    // SQLFlow parar com uma transação pendente deixa de travar outros sistemas que usam o mesmo
    // arquivo. StateChange garante que roda pra toda conexão criada por este factory, não importa
    // de onde ela é aberta (query, grid, autocomplete, teste de conexão).
    private static SqliteConnection CreateSqliteConnection(ConnectionProfile profile)
    {
        var connection = new SqliteConnection(BuildSqliteConnectionString(profile));
        connection.StateChange += (_, e) =>
        {
            if (e.CurrentState != ConnectionState.Open) return;
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            command.ExecuteNonQuery();
        };
        return connection;
    }
}
