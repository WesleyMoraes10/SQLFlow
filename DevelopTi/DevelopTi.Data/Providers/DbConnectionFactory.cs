using System.Data.Common;
using DevelopTi.Data.Abstractions;
using DevelopTi.Data.Models;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Oracle.ManagedDataAccess.Client;

namespace DevelopTi.Data.Providers;

public class DbConnectionFactory : IDbConnectionFactory
{
    public DbConnection CreateConnection(ConnectionProfile profile, string password)
    {
        return profile.Kind switch
        {
            DatabaseKind.Oracle => new OracleConnection(BuildOracleConnectionString(profile, password)),
            DatabaseKind.SqlServer => new SqlConnection(BuildSqlServerConnectionString(profile, password)),
            DatabaseKind.MySql => new MySqlConnection(BuildMySqlConnectionString(profile, password)),
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
}
