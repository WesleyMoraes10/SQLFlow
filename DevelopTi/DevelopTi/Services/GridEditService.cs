using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DevelopTi.Data.Abstractions;
using DevelopTi.Data.Models;
using DevelopTi.Data.Services;
using ConnectionProfile = DevelopTi.Data.Models.ConnectionProfile;

namespace DevelopTi.Services;

/// <summary>Resultado de <see cref="GridEditService.ResolveEditContextAsync"/> — quando a grid não pode
/// ficar editável, <see cref="Reason"/> explica o motivo exato em vez de simplesmente ficar só-leitura
/// sem explicação nenhuma.</summary>
public record GridEditResolution(GridEditContext? Context, string? Reason);

/// <summary>
/// Decide se o resultado de um SELECT pode virar uma grid editável e aplica as edições feitas nela.
/// Só habilita edição pra SELECT de uma única tabela (sem JOIN). O SQL pode vir sem schema (ex: "FROM
/// Z8R010") — nesse caso procura em qual schema visível a tabela existe; se aparecer em mais de um,
/// pede pra qualificar. Pra identificar a linha no WHERE, tenta nessa ordem: chave primária → índice
/// único → todas as colunas da tabela (comum em ERPs sem PK formal, ex: TOTVS/Protheus — ver
/// <see cref="GridEditContext.IsSyntheticKey"/>). Só fica só-leitura se nem isso resolver (tabela sem
/// nenhuma coluna legível, sem acesso, etc.).
/// </summary>
public class GridEditService
{
    private static readonly Regex FromJoinKeyword = new(@"\b(FROM|JOIN)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FromTarget = new(@"\bFROM\s+([A-Za-z0-9_.\[\]""`]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ICredentialStore _credentialStore;
    private readonly IMetadataProviderFactory _providerFactory;
    private readonly QueryExecutionService _executionService;

    // GridEditService é singleton — cacheia a resolução por conexão+SQL pra não repetir o round-trip de
    // metadados toda vez que a edição é ligada/desligada na mesma aba (ou em abas com o mesmo SELECT).
    private readonly ConcurrentDictionary<(string ProfileId, string Sql), GridEditResolution> _resolutionCache = new();

    public GridEditService(
        IDbConnectionFactory connectionFactory,
        ICredentialStore credentialStore,
        IMetadataProviderFactory providerFactory,
        QueryExecutionService executionService)
    {
        _connectionFactory = connectionFactory;
        _credentialStore = credentialStore;
        _providerFactory = providerFactory;
        _executionService = executionService;
    }

    public async Task<GridEditResolution> ResolveEditContextAsync(ConnectionProfile profile, string sql)
    {
        var cacheKey = (profile.Id, sql);
        if (_resolutionCache.TryGetValue(cacheKey, out var cached)) return cached;

        var resolution = await ResolveEditContextCoreAsync(profile, sql);

        // Só cacheia resolução com sucesso: as recusas (JOIN, tabela ambígua, sem senha etc.) são
        // baratas de recalcular e podem deixar de se aplicar (ex: usuário corrige o SQL).
        if (resolution.Context is not null) _resolutionCache[cacheKey] = resolution;
        return resolution;
    }

    private async Task<GridEditResolution> ResolveEditContextCoreAsync(ConnectionProfile profile, string sql)
    {
        var fromJoinCount = FromJoinKeyword.Matches(sql).Count;
        if (fromJoinCount == 0) return new GridEditResolution(null, "Não achei nenhum FROM na consulta.");
        if (fromJoinCount > 1) return new GridEditResolution(null, "Consulta usa mais de uma tabela (JOIN) — só dá pra editar SELECT de uma tabela só.");

        var match = FromTarget.Match(sql);
        if (!match.Success) return new GridEditResolution(null, "Não consegui identificar a tabela depois do FROM.");

        var parts = match.Groups[1].Value
            .Split('.')
            .Select(p => p.Trim('"', '[', ']', '`'))
            .ToArray();

        var provider = _providerFactory.GetProvider(profile.Kind);
        var password = await _credentialStore.GetPasswordAsync(profile.Id);
        if (password is null) return new GridEditResolution(null, "Senha não encontrada para esta conexão.");

        string? database = null;
        string schema = string.Empty;
        string table = string.Empty;

        try
        {
            using var connection = _connectionFactory.CreateConnection(profile, password);
            await connection.OpenAsync();

            if (parts.Length >= 3 && provider.SupportsDatabaseBrowsing)
            {
                database = parts[^3];
                schema = parts[^2];
                table = parts[^1];
                connection.ChangeDatabase(database);
            }
            else if (parts.Length == 2)
            {
                schema = parts[0];
                table = parts[1];
            }
            else
            {
                // Sem schema — muito comum digitar assim, principalmente no Oracle, onde o "dono" da
                // sessão resolve sozinho. Procura em qual(is) schema(s) visível(eis) a tabela existe —
                // filtrado no próprio SQL (FindTableSchemasAsync), não um scan de todo o dicionário
                // de dados (GetAllTablesAsync), que é caro demais só pra essa checagem.
                table = parts[0];
                var matches = (await provider.FindTableSchemasAsync(connection, table))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (matches.Count == 0)
                {
                    return new GridEditResolution(null, $"Tabela \"{table}\" não encontrada em nenhum schema visível.");
                }
                if (matches.Count > 1)
                {
                    return new GridEditResolution(null,
                        $"Tabela \"{table}\" existe em mais de um schema ({string.Join(", ", matches)}) — qualifique (ex: {matches[0]}.{table}) pra eu saber qual usar.");
                }
                schema = matches[0];
            }

            var keyColumns = await provider.GetPrimaryKeyColumnsAsync(connection, schema, table);
            var isSynthetic = false;

            if (keyColumns.Count == 0)
            {
                // Comum em ERPs (ex: tabelas TOTVS/Protheus) não terem PK formal cadastrada — tenta um
                // índice único como substituto antes de desistir.
                var indexes = await provider.GetIndexesAsync(connection, schema, table);
                var uniqueIndex = indexes.FirstOrDefault(i => i.IsUnique && i.Columns.Count > 0);
                if (uniqueIndex is not null)
                {
                    keyColumns = uniqueIndex.Columns;
                }
            }

            if (keyColumns.Count == 0)
            {
                // Último recurso: sem PK nem índice único, usa todas as colunas da tabela como
                // identificador da linha (igual o DBeaver faz) — arriscado com linhas duplicadas de
                // verdade, mas é o único jeito de habilitar edição nessas tabelas.
                var allColumns = await provider.GetColumnsAsync(connection, schema, table);
                if (allColumns.Count == 0)
                {
                    return new GridEditResolution(null, $"Não consegui ler as colunas de {schema}.{table} pra montar uma chave.");
                }
                keyColumns = allColumns.Select(c => c.Name).ToList();
                isSynthetic = true;
            }

            var context = new GridEditContext
            {
                Database = database,
                Schema = schema,
                Table = table,
                PrimaryKeyColumns = keyColumns,
                IsSyntheticKey = isSynthetic
            };
            return new GridEditResolution(context, null);
        }
        catch (Exception ex)
        {
            return new GridEditResolution(null, $"Falha ao resolver {schema}.{table}: {ex.Message}");
        }
    }

    /// <summary>Monta e roda um UPDATE por linha editada e um DELETE por linha marcada pra excluir.
    /// Exclusão tem prioridade: se uma linha está marcada pra excluir, edições pendentes nela são ignoradas.</summary>
    public async Task<int> SaveChangesAsync(
        string tabId,
        ConnectionProfile profile,
        GridEditContext context,
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyDictionary<int, Dictionary<string, object?>> pendingEdits,
        IReadOnlyCollection<int> pendingDeletes,
        bool autoCommit)
    {
        var provider = _providerFactory.GetProvider(profile.Kind);
        var statements = new List<(string Sql, Dictionary<string, object?> Parameters)>();

        foreach (var rowIndex in pendingDeletes)
        {
            statements.Add(GridChangeSqlBuilder.BuildDelete(provider, context, rows[rowIndex]));
        }

        foreach (var (rowIndex, changes) in pendingEdits)
        {
            if (pendingDeletes.Contains(rowIndex) || changes.Count == 0) continue;
            statements.Add(GridChangeSqlBuilder.BuildUpdate(provider, context, rows[rowIndex], changes));
        }

        return await _executionService.ExecuteBatchAsync(tabId, profile, statements, autoCommit);
    }
}
