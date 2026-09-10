# DevelopTi

App MAUI Blazor Hybrid para consultar/editar Oracle, SQL Server e MySQL (estilo DBeaver).

## Padrão visual de modal/painel com cabeçalho

Toda tela do tipo modal, painel destacado ou vista em "tela cheia" (ex: `ConnectionFormModal.razor`,
o painel de resultado em tela cheia dentro de `QueryTabsPanel.razor`) deve seguir este mesmo padrão —
não inventar um layout de cabeçalho novo a cada tela nova:

1. **Faixa colorida de 4px no topo** do painel/modal (`border-top: 4px solid var(--db-blue-600);`).
   Só muda de cor quando o conteúdo é literalmente sobre um banco específico (ex: o modal de conexão
   usa a cor do banco selecionado — vermelho Oracle `#c74634`, azul SQL Server `#0078d4`, teal MySQL
   `#00758f`). Fora desse caso, usa sempre o azul padrão do app (`var(--db-blue-600)`).
2. **Cabeçalho claro** (`background: var(--db-white)`, `border-bottom: 1px solid var(--db-gray-200)`),
   nunca fundo escuro — o escuro (`--db-navy-800`) é reservado pra status bar/tabela, não pra cabeçalho
   de modal.
3. Dentro do cabeçalho, da esquerda pra direita:
   - Um **ícone quadrado 2rem × 2rem, `border-radius: 8px`, fundo azul (`--db-blue-600`)**, com o
     glifo em branco centralizado (ou a inicial do banco em maiúscula, se for uma tela ligada a um tipo
     de banco específico — ver `.modal-kind-icon`/`.db-kind-icon` em `DatabaseTree.razor.css`).
   - O **título** (`<h2>`, `font-size: 1.05rem; font-weight: 600; color: var(--db-gray-900);`).
   - Um **botão fechar em ícone só** (`×`, sem texto), 1.9rem × 1.9rem, `border-radius: 6px`, fundo
     transparente, `color: var(--db-gray-400)`, hover com `background: var(--db-gray-100); color:
     var(--db-gray-700);`. Nunca usar link de texto ("Fechar") como no protótipo antigo.
4. Campos de formulário: cada label vem com um ícone pequeno (0.95rem) à esquerda do texto, na cor
   azul (`var(--db-blue-600)`), usando o mesmo padrão de máscara SVG (`-webkit-mask-image`/`mask-image`)
   já usado em todos os ícones do app — nunca emoji ou ícone de biblioteca externa.
5. Ações destrutivas (excluir, descartar alterações) sempre passam por confirmação usando o componente
   `ConfirmDialog.razor` (reaproveitar, não recriar `confirm()` nativo do navegador nem outro modal de
   confirmação do zero).

Referências de implementação: `Components/Workspace/ConnectionFormModal.razor` (+ `.razor.css`) e o
cabeçalho do painel de resultado em tela cheia em `Components/Workspace/QueryTabsPanel.razor`
(classes `.result-panel-header`, `.result-header-icon`, `.result-panel-close-btn`).

## Outras convenções já estabelecidas no app

- Ícones são sempre SVG embutido via `-webkit-mask-image`/`mask-image` com `background-color:
  currentColor` (ou cor fixa quando o ícone precisa manter cor própria, ex: ícones coloridos de banco).
  Nunca usar `<img>`, fonte de ícone externa ou emoji nos componentes principais.
- Cores por tipo de banco são fixas em todo o app: Oracle `#c74634`, SQL Server `#0078d4`, MySQL
  `#00758f`.
- Toda ação que altera dados (rodar UPDATE/DELETE/TRUNCATE, salvar edição de grid, excluir conexão,
  fechar aba com transação pendente) pede confirmação via `ConfirmDialog`, nunca silenciosamente.
- Botões e outros elementos numa mesma barra de ferramentas (toolbar) usam sempre o mesmo tamanho —
  nunca um botão maior e outro menor lado a lado sem motivo. Um elemento só ganha tamanho próprio
  quando isso for pedido explicitamente pra ele. Referência: a toolbar do editor de SQL em
  `Components/Workspace/QueryTabsPanel.razor` usa a classe compartilhada `.toolbar-btn-sm`
  (`QueryTabsPanel.razor.css`) em todos os botões (Executar, Commit, Rollback, Histórico, Exportar,
  Salvar, Tela cheia) e o mesmo padding/font-size é replicado na pill de conexão ativa
  (`.toolbar-connection`, ex.: "Desenv-Teste"), mesmo essa não sendo um `<button>`. Uma tela nova
  com sua própria toolbar deve seguir o mesmo padrão: uma classe de tamanho só, reaproveitada por
  tudo que estiver naquela barra.
