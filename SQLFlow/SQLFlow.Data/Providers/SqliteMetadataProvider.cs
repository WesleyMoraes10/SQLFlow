using System.Data.Common;
using Dapper;
using SQLFlow.Data.Abstractions;
using SQLFlow.Data.Models;

namespace SQLFlow.Data.Providers;

/// <summary>Metadados via sqlite_master + PRAGMAs. SQLite não tem múltiplos bancos por conexão nem
/// schemas de verdade — "main" é o único schema exposto (arquivos ATTACHed não são suportados aqui).</summary>
public class SqliteMetadataProvider : IMetadataProvider
{
    private const string MainSchema = "main";

    public bool SupportsDatabaseBrowsing => false;

    public string? LastIdentityQuery => "SELECT last_insert_rowid()";

    public Task<IReadOnlyList<string>> GetDatabasesAsync(DbConnection connection, CancellationToken ct = default)
        => throw new NotSupportedException("SQLite não expõe múltiplos bancos por conexão — use GetSchemasAsync.");

    public Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(DbConnection connection, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SchemaInfo>>(new List<SchemaInfo> { new() { Name = MainSchema } });

    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(DbConnection connection, string schema, CancellationToken ct = default)
        => await GetAllTablesAsync(connection, ct);

    public async Task<IReadOnlyList<TableInfo>> GetAllTablesAsync(DbConnection connection, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT name, type
            FROM sqlite_master
            WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'
            ORDER BY name";
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var rows = await connection.QueryAsync(command);
        return rows.Select(r => new TableInfo
        {
            Schema = MainSchema,
            Name = r.name,
            IsView = string.Equals((string)r.type, "view", StringComparison.OrdinalIgnoreCase)
        }).ToList();
    }

    public async Task<IReadOnlyList<string>> FindTableSchemasAsync(DbConnection connection, string tableName, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT name
            FROM sqlite_master
            WHERE type IN ('table', 'view') AND name = @table";
        var command = new CommandDefinition(sql, new { table = tableName }, cancellationToken: ct);
        var names = await connection.QueryAsync<string>(command);
        return names.Any() ? new[] { MainSchema } : Array.Empty<string>();
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        var command = new CommandDefinition($"PRAGMA table_info({QuoteIdentifier(table)})", cancellationToken: ct);
        var rows = await connection.QueryAsync(command);
        return rows.Select(r => new ColumnInfo
        {
            Name = r.name,
            DataType = string.IsNullOrEmpty((string)r.type) ? "BLOB" : r.type,
            Nullable = Convert.ToInt32(r.notnull) == 0,
            OrdinalPosition = Convert.ToInt32(r.cid) + 1,
            IsPrimaryKey = Convert.ToInt32(r.pk) > 0,
            // Só é auto-incremento "de verdade" quando a coluna é INTEGER PRIMARY KEY (rowid alias) —
            // SQLite não tem um flag de identity separado do próprio PK nesse caso.
            IsIdentity = Convert.ToInt32(r.pk) > 0 && string.Equals((string)r.type, "INTEGER", StringComparison.OrdinalIgnoreCase)
        }).ToList();
    }

    public async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        var command = new CommandDefinition($"PRAGMA table_info({QuoteIdentifier(table)})", cancellationToken: ct);
        var rows = await connection.QueryAsync(command);
        return rows
            .Where(r => Convert.ToInt32(r.pk) > 0)
            .OrderBy(r => Convert.ToInt32(r.pk))
            .Select(r => (string)r.name)
            .ToList();
    }

    public async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        var listCommand = new CommandDefinition($"PRAGMA index_list({QuoteIdentifier(table)})", cancellationToken: ct);
        var indexes = await connection.QueryAsync(listCommand);

        var result = new List<IndexInfo>();
        foreach (var index in indexes)
        {
            string indexName = index.name;
            var infoCommand = new CommandDefinition($"PRAGMA index_info({QuoteIdentifier(indexName)})", cancellationToken: ct);
            var columns = await connection.QueryAsync(infoCommand);
            result.Add(new IndexInfo
            {
                Name = indexName,
                IsUnique = Convert.ToInt32(index.unique) != 0,
                Columns = columns.OrderBy(c => Convert.ToInt32(c.seqno)).Select(c => (string)c.name).ToList()
            });
        }
        return result;
    }

    public async Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        var command = new CommandDefinition($"PRAGMA foreign_key_list({QuoteIdentifier(table)})", cancellationToken: ct);
        var rows = await connection.QueryAsync(command);
        return rows.Select(r => new ForeignKeyInfo
        {
            Name = $"fk_{table}_{(int)r.id}",
            Column = r.from,
            ReferencedSchema = MainSchema,
            ReferencedTable = r.table,
            ReferencedColumn = r.to
        }).ToList();
    }

    public string BuildSelectAllSql(string? database, string schema, string table, int maxRows)
        => $"SELECT * FROM {QuoteIdentifier(table)} LIMIT {maxRows}";

    public string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string ParameterToken(int index) => $"@p{index}";

    public string ParameterName(int index) => $"@p{index}";
}
