using System.Data.Common;
using DevelopTi.Data.Models;

namespace DevelopTi.Data.Abstractions;

/// <summary>
/// Leitura de metadados (schemas, tabelas, colunas, PK) — uma implementação concreta por SGBD,
/// já que cada banco expõe seus metadados através de catálogos diferentes.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>Se true, a árvore mostra um nível "Banco de dados" acima dos schemas (ex: SQL Server,
    /// onde uma mesma conexão/instância enxerga vários bancos). Se false, a conexão já aponta pra um
    /// único banco/serviço fixo e os schemas aparecem direto (Oracle, MySQL).</summary>
    bool SupportsDatabaseBrowsing { get; }

    /// <summary>Lista os bancos visíveis na instância (só chamado quando <see cref="SupportsDatabaseBrowsing"/> é true).</summary>
    Task<IReadOnlyList<string>> GetDatabasesAsync(DbConnection connection, CancellationToken ct = default);

    Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(DbConnection connection, CancellationToken ct = default);

    Task<IReadOnlyList<TableInfo>> GetTablesAsync(DbConnection connection, string schema, CancellationToken ct = default);

    /// <summary>Lista tabelas/views de todos os schemas visíveis numa única consulta (usado pelo autocomplete — evita 1 round-trip por schema).</summary>
    Task<IReadOnlyList<TableInfo>> GetAllTablesAsync(DbConnection connection, CancellationToken ct = default);

    /// <summary>Lista em quais schemas visíveis existe uma tabela/view com este nome (filtro no próprio
    /// SQL, ao contrário de <see cref="GetAllTablesAsync"/> — usado quando só precisamos localizar o
    /// schema de UMA tabela, ex: SQL sem schema qualificado no FROM).</summary>
    Task<IReadOnlyList<string>> FindTableSchemasAsync(DbConnection connection, string tableName, CancellationToken ct = default);

    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DbConnection connection, string schema, string table, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(DbConnection connection, string schema, string table, CancellationToken ct = default);

    Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(DbConnection connection, string schema, string table, CancellationToken ct = default);

    Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(DbConnection connection, string schema, string table, CancellationToken ct = default);

    /// <summary>Monta um SELECT * com limite de linhas usando a sintaxe própria do banco (FETCH FIRST/TOP/LIMIT).
    /// <paramref name="database"/> é opcional: quando informado (SQL Server), qualifica com 3 partes
    /// pra funcionar independente do banco padrão da conexão.</summary>
    string BuildSelectAllSql(string? database, string schema, string table, int maxRows);

    /// <summary>Envolve um identificador (schema/tabela/coluna) com o delimitador de identificador do banco.</summary>
    string QuoteIdentifier(string identifier);

    /// <summary>Placeholder do parâmetro dentro do texto do SQL (ex: "@p0" no SQL Server/MySQL, ":p0" no Oracle).</summary>
    string ParameterToken(int index);

    /// <summary>Nome usado em DbParameter.ParameterName pro mesmo parâmetro — o Oracle não aceita o
    /// prefixo ":" aqui mesmo usando ":nome" no texto do SQL, por isso é separado do token acima.</summary>
    string ParameterName(int index);
}
