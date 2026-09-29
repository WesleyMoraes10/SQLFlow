# SQLFlow

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
   - Um **ícone quadrado 1.75rem × 1.75rem, `border-radius: 7px`, fundo azul (`--db-blue-600`)**, com o
     glifo em branco centralizado (ou a inicial do banco em maiúscula, se for uma tela ligada a um tipo
     de banco específico — ver `.modal-kind-icon`/`.db-kind-icon` em `DatabaseTree.razor.css`).
   - O **título** (`<h2>`, `font-size: 0.95rem; font-weight: 600; color: var(--db-gray-900);`).
   - Um **botão fechar em ícone só** (`×`, sem texto), 1.6rem × 1.6rem, `border-radius: 6px`, fundo
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

## Deploy (publish para o servidor)

O app é distribuído via pasta de rede: cada máquina roda `iniciar-sqlflow.bat`, que compara o
arquivo `SQLFlow.dll` (tamanho+data) entre `\\192.168.0.230\wwwroot\SQLFlow` (servidor) e
`%LOCALAPPDATA%\SQLFlow` (cópia local), só faz `robocopy /MIR` se mudou, e então abre o `.exe`
local. Ou seja: **atualizar o servidor já é o deploy inteiro** — não tem passo adicional em cada
máquina, o launcher se encarrega de sincronizar sozinho na próxima abertura.

Pra publicar uma atualização:

```
dotnet publish SQLFlow.csproj -c Release -f net10.0-windows10.0.19041.0
```

rodado a partir de `SQLFlow/SQLFlow` (o projeto principal). Gera a saída em
`SQLFlow/SQLFlow/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`. Depois, copia essa
pasta pro servidor com robocopy **aditivo** (sem `/MIR` — nunca apaga nada que já esteja lá, só
sobrescreve/adiciona; evita apagar algo do servidor por engano numa publicação parcial):

```
robocopy "<pasta publish>" "\\192.168.0.230\wwwroot\SQLFlow" /E /MT:16 /R:1 /W:1 /XD "SQLFlow.exe.WebView2" /NFL /NDL /NJH /NP
```

(`/XD "SQLFlow.exe.WebView2"` exclui a pasta de perfil/cache do WebView2, que não deve estar na
publicação compartilhada — cada máquina cria a sua própria localmente.)

Exit code 1 do robocopy não é erro — é o código normal pra "um ou mais arquivos copiados com
sucesso" (só código ≥ 8 é falha de verdade).

Esse é um compartilhamento de **produção**, usado por todas as máquinas que abrem o app — não faz
backup automático antes de sobrescrever (decisão explícita, pra manter o deploy rápido); se quiser
uma rede de segurança antes de publicar algo arriscado, copiar a pasta atual do servidor pra um
`SQLFlow.bak-<data>` antes é responsabilidade de quem publica, não algo automático do processo.
