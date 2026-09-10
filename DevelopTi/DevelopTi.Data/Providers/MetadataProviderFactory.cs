using DevelopTi.Data.Abstractions;
using DevelopTi.Data.Models;

namespace DevelopTi.Data.Providers;

public class MetadataProviderFactory : IMetadataProviderFactory
{
    private readonly OracleMetadataProvider _oracle;
    private readonly SqlServerMetadataProvider _sqlServer;
    private readonly MySqlMetadataProvider _mySql;

    public MetadataProviderFactory(OracleMetadataProvider oracle, SqlServerMetadataProvider sqlServer, MySqlMetadataProvider mySql)
    {
        _oracle = oracle;
        _sqlServer = sqlServer;
        _mySql = mySql;
    }

    public IMetadataProvider GetProvider(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Oracle => _oracle,
        DatabaseKind.SqlServer => _sqlServer,
        DatabaseKind.MySql => _mySql,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Tipo de banco não suportado")
    };
}
