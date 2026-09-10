using DevelopTi.Data.Abstractions;
using DevelopTi.Data.Models;
using Microsoft.JSInterop;
using ConnectionProfile = DevelopTi.Data.Models.ConnectionProfile;

namespace DevelopTi.Services;

public record CompletionColumn(string Name, string DataType, bool IsPrimaryKey);

/// <summary>Sugestão pra depois de FROM/JOIN — Kind é "database", "schema" ou "table" (o JS mapeia pro ícone certo).</summary>
public record CompletionEntry(string Label, string Kind);

/// <summary>
/// Ponte JS-interop para o autocomplete do Monaco: sugere banco/schema/tabela depois de FROM/JOIN
/// (suportando nomes qualificados tipo "PCF4.dbo.CTBLEtiqueta") e, dado um alias usado no SQL (ex.:
/// "FROM SD3010 D" -> "D"), devolve as colunas da tabela correspondente. Cada aba de consulta pode
/// apontar pra uma conexão diferente, então o id da conexão vem explicitamente do JS (que rastreia
/// qual aba está ativa no Monaco).
///
/// O usuário logado nem sempre é o dono do schema das tabelas (comum em ERPs) — por isso, em vez de
/// assumir um schema "padrão", indexamos as tabelas de todos os schemas visíveis na primeira consulta
/// e resolvemos o nome digitado por busca nesse índice (cacheado por conexão/banco).
/// </summary>
public class SqlCompletionService
{
    private readonly IConnectionProfileStore _profileStore;
    private readonly IMetadataProviderFactory _providerFactory;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ICredentialStore _credentialStore;

    // Chave: profile.Id (banco padrão da conexão) ou "{profile.Id}|{database}" (SQL Server, banco explícito).
    private readonly Dictionary<string, List<TableInfo>> _tableIndexCache = new();
    private readonly Dictionary<string, List<string>> _databaseIndexCache = new();
    private readonly Dictionary<string, CompletionColumn[]> _columnCache = new();

    public SqlCompletionService(
        IConnectionProfileStore profileStore,
        IMetadataProviderFactory providerFactory,
        IDbConnectionFactory connectionFactory,
        ICredentialStore credentialStore)
    {
        _profileStore = profileStore;
        _providerFactory = providerFactory;
        _connectionFactory = connectionFactory;
        _credentialStore = credentialStore;
    }

    // O serviço é singleton (sobrevive a recriações do BlazorWebView no F5/hot-restart), mas o
    // DotNetObjectReference só pode ser rastreado por um JSRuntime por vez — por isso não é
    // cacheado aqui. Quem consome (QueryTabsPanel) cria e descarta a própria referência a cada
    // sessão do webview, no callsite do JS interop.
    public DotNetObjectReference<SqlCompletionService> CreateReference() => DotNetObjectReference.Create(this);

    /// <summary>
    /// Sugestões pra depois de FROM/JOIN. <paramref name="segments"/> é o texto já digitado dividido
    /// por ".", com o último elemento sendo o prefixo ainda sendo digitado (pode ser vazio):
    /// ["CTBL"] -> tabela sem qualificar; ["PCF4", ""] -> banco.<prefixo de schema>;
    /// ["PCF4", "dbo", "CTBL"] -> banco.schema.<prefixo de tabela>.
    /// </summary>
    [JSInvokable]
    public async Task<CompletionEntry[]> GetFromCompletionsAsync(string connectionProfileId, string[] segments)
    {
        var profile = await _profileStore.GetByIdAsync(connectionProfileId);
        if (profile is null || segments.Length == 0) return Array.Empty<CompletionEntry>();

        var prefix = segments[^1];
        var qualifiers = segments[..^1];
        var provider = _providerFactory.GetProvider(profile.Kind);

        try
        {
            switch (qualifiers.Length)
            {
                case 0:
                {
                    var entries = new List<CompletionEntry>();
                    if (provider.SupportsDatabaseBrowsing)
                    {
                        var databases = await GetOrLoadDatabasesAsync(profile, provider);
                        entries.AddRange(databases
                            .Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            .Select(d => new CompletionEntry(d, "database")));
                    }

                    var defaultTables = await GetOrLoadTableIndexAsync(profile, provider, database: null);
                    entries.AddRange(defaultTables
                        .Select(t => t.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .Select(n => new CompletionEntry(n, "table")));

                    return entries.OrderBy(e => e.Kind).ThenBy(e => e.Label).Take(50).ToArray();
                }

                case 1 when provider.SupportsDatabaseBrowsing:
                {
                    // banco.<prefixo> -> schemas dentro do banco.
                    var tables = await GetOrLoadTableIndexAsync(profile, provider, qualifiers[0]);
                    return tables
                        .Select(t => t.Schema)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(s => s)
                        .Select(s => new CompletionEntry(s, "schema"))
                        .Take(50)
                        .ToArray();
                }

                case 1:
                {
                    // schema.<prefixo> -> tabelas do schema (bancos sem múltiplos databases: Oracle/MySQL).
                    var tables = await GetOrLoadTableIndexAsync(profile, provider, database: null);
                    return FilterTablesBySchema(tables, qualifiers[0], prefix);
                }

                case 2 when provider.SupportsDatabaseBrowsing:
                {
                    // banco.schema.<prefixo> -> tabelas do schema dentro do banco.
                    var tables = await GetOrLoadTableIndexAsync(profile, provider, qualifiers[0]);
                    return FilterTablesBySchema(tables, qualifiers[1], prefix);
                }

                default:
                    return Array.Empty<CompletionEntry>();
            }
        }
        catch
        {
            return Array.Empty<CompletionEntry>();
        }
    }

    private static CompletionEntry[] FilterTablesBySchema(List<TableInfo> tables, string schema, string prefix)
        => tables
            .Where(t => string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n)
            .Select(n => new CompletionEntry(n, "table"))
            .Take(50)
            .ToArray();

    [JSInvokable]
    public async Task<CompletionColumn[]> GetColumnsAsync(string connectionProfileId, string tableNameRaw)
    {
        var profile = await _profileStore.GetByIdAsync(connectionProfileId);
        if (profile is null || string.IsNullOrWhiteSpace(tableNameRaw))
            return Array.Empty<CompletionColumn>();

        var cleaned = tableNameRaw.Trim();
        var cacheKey = $"{profile.Id}|{cleaned}".ToUpperInvariant();
        if (_columnCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var password = await _credentialStore.GetPasswordAsync(profile.Id)
            ?? throw new InvalidOperationException($"Senha não encontrada para a conexão '{profile.Name}'.");
        var provider = _providerFactory.GetProvider(profile.Kind);

        List<(string? Database, string Schema, string Table)> candidates;
        var explicitParts = cleaned.Split('.').Select(p => p.Trim('"', '[', ']', '`')).ToArray();
        if (explicitParts.Length >= 3)
        {
            // banco.schema.tabela (SQL Server) — usa só as 3 últimas partes.
            candidates = new List<(string?, string, string)>
            {
                (explicitParts[^3], explicitParts[^2], explicitParts[^1])
            };
        }
        else if (explicitParts.Length == 2)
        {
            candidates = new List<(string?, string, string)> { (null, explicitParts[0], explicitParts[1]) };
        }
        else
        {
            var tables = await GetOrLoadTableIndexAsync(profile, provider, database: null);
            // Em ERPs multiempresa a mesma tabela pode aparecer em vários schemas (uma cópia por
            // filial); a primeira pode não ter privilégio real de leitura de colunas mesmo aparecendo
            // no catálogo. Testa todos os schemas onde a tabela aparece até achar um com colunas.
            candidates = tables
                .Where(t => string.Equals(t.Name, cleaned, StringComparison.OrdinalIgnoreCase))
                .Select(t => ((string?)null, t.Schema, t.Name))
                .Distinct()
                .ToList();

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Tabela '{cleaned}' não encontrada no índice ({tables.Count} tabela(s) indexada(s) para esta conexão).");
            }
        }

        using var connection = _connectionFactory.CreateConnection(profile, password);
        await connection.OpenAsync();

        CompletionColumn[] result = Array.Empty<CompletionColumn>();
        foreach (var (database, schema, table) in candidates)
        {
            if (!string.IsNullOrEmpty(database))
            {
                try
                {
                    connection.ChangeDatabase(database);
                }
                catch
                {
                    continue; // banco inexistente/sem acesso — tenta o próximo candidato, se houver.
                }
            }

            var columns = await provider.GetColumnsAsync(connection, schema, table);
            if (columns.Count > 0)
            {
                result = columns.Select(c => new CompletionColumn(c.Name, c.DataType, c.IsPrimaryKey)).ToArray();
                break;
            }
        }

        _columnCache[cacheKey] = result;
        return result;
    }

    private async Task<List<string>> GetOrLoadDatabasesAsync(ConnectionProfile profile, IMetadataProvider provider)
    {
        if (_databaseIndexCache.TryGetValue(profile.Id, out var cached))
            return cached;

        var databases = new List<string>();
        var indexed = false;
        try
        {
            var password = await _credentialStore.GetPasswordAsync(profile.Id);
            if (password is not null)
            {
                using var connection = _connectionFactory.CreateConnection(profile, password);
                await connection.OpenAsync();
                databases.AddRange(await provider.GetDatabasesAsync(connection));
                indexed = true;
            }
        }
        catch
        {
            // Não cacheia falha — tenta de novo na próxima.
        }

        if (indexed)
        {
            _databaseIndexCache[profile.Id] = databases;
        }

        return databases;
    }

    private async Task<List<TableInfo>> GetOrLoadTableIndexAsync(ConnectionProfile profile, IMetadataProvider provider, string? database)
    {
        var cacheKey = string.IsNullOrEmpty(database) ? profile.Id : $"{profile.Id}|{database}";
        if (_tableIndexCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var tables = new List<TableInfo>();
        var indexed = false;
        try
        {
            var password = await _credentialStore.GetPasswordAsync(profile.Id);
            if (password is not null)
            {
                using var connection = _connectionFactory.CreateConnection(profile, password);
                await connection.OpenAsync();
                if (!string.IsNullOrEmpty(database))
                {
                    connection.ChangeDatabase(database);
                }

                // Uma única consulta cobrindo todos os schemas — evita um round-trip por schema,
                // que em instâncias com muitos schemas (comum em ERPs multiempresa) deixava o
                // autocomplete extremamente lento na primeira vez.
                tables.AddRange(await provider.GetAllTablesAsync(connection));
                indexed = true;
            }
        }
        catch
        {
            // Não cacheia: se a conexão falhou (ex: instância/porta/banco incorretos na hora, ou banco
            // digitado errado no SQL), a próxima tentativa de autocomplete deve reindexar em vez de
            // ficar presa numa lista vazia.
        }

        if (indexed)
        {
            _tableIndexCache[cacheKey] = tables;
        }

        return tables;
    }

}
