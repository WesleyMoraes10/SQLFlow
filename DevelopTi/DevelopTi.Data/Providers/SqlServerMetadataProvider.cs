using System.Data.Common;
using Dapper;
using DevelopTi.Data.Abstractions;
using DevelopTi.Data.Models;

namespace DevelopTi.Data.Providers;

/// <summary>Metadados via catálogos sys.* do SQL Server.</summary>
public class SqlServerMetadataProvider : IMetadataProvider
{
    public bool SupportsDatabaseBrowsing => true;

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(DbConnection connection, CancellationToken ct = default)
    {
        // database_id > 4 exclui os bancos de sistema (master/tempdb/model/msdb); state = 0 = online.
        const string sql = @"
            SELECT name FROM sys.databases
            WHERE state = 0 AND database_id > 4
            ORDER BY name";
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var names = await connection.QueryAsync<string>(command);
        return names.ToList();
    }

    // Usa INFORMATION_SCHEMA em vez de sys.tables/sys.schemas: catálogos sys.* só mostram objetos
    // pra quem tem VIEW DEFINITION (ou dono), enquanto INFORMATION_SCHEMA reflete direito qualquer
    // GRANT (ex: SELECT) — logins com permissão só de leitura via role às vezes ficam "cegos" pros
    // sys.* mas continuam vendo tudo aqui, igual no SSMS.
    public async Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(DbConnection connection, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT DISTINCT TABLE_SCHEMA AS Name
            FROM INFORMATION_SCHEMA.TABLES
            ORDER BY Name";
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var names = await connection.QueryAsync<string>(command);
        return names.Select(n => new SchemaInfo { Name = n }).ToList();
    }

    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(DbConnection connection, string schema, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT TABLE_SCHEMA AS SchemaName, TABLE_NAME AS TableName,
                   CASE WHEN TABLE_TYPE = 'VIEW' THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS IsView
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = @schema
            ORDER BY TableName";
        var command = new CommandDefinition(sql, new { schema }, cancellationToken: ct);
        var rows = await connection.QueryAsync(command);
        return rows.Select(r => new TableInfo { Schema = r.SchemaName, Name = r.TableName, IsView = r.IsView }).ToList();
    }

    public async Task<IReadOnlyList<TableInfo>> GetAllTablesAsync(DbConnection connection, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT TABLE_SCHEMA AS SchemaName, TABLE_NAME AS TableName,
                   CASE WHEN TABLE_TYPE = 'VIEW' THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS IsView
            FROM INFORMATION_SCHEMA.TABLES
            ORDER BY TableName";
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var rows = await connection.QueryAsync(command);
        return rows.Select(r => new TableInfo { Schema = r.SchemaName, Name = r.TableName, IsView = r.IsView }).ToList();
    }

    public async Task<IReadOnlyList<string>> FindTableSchemasAsync(DbConnection connection, string tableName, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT DISTINCT TABLE_SCHEMA
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_NAME = @table";
        var command = new CommandDefinition(sql, new { table = tableName }, cancellationToken: ct);
        var names = await connection.QueryAsync<string>(command);
        return names.ToList();
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT COLUMN_NAME AS ColumnName, DATA_TYPE AS DataType,
                   CASE WHEN IS_NULLABLE = 'YES' THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS Nullable,
                   ORDINAL_POSITION AS OrdinalPosition
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION";
        var command = new CommandDefinition(sql, new { schema, table }, cancellationToken: ct);
        var rows = await connection.QueryAsync(command);

        var primaryKeyColumns = (await GetPrimaryKeyColumnsAsync(connection, schema, table, ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return rows.Select(r => new ColumnInfo
        {
            Name = r.ColumnName,
            DataType = r.DataType,
            Nullable = r.Nullable,
            OrdinalPosition = (int)r.OrdinalPosition,
            IsPrimaryKey = primaryKeyColumns.Contains((string)r.ColumnName)
        }).ToList();
    }

    public async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE i.is_primary_key = 1 AND s.name = @schema AND t.name = @table
            ORDER BY ic.key_ordinal";
        var command = new CommandDefinition(sql, new { schema, table }, cancellationToken: ct);
        var columns = await connection.QueryAsync<string>(command);
        return columns.ToList();
    }

    public async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT i.name AS IndexName, c.name AS ColumnName, ic.key_ordinal AS ColumnPosition, i.is_unique AS IsUnique
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE i.name IS NOT NULL AND s.name = @schema AND t.name = @table
            ORDER BY i.name, ic.key_ordinal";
        var command = new CommandDefinition(sql, new { schema, table }, cancellationToken: ct);
        var rows = await connection.QueryAsync(command);

        return rows
            .GroupBy(r => (string)r.IndexName)
            .Select(g => new IndexInfo
            {
                Name = g.Key,
                IsUnique = (bool)g.First().IsUnique,
                Columns = g.Select(r => (string)r.ColumnName).ToList()
            })
            .ToList();
    }

    public async Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT fk.name AS ConstraintName, pc.name AS ColumnName, rs.name AS RefSchema, rt.name AS RefTable, rc.name AS RefColumn
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.tables t ON t.object_id = fk.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE s.name = @schema AND t.name = @table
            ORDER BY fk.name, fkc.constraint_column_id";
        var command = new CommandDefinition(sql, new { schema, table }, cancellationToken: ct);
        var rows = await connection.QueryAsync(command);

        return rows.Select(r => new ForeignKeyInfo
        {
            Name = r.ConstraintName,
            Column = r.ColumnName,
            ReferencedSchema = r.RefSchema,
            ReferencedTable = r.RefTable,
            ReferencedColumn = r.RefColumn
        }).ToList();
    }

    public string BuildSelectAllSql(string? database, string schema, string table, int maxRows)
    {
        var qualifiedTable = string.IsNullOrEmpty(database)
            ? $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}"
            : $"{QuoteIdentifier(database)}.{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        return $"SELECT TOP ({maxRows}) * FROM {qualifiedTable}";
    }

    public string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    public string ParameterToken(int index) => $"@p{index}";

    public string ParameterName(int index) => $"@p{index}";
}
