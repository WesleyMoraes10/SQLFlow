using System.Data.Common;
using Dapper;
using SQLFlow.Data.Abstractions;
using SQLFlow.Data.Models;
using Oracle.ManagedDataAccess.Client;

namespace SQLFlow.Data.Providers;

/// <summary>Metadados via catálogos ALL_* (visíveis a qualquer usuário com privilégio nos objetos).</summary>
public class OracleMetadataProvider : IMetadataProvider
{
    private static readonly string[] SystemSchemas =
    {
        "SYS", "SYSTEM", "OUTLN", "DBSNMP", "APPQOSSYS", "XDB", "ORDDATA", "CTXSYS", "MDSYS",
        "WMSYS", "GSMADMIN_INTERNAL", "ORDSYS", "OJVMSYS", "LBACSYS", "DVSYS", "AUDSYS",
        "GSMCATUSER", "GSMUSER", "REMOTE_SCHEDULER_AGENT", "SYSBACKUP", "SYSDG", "SYSKM",
        "SYSRAC", "DVF", "ORDPLUGINS", "ANONYMOUS"
    };

    public bool SupportsDatabaseBrowsing => false;

    // Oracle não tem um "LAST_INSERT_ID()" genérico: GENERATED AS IDENTITY só devolve o valor gerado via
    // RETURNING ... INTO no próprio INSERT, que exige parâmetro de saída específico do driver — a grid
    // simplesmente não sabe o valor até rodar a consulta de novo.
    public string? LastIdentityQuery => null;

    public Task<IReadOnlyList<string>> GetDatabasesAsync(DbConnection connection, CancellationToken ct = default)
        => throw new NotSupportedException("Oracle não expõe múltiplos bancos por conexão — use o schema (OWNER).");

    public async Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(DbConnection connection, CancellationToken ct = default)
    {
        const string sql = "SELECT DISTINCT OWNER AS Name FROM ALL_TABLES ORDER BY OWNER";
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var owners = (await connection.QueryAsync<string>(command)).ToList();
        return owners
            .Where(o => !SystemSchemas.Contains(o, StringComparer.OrdinalIgnoreCase))
            .Select(o => new SchemaInfo { Name = o })
            .ToList();
    }

    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(DbConnection connection, string schema, CancellationToken ct = default)
    {
        var tables = new List<TableInfo>();

        tables.AddRange(await QueryAsync(
            connection,
            "SELECT OWNER, TABLE_NAME FROM ALL_TABLES WHERE OWNER = :p_schema ORDER BY TABLE_NAME",
            new[] { ("p_schema", (object)schema) },
            r => new TableInfo { Schema = r.GetString(0), Name = r.GetString(1), IsView = false },
            ct));

        tables.AddRange(await QueryAsync(
            connection,
            "SELECT OWNER, VIEW_NAME FROM ALL_VIEWS WHERE OWNER = :p_schema ORDER BY VIEW_NAME",
            new[] { ("p_schema", (object)schema) },
            r => new TableInfo { Schema = r.GetString(0), Name = r.GetString(1), IsView = true },
            ct));

        return tables.OrderBy(t => t.Name).ToList();
    }

    public async Task<IReadOnlyList<TableInfo>> GetAllTablesAsync(DbConnection connection, CancellationToken ct = default)
    {
        var tables = new List<TableInfo>();

        var tablesCommand = new CommandDefinition("SELECT OWNER, TABLE_NAME FROM ALL_TABLES", cancellationToken: ct);
        var tableRows = await connection.QueryAsync(tablesCommand);
        tables.AddRange(tableRows.Select(r => new TableInfo { Schema = r.OWNER, Name = r.TABLE_NAME, IsView = false }));

        var viewsCommand = new CommandDefinition("SELECT OWNER, VIEW_NAME FROM ALL_VIEWS", cancellationToken: ct);
        var viewRows = await connection.QueryAsync(viewsCommand);
        tables.AddRange(viewRows.Select(r => new TableInfo { Schema = r.OWNER, Name = r.VIEW_NAME, IsView = true }));

        return tables
            .Where(t => !SystemSchemas.Contains(t.Schema, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> FindTableSchemasAsync(DbConnection connection, string tableName, CancellationToken ct = default)
    {
        var schemas = new List<string>();

        schemas.AddRange(await QueryAsync(
            connection,
            "SELECT OWNER FROM ALL_TABLES WHERE TABLE_NAME = :p_table",
            new[] { ("p_table", (object)tableName) },
            r => r.GetString(0),
            ct));

        schemas.AddRange(await QueryAsync(
            connection,
            "SELECT OWNER FROM ALL_VIEWS WHERE VIEW_NAME = :p_table",
            new[] { ("p_table", (object)tableName) },
            r => r.GetString(0),
            ct));

        return schemas
            .Where(s => !SystemSchemas.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT COLUMN_NAME, DATA_TYPE, NULLABLE, COLUMN_ID
            FROM ALL_TAB_COLUMNS
            WHERE OWNER = :p_schema AND TABLE_NAME = :p_table
            ORDER BY COLUMN_ID";

        var primaryKeyColumns = (await GetPrimaryKeyColumnsAsync(connection, schema, table, ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var identityColumns = (await GetIdentityColumnsAsync(connection, schema, table, ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return await QueryAsync(
            connection,
            sql,
            new[] { ("p_schema", (object)schema), ("p_table", (object)table) },
            r =>
            {
                var name = r.GetString(0);
                return new ColumnInfo
                {
                    Name = name,
                    DataType = r.GetString(1),
                    Nullable = string.Equals(r.GetString(2), "Y", StringComparison.OrdinalIgnoreCase),
                    OrdinalPosition = Convert.ToInt32(r.GetValue(3)),
                    IsPrimaryKey = primaryKeyColumns.Contains(name),
                    IsIdentity = identityColumns.Contains(name)
                };
            },
            ct);
    }

    /// <summary>Colunas GENERATED ... AS IDENTITY (só existe a partir do Oracle 12c) — ficam de fora do
    /// INSERT montado pela grid. ALL_TAB_IDENTITY_COLS não existe em versões mais antigas; nesse caso
    /// simplesmente não há colunas identity pra excluir (comportamento de antes desta checagem).</summary>
    private static async Task<IReadOnlyList<string>> GetIdentityColumnsAsync(DbConnection connection, string schema, string table, CancellationToken ct)
    {
        const string sql = @"
            SELECT COLUMN_NAME
            FROM ALL_TAB_IDENTITY_COLS
            WHERE OWNER = :p_schema AND TABLE_NAME = :p_table";
        try
        {
            return await QueryAsync(
                connection, sql,
                new[] { ("p_schema", (object)schema), ("p_table", (object)table) },
                r => r.GetString(0),
                ct);
        }
        catch (OracleException)
        {
            return Array.Empty<string>();
        }
    }

    public async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT cols.COLUMN_NAME
            FROM ALL_CONSTRAINTS cons
            JOIN ALL_CONS_COLUMNS cols
              ON cons.OWNER = cols.OWNER AND cons.CONSTRAINT_NAME = cols.CONSTRAINT_NAME
            WHERE cons.CONSTRAINT_TYPE = 'P' AND cons.OWNER = :p_schema AND cons.TABLE_NAME = :p_table
            ORDER BY cols.POSITION";

        return await QueryAsync(
            connection,
            sql,
            new[] { ("p_schema", (object)schema), ("p_table", (object)table) },
            r => r.GetString(0),
            ct);
    }

    public async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT ic.INDEX_NAME, ic.COLUMN_NAME, ind.UNIQUENESS
            FROM ALL_IND_COLUMNS ic
            JOIN ALL_INDEXES ind
              ON ind.OWNER = ic.INDEX_OWNER AND ind.INDEX_NAME = ic.INDEX_NAME
            WHERE ic.TABLE_OWNER = :p_schema AND ic.TABLE_NAME = :p_table
            ORDER BY ic.INDEX_NAME, ic.COLUMN_POSITION";

        var rows = await QueryAsync(
            connection,
            sql,
            new[] { ("p_schema", (object)schema), ("p_table", (object)table) },
            r => (IndexName: r.GetString(0), ColumnName: r.GetString(1), Uniqueness: r.GetString(2)),
            ct);

        return rows
            .GroupBy(r => r.IndexName)
            .Select(g => new IndexInfo
            {
                Name = g.Key,
                IsUnique = string.Equals(g.First().Uniqueness, "UNIQUE", StringComparison.OrdinalIgnoreCase),
                Columns = g.Select(r => r.ColumnName).ToList()
            })
            .ToList();
    }

    public async Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(DbConnection connection, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT a.CONSTRAINT_NAME, a.COLUMN_NAME, c_pk.OWNER AS REF_SCHEMA, c_pk.TABLE_NAME AS REF_TABLE, b.COLUMN_NAME AS REF_COLUMN
            FROM ALL_CONS_COLUMNS a
            JOIN ALL_CONSTRAINTS c
              ON a.OWNER = c.OWNER AND a.CONSTRAINT_NAME = c.CONSTRAINT_NAME
            JOIN ALL_CONSTRAINTS c_pk
              ON c.R_OWNER = c_pk.OWNER AND c.R_CONSTRAINT_NAME = c_pk.CONSTRAINT_NAME
            JOIN ALL_CONS_COLUMNS b
              ON c_pk.OWNER = b.OWNER AND c_pk.CONSTRAINT_NAME = b.CONSTRAINT_NAME AND a.POSITION = b.POSITION
            WHERE c.CONSTRAINT_TYPE = 'R' AND a.OWNER = :p_schema AND a.TABLE_NAME = :p_table
            ORDER BY a.CONSTRAINT_NAME, a.POSITION";

        return await QueryAsync(
            connection,
            sql,
            new[] { ("p_schema", (object)schema), ("p_table", (object)table) },
            r => new ForeignKeyInfo
            {
                Name = r.GetString(0),
                Column = r.GetString(1),
                ReferencedSchema = r.GetString(2),
                ReferencedTable = r.GetString(3),
                ReferencedColumn = r.GetString(4)
            },
            ct);
    }

    public string BuildSelectAllSql(string? database, string schema, string table, int maxRows)
        => $"SELECT * FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(table)} FETCH FIRST {maxRows} ROWS ONLY";

    public string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string ParameterToken(int index) => $":p{index}";

    // ODP.NET (Oracle.ManagedDataAccess) não aceita ":" em ParameterName mesmo com BindByName = true.
    public string ParameterName(int index) => $"p{index}";

    /// <summary>
    /// Executa uma query parametrizada em ADO.NET puro (sem passar pelo binding dinâmico do Dapper),
    /// forçando OracleCommand.BindByName = true — o Oracle.ManagedDataAccess usa binding posicional por
    /// padrão, e o caminho via Dapper (IDynamicParameters) se mostrou inconsistente ao repetir a mesma
    /// query várias vezes na mesma conexão (autocomplete testando vários schemas), causando ORA-01745.
    /// </summary>
    private static async Task<List<T>> QueryAsync<T>(
        DbConnection connection,
        string sql,
        (string Name, object Value)[] parameters,
        Func<DbDataReader, T> map,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (command is OracleCommand oracleCommand)
        {
            oracleCommand.BindByName = true;
        }

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        var results = new List<T>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(map(reader));
        }

        return results;
    }
}
