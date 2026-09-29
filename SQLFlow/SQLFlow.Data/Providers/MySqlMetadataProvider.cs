using System.Data.Common;
using Dapper;
using SQLFlow.Data.Abstractions;
using SQLFlow.Data.Models;

namespace SQLFlow.Data.Providers;

/// <summary>Metadados via INFORMATION_SCHEMA (portável entre MySQL e MariaDB).</summary>
public class MySqlMetadataProvider : IMetadataProvider
{
    private static readonly string[] SystemSchemas = { "information_schema", "mysql", "performance_schema", "sys" };

    public bool SupportsDatabaseBrowsing => false;

    public string? LastIdentityQuery => "SELECT LAST_INSERT_ID()";

    public Task<IReadOnlyList<string>> GetDatabasesAsync(DbConnection connection, CancellationToken ct = default)
        => throw new NotSupportedException("No MySQL cada schema já é um banco — use GetSchemasAsync.");

    public async Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(DbConnection connection, CancellationToken ct = default)
    {
        const string sql = "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA ORDER BY SCHEMA_NAME";
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var names = await connection.QueryAsync<string>(command);
        return names
            .Where(n => !SystemSchemas.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Select(n => new SchemaInfo { Name = n })
            .ToList();
    }

    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(DbConnection connection, string schema, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = @schema
            ORDER BY TABLE_NAME";
        var command = new CommandDefinition(sql, new { schema }, cancellationToken: ct);
        var rows = await connection.QueryAsync(command);
        return rows.Select(r => new TableInfo
        {
            Schema = r.TABLE_SCHEMA,
            Name = r.TABLE_NAME,
            IsView = string.Equals((string)r.TABLE_TYPE, "VIEW", StringComparison.OrdinalIgnoreCase)
        }).ToList();
    }

    public async Task<IReadOnlyList<TableInfo>> GetAllTablesAsync(DbConnection connection, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
            FROM INFORMATION_SCHEMA.TABLES
            ORDER BY TABLE_NAME";
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var rows = await connection.QueryAsync(command);
        return rows
            .Select(r => new TableInfo
            {
                Schema = r.TABLE_SCHEMA,
                Name = r.TABLE_NAME,
                IsView = string.Equals((string)r.TABLE_TYPE, "VIEW", StringComparison.OrdinalIgnoreCase)
            })
            .Where(t => !SystemSchemas.Contains(t.Schema, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> FindTableSchemasAsync(DbConnection connection, string tableName, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT DISTINCT TABLE_SCHEMA
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_NAME = @table";
        var command = new CommandDefinition(sql, new { table = tableName }, cancellationToken: ct);
        var names = await connection.QueryAsync<string>(command);
        return names
            .Where(n => !SystemSchemas.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, ORDINAL_POSITION, EXTRA
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION";
        var command = new CommandDefinition(sql, new { schema, table }, cancellationToken: ct);
        var rows = await connection.QueryAsync(command);

        var primaryKeyColumns = (await GetPrimaryKeyColumnsAsync(connection, schema, table, ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return rows.Select(r => new ColumnInfo
        {
            Name = r.COLUMN_NAME,
            DataType = r.DATA_TYPE,
            Nullable = string.Equals((string)r.IS_NULLABLE, "YES", StringComparison.OrdinalIgnoreCase),
            OrdinalPosition = (int)r.ORDINAL_POSITION,
            IsPrimaryKey = primaryKeyColumns.Contains((string)r.COLUMN_NAME),
            IsIdentity = ((string)r.EXTRA).Contains("auto_increment", StringComparison.OrdinalIgnoreCase)
        }).ToList();
    }

    public async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT k.COLUMN_NAME
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
            JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
              ON tc.CONSTRAINT_NAME = k.CONSTRAINT_NAME AND tc.TABLE_SCHEMA = k.TABLE_SCHEMA AND tc.TABLE_NAME = k.TABLE_NAME
            WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY' AND k.TABLE_SCHEMA = @schema AND k.TABLE_NAME = @table
            ORDER BY k.ORDINAL_POSITION";
        var command = new CommandDefinition(sql, new { schema, table }, cancellationToken: ct);
        var columns = await connection.QueryAsync<string>(command);
        return columns.ToList();
    }

    public async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT INDEX_NAME, COLUMN_NAME, SEQ_IN_INDEX, NON_UNIQUE
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY INDEX_NAME, SEQ_IN_INDEX";
        var command = new CommandDefinition(sql, new { schema, table }, cancellationToken: ct);
        var rows = await connection.QueryAsync(command);

        return rows
            .GroupBy(r => (string)r.INDEX_NAME)
            .Select(g => new IndexInfo
            {
                Name = g.Key,
                IsUnique = Convert.ToInt32(g.First().NON_UNIQUE) == 0,
                Columns = g.Select(r => (string)r.COLUMN_NAME).ToList()
            })
            .ToList();
    }

    public async Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT CONSTRAINT_NAME, COLUMN_NAME, REFERENCED_TABLE_SCHEMA, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table AND REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY CONSTRAINT_NAME, ORDINAL_POSITION";
        var command = new CommandDefinition(sql, new { schema, table }, cancellationToken: ct);
        var rows = await connection.QueryAsync(command);

        return rows.Select(r => new ForeignKeyInfo
        {
            Name = r.CONSTRAINT_NAME,
            Column = r.COLUMN_NAME,
            ReferencedSchema = r.REFERENCED_TABLE_SCHEMA,
            ReferencedTable = r.REFERENCED_TABLE_NAME,
            ReferencedColumn = r.REFERENCED_COLUMN_NAME
        }).ToList();
    }

    public string BuildSelectAllSql(string? database, string schema, string table, int maxRows)
        => $"SELECT * FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(table)} LIMIT {maxRows}";

    public string QuoteIdentifier(string identifier) => $"`{identifier.Replace("`", "``")}`";

    public string ParameterToken(int index) => $"@p{index}";

    public string ParameterName(int index) => $"@p{index}";
}
