using SQLFlow.Data.Abstractions;
using SQLFlow.Data.Models;

namespace SQLFlow.Data.Providers;

public class MetadataProviderFactory : IMetadataProviderFactory
{
    private readonly OracleMetadataProvider _oracle;
    private readonly SqlServerMetadataProvider _sqlServer;
    private readonly MySqlMetadataProvider _mySql;
    private readonly SqliteMetadataProvider _sqlite;

    public MetadataProviderFactory(
        OracleMetadataProvider oracle,
        SqlServerMetadataProvider sqlServer,
        MySqlMetadataProvider mySql,
        SqliteMetadataProvider sqlite)
    {
        _oracle = oracle;
        _sqlServer = sqlServer;
        _mySql = mySql;
        _sqlite = sqlite;
    }

    public IMetadataProvider GetProvider(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Oracle => _oracle,
        DatabaseKind.SqlServer => _sqlServer,
        DatabaseKind.MySql => _mySql,
        DatabaseKind.Sqlite => _sqlite,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Tipo de banco não suportado")
    };
}
