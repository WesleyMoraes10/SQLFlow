using System.Data.Common;
using System.Diagnostics;
using System.Text.RegularExpressions;
using SQLFlow.Data.Abstractions;
using SQLFlow.Data.Models;
using Oracle.ManagedDataAccess.Client;

namespace SQLFlow.Data.Services;

/// <summary>
/// Executa SQL por aba. Em auto-commit (padrão) cada comando abre uma conexão nova, roda e fecha —
/// confirmando sozinho, igual antes. Em modo manual, a aba ganha uma conexão+transação persistente
/// (uma "sessão") que fica aberta entre execuções até o usuário confirmar (Commit) ou desfazer
/// (Rollback) — só aí a conexão é liberada. Isso é por aba, não por conexão: duas abas na mesma
/// conexão podem ter transações independentes, cada uma com sua própria conexão física.
/// </summary>
public class QueryExecutionService : IDisposable
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ICredentialStore _credentialStore;
    private readonly Dictionary<string, Session> _sessions = new();

    public QueryExecutionService(IDbConnectionFactory connectionFactory, ICredentialStore credentialStore)
    {
        _connectionFactory = connectionFactory;
        _credentialStore = credentialStore;
    }

    public bool HasPendingTransaction(string tabId) => _sessions.ContainsKey(tabId);

    public int PendingStatementCount(string tabId) => _sessions.TryGetValue(tabId, out var s) ? s.StatementCount : 0;

    public async Task<QueryResult> ExecuteAsync(string tabId, ConnectionProfile profile, string sql, int maxRows, bool autoCommit, CancellationToken ct = default)
    {
        if (autoCommit)
        {
            var password = await _credentialStore.GetPasswordAsync(profile.Id)
                ?? throw new InvalidOperationException($"Nenhuma senha salva para a conexão '{profile.Name}'.");

            using var connection = _connectionFactory.CreateConnection(profile, password);
            await connection.OpenAsync(ct);
            return await RunAsync(connection, transaction: null, sql, maxRows, ct);
        }

        var session = await GetOrCreateSessionAsync(tabId, profile, ct);
        var result = await RunAsync(session.Connection, session.Transaction, sql, maxRows, ct);
        // SELECT não conta como "pendente": ele já roda dentro da transação (pra ver dados ainda não
        // confirmados), mas não é algo que precise de Commit/Rollback — só o que altera dado conta.
        if (ContainsModifyingStatement(sql))
        {
            session.StatementCount++;
        }
        return result;
    }

    /// <summary>Roda vários comandos parametrizados em sequência (usado pelo "Salvar" da grid editável —
    /// um UPDATE/DELETE por linha alterada, um INSERT por linha nova). Em auto-commit, o lote inteiro
    /// roda numa transação própria e temporária (tudo ou nada); em modo manual, entra na mesma
    /// sessão/transação da aba, junto com o que já estiver pendente, só liberado no Commit/Rollback
    /// explícito. <paramref name="statements"/> pode informar, por comando, uma <c>IdentityQuery</c>
    /// (ver <see cref="IMetadataProvider.LastIdentityQuery"/>) pra ler de volta o valor gerado numa coluna
    /// identity logo após aquele INSERT — o valor lido (ou null se não pedido) volta em GeneratedIds, na
    /// mesma ordem dos comandos que pediram.</summary>
    public async Task<(int Affected, IReadOnlyList<object?> GeneratedIds)> ExecuteBatchAsync(
        string tabId,
        ConnectionProfile profile,
        IReadOnlyList<(string Sql, Dictionary<string, object?> Parameters, string? IdentityQuery)> statements,
        bool autoCommit,
        CancellationToken ct = default)
    {
        if (statements.Count == 0) return (0, Array.Empty<object?>());

        var generatedIds = new List<object?>();

        if (!autoCommit)
        {
            var session = await GetOrCreateSessionAsync(tabId, profile, ct);
            var total = 0;
            foreach (var (sql, parameters, identityQuery) in statements)
            {
                var (affected, generatedId) = await RunNonQueryAsync(session.Connection, session.Transaction, sql, parameters, identityQuery, ct);
                total += affected;
                if (identityQuery is not null) generatedIds.Add(generatedId);
            }
            session.StatementCount += statements.Count;
            return (total, generatedIds);
        }

        var password = await _credentialStore.GetPasswordAsync(profile.Id)
            ?? throw new InvalidOperationException($"Nenhuma senha salva para a conexão '{profile.Name}'.");
        using var connection = _connectionFactory.CreateConnection(profile, password);
        await connection.OpenAsync(ct);
        using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            var total = 0;
            foreach (var (sql, parameters, identityQuery) in statements)
            {
                var (affected, generatedId) = await RunNonQueryAsync(connection, transaction, sql, parameters, identityQuery, ct);
                total += affected;
                if (identityQuery is not null) generatedIds.Add(generatedId);
            }
            await transaction.CommitAsync(ct);
            return (total, generatedIds);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task<(int Affected, object? GeneratedId)> RunNonQueryAsync(
        DbConnection connection, DbTransaction? transaction, string sql, Dictionary<string, object?> parameters, string? identityQuery, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        if (command is OracleCommand oracleCommand)
        {
            oracleCommand.BindByName = true;
        }

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var affected = await command.ExecuteNonQueryAsync(ct);
        if (identityQuery is null) return (affected, null);

        // Mesma conexão/transação, logo em seguida: SCOPE_IDENTITY()/LAST_INSERT_ID() são escopados por
        // sessão, então isso só reflete o INSERT que acabou de rodar, mesmo com outros comandos no lote.
        using var idCommand = connection.CreateCommand();
        idCommand.CommandText = identityQuery;
        idCommand.Transaction = transaction;
        var generatedId = await idCommand.ExecuteScalarAsync(ct);
        return (affected, generatedId is DBNull ? null : generatedId);
    }

    /// <summary>Se o texto (podendo ter vários comandos separados por ";") contém algum INSERT/UPDATE/
    /// DELETE/TRUNCATE — usado pra decidir se pede confirmação antes de rodar. Um único comando com
    /// vários registros afetados ainda conta como "um" pra esse fim (confirma-se a execução, não linha
    /// por linha).</summary>
    public static bool ContainsModifyingStatement(string sql)
        => sql.Split(';').Any(part =>
        {
            var trimmed = part.TrimStart();
            return trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("TRUNCATE", StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>Heurística (não é parser SQL completo — só olha se aparece a palavra "WHERE" no
    /// texto do comando): UPDATE/DELETE sem WHERE afeta a tabela inteira. INSERT não tem WHERE e
    /// TRUNCATE não aceita WHERE, então nenhum dos dois entra nessa checagem.</summary>
    public static bool ContainsUnboundedUpdateOrDelete(string sql)
        => sql.Split(';').Any(part =>
        {
            var trimmed = part.TrimStart();
            var isUpdateOrDelete = trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase);
            return isUpdateOrDelete && !Regex.IsMatch(trimmed, @"\bWHERE\b", RegexOptions.IgnoreCase);
        });

    public async Task CommitAsync(string tabId)
    {
        if (!_sessions.Remove(tabId, out var session)) return;
        try
        {
            await session.Transaction.CommitAsync();
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    public async Task RollbackAsync(string tabId)
    {
        if (!_sessions.Remove(tabId, out var session)) return;
        try
        {
            await session.Transaction.RollbackAsync();
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    /// <summary>Chamado ao fechar a aba: desfaz silenciosamente qualquer transação pendente pra não
    /// deixar locks presos no banco por causa de uma aba fechada sem Commit/Rollback explícito.</summary>
    public async Task DiscardSessionAsync(string tabId)
    {
        if (!_sessions.Remove(tabId, out var session)) return;
        try
        {
            await session.Transaction.RollbackAsync();
        }
        catch
        {
            // conexão pode já ter caído — só garante que a sessão não fica presa em memória
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private async Task<Session> GetOrCreateSessionAsync(string tabId, ConnectionProfile profile, CancellationToken ct)
    {
        if (_sessions.TryGetValue(tabId, out var existing)) return existing;

        var password = await _credentialStore.GetPasswordAsync(profile.Id)
            ?? throw new InvalidOperationException($"Nenhuma senha salva para a conexão '{profile.Name}'.");

        var connection = _connectionFactory.CreateConnection(profile, password);
        await connection.OpenAsync(ct);
        var transaction = await connection.BeginTransactionAsync(ct);

        var session = new Session(connection, transaction);
        _sessions[tabId] = session;
        return session;
    }

    private static async Task<QueryResult> RunAsync(DbConnection connection, DbTransaction? transaction, string sql, int maxRows, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        if (IsResultSetQuery(sql))
        {
            using var reader = await command.ExecuteReaderAsync(ct);

            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            // Tipo nativo do banco (ex.: VARCHAR2(50), NUMBER(10,2), datetime) pra exibir no cabeçalho
            // da grid — não o tipo CLR do Dapper, que perde essa distinção (NUMBER e NUMBER(1) viram
            // ambos decimal). GetColumnSchema() dá precisão/tamanho; nem todo provider suporta, por
            // isso o CanGetColumnSchema() antes.
            var columnSchema = reader.CanGetColumnSchema() ? reader.GetColumnSchema() : null;
            var columnTypes = new Dictionary<string, string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var schemaColumn = columnSchema is not null && i < columnSchema.Count ? columnSchema[i] : null;
                columnTypes[columns[i]] = FormatColumnType(reader.GetDataTypeName(i), schemaColumn);
            }
            var rows = new List<Dictionary<string, object?>>();

            while (rows.Count < maxRows && await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    row[columns[i]] = value is DBNull ? null : value;
                }
                rows.Add(row);
            }

            stopwatch.Stop();
            return new QueryResult
            {
                IsResultSet = true,
                Columns = columns,
                ColumnTypes = columnTypes,
                Rows = rows,
                Elapsed = stopwatch.Elapsed
            };
        }
        else
        {
            var affected = await command.ExecuteNonQueryAsync(ct);
            stopwatch.Stop();
            return new QueryResult
            {
                IsResultSet = false,
                AffectedRows = affected,
                Elapsed = stopwatch.Elapsed
            };
        }
    }

    /// <summary>Ex.: "Number" + precisão 10/escala 2 -> "Number(10,2)"; "Varchar2" + tamanho 50 ->
    /// "Varchar2(50)". Precisão tem prioridade sobre tamanho porque em tipos numéricos o ColumnSize
    /// do ADO.NET costuma refletir o tamanho de armazenamento em bytes, não os dígitos do NUMBER(p,s).</summary>
    private static string FormatColumnType(string typeName, DbColumn? column)
    {
        var precision = column?.NumericPrecision;
        if (precision is > 0)
        {
            var scale = column?.NumericScale;
            return scale is > 0 ? $"{typeName}({precision},{scale})" : $"{typeName}({precision})";
        }

        var size = column?.ColumnSize;
        if (size is > 0 and < int.MaxValue)
        {
            return $"{typeName}({size})";
        }

        return typeName;
    }

    private static bool IsResultSetQuery(string sql)
    {
        var trimmed = sql.TrimStart();
        return trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            try { session.Transaction.Rollback(); } catch { /* melhor esforço no shutdown */ }
            session.Connection.Dispose();
        }
        _sessions.Clear();
    }

    private sealed class Session : IAsyncDisposable
    {
        public Session(DbConnection connection, DbTransaction transaction)
        {
            Connection = connection;
            Transaction = transaction;
        }

        public DbConnection Connection { get; }
        public DbTransaction Transaction { get; }
        public int StatementCount { get; set; }

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
