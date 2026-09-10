using GridEditContext = DevelopTi.Data.Models.GridEditContext;
using QueryResult = DevelopTi.Data.Models.QueryResult;

namespace DevelopTi.Services;

public class QueryTabModel
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Nova consulta";

    /// <summary>Perfil de conexão ao qual esta aba está vinculada (cada aba pode apontar pra uma conexão diferente).</summary>
    public string ConnectionProfileId { get; set; } = string.Empty;

    /// <summary>SQL usado só para popular o model do Monaco na primeira renderização da aba.</summary>
    public string InitialSql { get; set; } = string.Empty;

    /// <summary>Texto exato da última execução — usado pelo botão "Editar no grid" pra resolver a
    /// tabela/chave sob demanda, em vez de fazer isso (com round-trip ao banco) depois de todo SELECT.</summary>
    public string LastExecutedSql { get; set; } = string.Empty;

    public int MaxRows { get; set; } = 200;
    public bool ModelReady { get; set; }
    public bool Running { get; set; }
    public string? Error { get; set; }

    /// <summary>Linha (1-based, dentro de LastExecutedSql) onde o banco apontou o erro — só o SQL
    /// Server informa isso de verdade (SqlException.LineNumber); fica nulo nos outros bancos.</summary>
    public int? ErrorLine { get; set; }

    public QueryResult? Result { get; set; }

    /// <summary>Se false (padrão), os comandos rodados nesta aba ficam numa transação aberta até
    /// Commit/Rollback, em vez de confirmar sozinhos a cada execução.</summary>
    public bool AutoCommit { get; set; } = false;

    /// <summary>Não nulo quando o último SELECT rodado habilita edição direta na grid de resultado.
    /// Recalculado (e as pendências abaixo limpas) a cada execução.</summary>
    public GridEditContext? EditContext { get; set; }

    /// <summary>Quando EditContext é nulo depois de um SELECT, explica o motivo (mostrado na UI) em vez
    /// de simplesmente deixar a grid só-leitura sem dizer por quê.</summary>
    public string? EditDisabledReason { get; set; }

    /// <summary>Edições pendentes por linha (índice em Result.Rows) → coluna → novo valor, ainda não salvas.</summary>
    public Dictionary<int, Dictionary<string, object?>> PendingEdits { get; } = new();

    /// <summary>Linhas (índice em Result.Rows) marcadas pra excluir, ainda não salvas.</summary>
    public HashSet<int> PendingDeletes { get; } = new();

    public void ClearGridEdits()
    {
        EditContext = null;
        EditDisabledReason = null;
        PendingEdits.Clear();
        PendingDeletes.Clear();
    }

    /// <summary>Resultados de uma execução simultânea (até 3 SELECTs selecionados de uma vez no
    /// editor) — populada por ExecuteMultipleAsync em QueryTabsPanel, mostrada lado a lado em vez do
    /// resultado único de Result. Sem suporte a edição de grid (só leitura), diferente do fluxo normal.</summary>
    public List<ParallelRunResult> ParallelResults { get; } = new();
}

/// <summary>Um dos resultados de uma execução simultânea — ver QueryTabModel.ParallelResults.</summary>
public class ParallelRunResult
{
    public required string Sql { get; init; }
    public bool Running { get; set; } = true;
    public QueryResult? Result { get; set; }
    public string? Error { get; set; }
}

/// <summary>Guarda as abas de consulta abertas e qual está ativa — sobrevive à navegação entre páginas.</summary>
public class QueryTabState
{
    public List<QueryTabModel> Tabs { get; } = new();
    public string? ActiveTabId { get; private set; }

    public event Action? Changed;

    public QueryTabModel AddTab(string connectionProfileId, string title, string initialSql = "")
    {
        var tab = new QueryTabModel { ConnectionProfileId = connectionProfileId, Title = title, InitialSql = initialSql };
        Tabs.Add(tab);
        ActiveTabId = tab.Id;
        Changed?.Invoke();
        return tab;
    }

    public void SetActive(string id)
    {
        if (Tabs.Any(t => t.Id == id))
        {
            ActiveTabId = id;
            Changed?.Invoke();
        }
    }

    public void CloseTab(string id)
    {
        var index = Tabs.FindIndex(t => t.Id == id);
        if (index < 0) return;

        Tabs.RemoveAt(index);

        if (ActiveTabId == id)
        {
            ActiveTabId = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)].Id;
        }

        Changed?.Invoke();
    }

    public QueryTabModel? GetActive() => Tabs.FirstOrDefault(t => t.Id == ActiveTabId);

    public void NotifyChanged() => Changed?.Invoke();
}
