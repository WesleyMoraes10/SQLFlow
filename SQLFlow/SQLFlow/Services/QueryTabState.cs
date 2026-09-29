using GridEditContext = SQLFlow.Data.Models.GridEditContext;
using QueryResult = SQLFlow.Data.Models.QueryResult;

namespace SQLFlow.Services;

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

    /// <summary>True quando o texto do editor mudou desde o último salvamento — liga o alerta de
    /// "salvar antes de fechar" na aba (igual DBeaver). Setado pelo JS (Monaco onDidChangeContent) via
    /// MarkTabDirty, limpo depois de um salvamento bem-sucedido.</summary>
    public bool IsDirty { get; set; }

    /// <summary>Caminho do arquivo escolhido no primeiro "Salvar" desta aba. Uma vez preenchido, os
    /// salvamentos seguintes sobrescrevem esse arquivo direto, sem abrir o diálogo "Salvar como" de novo
    /// — mesmo comportamento do DBeaver, que mantém o editor aberto e associado ao arquivo depois de salvo.</summary>
    public string? SavedFilePath { get; set; }

    /// <summary>Linha (1-based, dentro de LastExecutedSql) onde o banco apontou o erro — só o SQL
    /// Server informa isso de verdade (SqlException.LineNumber); fica nulo nos outros bancos.</summary>
    public int? ErrorLine { get; set; }

    /// <summary>Linha (1-based, no editor Monaco inteiro) onde começa LastExecutedSql — capturada
    /// pelo JS (sqlflowMonaco.getExecutionStartLine) antes de rodar, já que quem roda pode ser só um
    /// trecho selecionado ou um statement empilhado no meio do editor, não necessariamente a partir da
    /// linha 1. Usada junto com ErrorLine pra sublinhar a linha certa no editor de verdade (ver
    /// sqlflowMonaco.setErrorMarker), não só na cópia estática mostrada no modal/painel de erro.</summary>
    public int ExecutionStartLine { get; set; } = 1;

    public QueryResult? Result { get; set; }

    /// <summary>Se false, os comandos rodados nesta aba ficam numa transação aberta até
    /// Commit/Rollback, em vez de confirmar sozinhos a cada execução. Padrão depende do banco (ver
    /// QueryTabState.AddTab): ligado pra SQLite, porque lá até um SELECT dentro de uma transação
    /// manual mantém lock no arquivo — uma aba esquecida sem Commit/Rollback já travou escrita de
    /// outros processos nesse arquivo compartilhado. Pros outros bancos fica desligado por padrão —
    /// quem quiser controle manual de transação liga o toggle explicitamente.</summary>
    public bool AutoCommit { get; set; } = true;

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

    /// <summary>Linhas novas ainda não salvas (via "Duplicar" ou "Adicionar linha") — cada item é uma
    /// linha completa (coluna → valor) a inserir quando a grid for salva. Renderizadas sempre no fim da
    /// tabela, depois das linhas existentes, pra não bagunçar os índices usados por PendingEdits/PendingDeletes.</summary>
    public List<Dictionary<string, object?>> PendingInserts { get; } = new();

    public void ClearGridEdits()
    {
        EditContext = null;
        EditDisabledReason = null;
        PendingEdits.Clear();
        PendingDeletes.Clear();
        PendingInserts.Clear();
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

    /// <summary>Setado pelo QueryTabsPanel vivo (via OnInitializedAsync) — chamado pelo hook nativo de
    /// fechamento da janela (MainPage) antes de deixar o app fechar de verdade. Pergunta aba a aba (só
    /// as com IsDirty) se quer salvar; retorna false se o usuário cancelar em alguma, abortando o
    /// fechamento do app inteiro. As abas já salvas nunca disparam pergunta nenhuma.</summary>
    public Func<Task<bool>>? ConfirmCloseHandler { get; set; }

    public QueryTabModel AddTab(string connectionProfileId, string title, string initialSql = "", SQLFlow.Data.Models.DatabaseKind? kind = null)
    {
        var tab = new QueryTabModel
        {
            ConnectionProfileId = connectionProfileId,
            Title = title,
            InitialSql = initialSql,
            AutoCommit = kind == SQLFlow.Data.Models.DatabaseKind.Sqlite
        };
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
