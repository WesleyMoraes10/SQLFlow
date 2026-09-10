console.log('[DevelopTi] monaco-interop.js carregado — build-tag: comment-aware-uppercase-v1');

window.developtiMonaco = (function () {
    let editor = null;
    let loaderConfigured = false;
    let completionRegistered = false;
    let completionDotNetRef = null;
    let execDotNetRef = null;
    let activeConnectionId = null;
    let activeTabId = null;
    const models = {};

    function ensureLoader() {
        if (loaderConfigured) return;
        loaderConfigured = true;
        require.config({ paths: { vs: 'lib/monaco/vs' } });
    }

    // Palavras que aparecem depois de "FROM tabela" / "JOIN tabela" mas não são apelidos.
    const NON_ALIAS_KEYWORDS = new Set([
        'where', 'on', 'inner', 'left', 'right', 'outer', 'full', 'cross', 'join',
        'order', 'group', 'having', 'union', 'set', 'values', 'and', 'or'
    ]);

    // Sugestões padrão de SQL (Ctrl+Espaço fora de FROM/JOIN ou "alias.") — o app não sabe o dialeto
    // na hora de montar essa lista (a aba pode trocar de conexão), então fica só o núcleo comum a
    // Oracle/SQL Server/MySQL, sem função específica de um banco só.
    const SQL_KEYWORDS = [
        'SELECT', 'DISTINCT', 'FROM', 'WHERE', 'AND', 'OR', 'NOT', 'IN', 'EXISTS', 'BETWEEN', 'LIKE',
        'IS NULL', 'IS NOT NULL', 'ORDER BY', 'GROUP BY', 'HAVING', 'JOIN', 'INNER JOIN', 'LEFT JOIN',
        'RIGHT JOIN', 'FULL JOIN', 'CROSS JOIN', 'ON', 'AS', 'UNION', 'UNION ALL', 'INSERT INTO',
        'VALUES', 'UPDATE', 'SET', 'DELETE FROM', 'CASE', 'WHEN', 'THEN', 'ELSE', 'END', 'NULL',
        'WITH', 'ASC', 'DESC'
    ];

    const SQL_FUNCTIONS = [
        'COUNT', 'SUM', 'AVG', 'MIN', 'MAX', 'TRIM', 'UPPER', 'LOWER', 'SUBSTR', 'LENGTH', 'COALESCE',
        'CAST', 'ROUND', 'CONCAT', 'REPLACE'
    ];

    function extractAliasMap(sql) {
        const map = {};
        const regex = /\b(?:FROM|JOIN)\s+([A-Za-z0-9_."[\]`]+)(?:\s+(?:AS\s+)?([A-Za-z_][A-Za-z0-9_]*))?/gi;
        let match;
        while ((match = regex.exec(sql)) !== null) {
            const table = match[1].replace(/["[\]`]/g, '');
            const alias = match[2];
            map[table.toLowerCase()] = table;
            if (alias && !NON_ALIAS_KEYWORDS.has(alias.toLowerCase())) {
                map[alias.toLowerCase()] = table;
            }
        }
        return map;
    }

    // Mesma lógica de "statement onde o cursor está" usada por getStatementAtCursor (delimitado por
    // linhas em branco e, dentro do bloco, por ';'), mas recebendo (model, position) direto em vez de
    // olhar pelo tabId — usada pelo completion provider pra não misturar tabelas de OUTRAS consultas
    // empilhadas no mesmo editor (ver extractAliasMap abaixo).
    function getStatementTextAt(model, position) {
        const lineCount = model.getLineCount();
        let startLine = position.lineNumber;
        while (startLine > 1 && model.getLineContent(startLine - 1).trim() !== '') {
            startLine--;
        }
        let endLine = position.lineNumber;
        while (endLine < lineCount && model.getLineContent(endLine + 1).trim() !== '') {
            endLine++;
        }

        const blockStartOffset = model.getOffsetAt({ lineNumber: startLine, column: 1 });
        const block = model.getValueInRange({
            startLineNumber: startLine,
            startColumn: 1,
            endLineNumber: endLine,
            endColumn: model.getLineMaxColumn(endLine)
        });

        if (block.indexOf(';') === -1) {
            return block;
        }

        // O bloco tem mais de um statement separado por ';' — pega só o que contém o cursor.
        const cursorOffset = model.getOffsetAt(position) - blockStartOffset;
        const parts = block.split(';');
        let consumed = 0;
        for (let i = 0; i < parts.length; i++) {
            const partEnd = consumed + parts[i].length + 1;
            const isLast = i === parts.length - 1;
            if (cursorOffset <= partEnd || isLast) {
                return parts[i];
            }
            consumed = partEnd;
        }

        return block;
    }

    function registerCompletionProvider(dotNetRef) {
        completionDotNetRef = dotNetRef;
        if (completionRegistered || !dotNetRef) return;
        completionRegistered = true;

        monaco.languages.registerCompletionItemProvider('sql', {
            triggerCharacters: ['.'],
            provideCompletionItems: async function (model, position) {
                if (!activeConnectionId || !completionDotNetRef) {
                    console.warn('[DevelopTi] completion abortado: activeConnectionId=' + activeConnectionId + ', completionDotNetRef=' + (completionDotNetRef ? 'ok' : 'null'));
                    return { suggestions: [] };
                }

                const textUntilPosition = model.getValueInRange({
                    startLineNumber: 1,
                    startColumn: 1,
                    endLineNumber: position.lineNumber,
                    endColumn: position.column
                });

                const word = model.getWordUntilPosition(position);
                const range = {
                    startLineNumber: position.lineNumber,
                    endLineNumber: position.lineNumber,
                    startColumn: word.startColumn,
                    endColumn: word.endColumn
                };

                // Banco/schema/tabela logo depois de FROM/JOIN, com 0 a 2 níveis de qualificação já
                // digitados: "CTBL", "PCF4.", "PCF4.dbo.", "PCF4.dbo.CTBL". Checado antes do caso de
                // coluna abaixo, senão "FROM PCF4." cairia ali (achando que "PCF4" é alias/tabela).
                const fromTailMatch = textUntilPosition.match(/\b(?:FROM|JOIN)\s+([A-Za-z0-9_.]*)$/i);
                if (fromTailMatch) {
                    const segments = fromTailMatch[1].split('.');
                    let entries = [];
                    try {
                        entries = await completionDotNetRef.invokeMethodAsync('GetFromCompletionsAsync', activeConnectionId, segments);
                    } catch (e) {
                        console.error('[DevelopTi] GetFromCompletionsAsync(' + JSON.stringify(segments) + ') falhou:', e);
                        entries = [];
                    }

                    return {
                        suggestions: (entries || []).map(function (entry) {
                            const kind = entry.kind === 'database' ? monaco.languages.CompletionItemKind.Module
                                : entry.kind === 'schema' ? monaco.languages.CompletionItemKind.Folder
                                : monaco.languages.CompletionItemKind.Struct;
                            return {
                                label: entry.label,
                                kind: kind,
                                detail: entry.kind,
                                insertText: entry.label,
                                range: range
                            };
                        })
                    };
                }

                // Coluna: "alias." ou "tabela." logo antes do cursor (fora de um FROM/JOIN).
                const dotMatch = textUntilPosition.match(/([A-Za-z_][A-Za-z0-9_]*)\.[A-Za-z0-9_]*$/);
                if (dotMatch) {
                    const aliasMap = extractAliasMap(getStatementTextAt(model, position));
                    const token = dotMatch[1].toLowerCase();
                    const tableRef = aliasMap[token] || dotMatch[1];
                    console.log('[DevelopTi] coluna: conexao=' + activeConnectionId + ' alias="' + token + '" -> tabela="' + tableRef + '" aliasMap=' + JSON.stringify(aliasMap));

                    let columns = [];
                    try {
                        columns = await completionDotNetRef.invokeMethodAsync('GetColumnsAsync', activeConnectionId, tableRef);
                        console.log('[DevelopTi] GetColumnsAsync devolveu ' + (columns ? columns.length : 0) + ' coluna(s):', columns);
                    } catch (e) {
                        console.error('[DevelopTi] GetColumnsAsync("' + tableRef + '") falhou:', e);
                        columns = [];
                    }

                    return {
                        suggestions: (columns || []).map(function (c) {
                            return {
                                label: c.name,
                                kind: monaco.languages.CompletionItemKind.Field,
                                detail: c.dataType + (c.isPrimaryKey ? ' · PK' : ''),
                                insertText: c.name,
                                range: range
                            };
                        })
                    };
                }

                // Fallback: fora de FROM/JOIN e fora de "alias." — sugere palavras-chave/funções
                // padrão do SQL (SELECT, DISTINCT, COUNT...), como qualquer editor SQL costuma
                // oferecer mesmo sem saber ainda de qual tabela se trata.
                const offset = model.getOffsetAt(position);
                if (classifySqlStateAt(model.getValue(), offset) !== 'normal') {
                    return { suggestions: [] }; // dentro de comentário/string — não sugere nada
                }

                const prefix = word.word.toLowerCase();
                const keywordSuggestions = SQL_KEYWORDS
                    .filter(function (kw) { return kw.toLowerCase().startsWith(prefix); })
                    .map(function (kw) {
                        return {
                            label: kw,
                            kind: monaco.languages.CompletionItemKind.Keyword,
                            insertText: kw,
                            range: range
                        };
                    });

                const functionSuggestions = SQL_FUNCTIONS
                    .filter(function (fn) { return fn.toLowerCase().startsWith(prefix); })
                    .map(function (fn) {
                        return {
                            label: fn + '()',
                            kind: monaco.languages.CompletionItemKind.Function,
                            insertText: fn + '($0)',
                            insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
                            range: range
                        };
                    });

                // Colunas das tabelas já referenciadas no FROM/JOIN, mesmo sem digitar "alias." antes —
                // "B1_COD" sugere igual estando com "S." na frente ou não, sem precisar decorar o alias.
                // Restrito ao statement ATUAL (não o editor inteiro), senão consultas empilhadas no
                // mesmo editor vazavam colunas de tabelas de OUTRAS consultas.
                const aliasMap = extractAliasMap(getStatementTextAt(model, position));
                const referencedTables = Array.from(new Set(Object.values(aliasMap)));
                let columnSuggestions = [];
                if (referencedTables.length > 0) {
                    const columnLists = await Promise.all(referencedTables.map(function (table) {
                        return completionDotNetRef.invokeMethodAsync('GetColumnsAsync', activeConnectionId, table)
                            .catch(function () { return []; });
                    }));
                    columnLists.forEach(function (columns, idx) {
                        (columns || []).forEach(function (c) {
                            if (!c.name.toLowerCase().startsWith(prefix)) return;
                            columnSuggestions.push({
                                label: c.name,
                                kind: monaco.languages.CompletionItemKind.Field,
                                detail: referencedTables[idx] + (c.isPrimaryKey ? ' · PK' : ''),
                                insertText: c.name,
                                range: range,
                                sortText: '0' + c.name // colunas da tabela em uso vêm antes de keyword/função genérica
                            });
                        });
                    });
                }

                return { suggestions: columnSuggestions.concat(keywordSuggestions, functionSuggestions) };
            }
        });
    }

    // Segue o mesmo espírito do app.css: as MESMAS chaves de cor, só muda o valor entre claro/escuro
    // (ver comentário "componentes não precisam saber qual tema está ativo" em app.css).
    function defineThemes() {
        monaco.editor.defineTheme('developti', {
            base: 'vs',
            inherit: true,
            rules: [
                { token: 'keyword.sql', foreground: '1f5b8f', fontStyle: 'bold' },
                { token: 'comment.sql', foreground: '6b7785', fontStyle: 'italic' },
                { token: 'string.sql', foreground: '2e8b57' },
                { token: 'number.sql', foreground: 'b8860b' }
            ],
            colors: {
                'editor.background': '#ffffff',
                'editor.foreground': '#1f2933',
                'editor.lineHighlightBackground': '#f2f8fd',
                'editorLineNumber.foreground': '#9aa5b1',
                'editorLineNumber.activeForeground': '#1f5b8f',
                'editorCursor.foreground': '#1f5b8f',
                'editor.selectionBackground': '#e6f1fa',
                // Widget de busca/substituição (Ctrl+F / Ctrl+H) usando as cores --db-* do app
                // em vez do cinza padrão do VS Code.
                'editorWidget.background': '#ffffff',
                'editorWidget.foreground': '#1f2933',
                'editorWidget.border': '#dfe4ea',
                'widget.shadow': 'rgba(22, 33, 44, 0.16)',
                'input.background': '#ffffff',
                'input.foreground': '#1f2933',
                'input.border': '#dfe4ea',
                'inputOption.activeBackground': '#e6f1fa',
                'inputOption.activeBorder': '#2e7bb8',
                'inputOption.activeForeground': '#1f5b8f',
                'inputValidation.errorBackground': '#fbeceb',
                'inputValidation.errorBorder': '#c0392b',
                'inputValidation.errorForeground': '#c0392b',
                'editor.findMatchBackground': '#ffd600',
                'editor.findMatchBorder': '#c79a00',
                'editor.findMatchHighlightBackground': 'rgba(255, 214, 0, 0.35)',
                'editor.findMatchHighlightBorder': 'rgba(255, 214, 0, 0.7)',
                'editor.findRangeHighlightBackground': 'rgba(255, 214, 0, 0.12)',
                'toolbar.hoverBackground': '#eef1f4',
                'icon.foreground': '#6b7785',
                'focusBorder': '#2e7bb8'
            }
        });

        monaco.editor.defineTheme('developti-dark', {
            base: 'vs-dark',
            inherit: true,
            rules: [
                { token: 'keyword.sql', foreground: '4da3e0', fontStyle: 'bold' },
                { token: 'comment.sql', foreground: '8b98a8', fontStyle: 'italic' },
                { token: 'string.sql', foreground: '4cbf7d' },
                { token: 'number.sql', foreground: 'd9a441' }
            ],
            colors: {
                'editor.background': '#1b2532',
                'editor.foreground': '#e8ecf1',
                'editor.lineHighlightBackground': '#16283a',
                'editorLineNumber.foreground': '#6b7785',
                'editorLineNumber.activeForeground': '#4da3e0',
                'editorCursor.foreground': '#4da3e0',
                'editor.selectionBackground': '#1c3348',
                'editorWidget.background': '#1b2532',
                'editorWidget.foreground': '#e8ecf1',
                'editorWidget.border': '#2a3644',
                'widget.shadow': 'rgba(0, 0, 0, 0.4)',
                'input.background': '#1b2532',
                'input.foreground': '#e8ecf1',
                'input.border': '#2a3644',
                'inputOption.activeBackground': '#1c3348',
                'inputOption.activeBorder': '#3d8fd1',
                'inputOption.activeForeground': '#4da3e0',
                'inputValidation.errorBackground': '#3a1f1c',
                'inputValidation.errorBorder': '#e2685c',
                'inputValidation.errorForeground': '#e2685c',
                'editor.findMatchBackground': '#ffd600',
                'editor.findMatchBorder': '#c79a00',
                'editor.findMatchHighlightBackground': 'rgba(255, 214, 0, 0.28)',
                'editor.findMatchHighlightBorder': 'rgba(255, 214, 0, 0.6)',
                'editor.findRangeHighlightBackground': 'rgba(255, 214, 0, 0.12)',
                'toolbar.hoverBackground': '#202b38',
                'icon.foreground': '#8b98a8',
                'focusBorder': '#3d8fd1'
            }
        });

        // Paleta "Freshcut Contrast" (alto contraste, fundo preto puro) — cores exatamente como
        // fornecidas pelo usuário; papéis de token/cor que o fragmento original não cobria (ex.:
        // keyword.sql, editor.selectionBackground) reaproveitam cores já presentes na própria paleta
        // em vez de introduzir tons novos.
        monaco.editor.defineTheme('freshcut-contrast', {
            base: 'vs-dark',
            inherit: true,
            rules: [
                { token: 'comment.sql', foreground: '737b84', fontStyle: 'italic' },
                { token: 'string.sql', foreground: 'e9ee00' },
                { token: 'keyword.sql', foreground: '4ecdc4', fontStyle: 'bold' },
                { token: 'number.sql', foreground: 'f7b83d' }
            ],
            colors: {
                'editor.background': '#000000',
                'editor.foreground': '#f8f8f2',
                'editor.lineHighlightBackground': '#111111',
                'editorLineNumber.foreground': '#737b84',
                'editorLineNumber.activeForeground': '#4ecdc4',
                'editorCursor.foreground': '#4ecdc4',
                'editor.selectionBackground': '#333333',
                'editor.selectionForeground': '#ffffff',
                'editorBracketMatch.border': '#4ecdc4',
                'editorBracketHighlight.foreground1': '#00a8c6',
                'editorWidget.background': '#000000',
                'editorWidget.foreground': '#f8f8f2',
                'editorWidget.border': '#333333',
                'widget.shadow': 'rgba(0, 0, 0, 0.6)',
                'input.background': '#000000',
                'input.foreground': '#f8f8f2',
                'input.border': '#333333',
                'inputOption.activeBackground': '#333333',
                'inputOption.activeBorder': '#4ecdc4',
                'inputOption.activeForeground': '#4ecdc4',
                'inputValidation.errorBackground': '#3a1015',
                'inputValidation.errorBorder': '#e61f44',
                'inputValidation.errorForeground': '#e61f44',
                'editor.findMatchBackground': '#ffe792',
                'editor.findMatchBorder': '#c79a00',
                'editor.findMatchHighlightBackground': 'rgba(255, 231, 146, 0.3)',
                'editor.findMatchHighlightBorder': 'rgba(255, 231, 146, 0.6)',
                'editor.findRangeHighlightBackground': 'rgba(255, 231, 146, 0.12)',
                'toolbar.hoverBackground': '#1a1a1a',
                'icon.foreground': '#737b84',
                'focusBorder': '#4ecdc4',
                'editorOverviewRuler.errorForeground': '#e61f44',
                'editorOverviewRuler.warningForeground': '#f7b83d',
                'editorOverviewRuler.infoForeground': '#9d37fc',
                'scrollbar.shadow': '#000000',
                'editorSuggestWidget.background': '#000000',
                'editorSuggestWidget.foreground': '#f8f8f2',
                'editorSuggestWidget.highlightForeground': '#4ecdc4',
                'editorSuggestWidget.selectedBackground': '#333333'
            }
        });
    }

    // O tema do editor sempre segue o tema geral do app (Claro/Escuro/Freshcut Contrast) — cada um
    // já tem um tema de editor com as cores correspondentes (ver defineThemes).
    function resolveInitialEditorTheme() {
        var mode = resolveColorMode();
        if (mode === 'freshcut-contrast') return 'freshcut-contrast';
        return mode === 'dark' ? 'developti-dark' : 'developti';
    }

    // Lê o tema atual direto do atributo aplicado pelo developtiTheme (theme.js) — assim o Monaco
    // nasce já na cor certa mesmo se ele inicializar depois da troca de tema, sem precisar coordenar
    // ordem de carregamento entre os dois scripts.
    function resolveColorMode() {
        var attr = document.documentElement.getAttribute('data-theme');
        return (attr === 'dark' || attr === 'freshcut-contrast') ? attr : 'light';
    }

    // Chamado pelo developtiTheme.apply (theme.js) toda vez que o usuário troca claro/escuro nas
    // Configurações — se o Monaco ainda não foi inicializado (nenhuma aba de query aberta ainda),
    // é um no-op seguro: o init() abaixo já nasce com a cor certa via resolveColorMode().
    function setColorTheme(mode) {
        if (typeof monaco === 'undefined' || !monaco.editor) return;
        if (mode === 'freshcut-contrast') {
            monaco.editor.setTheme('freshcut-contrast');
            return;
        }
        monaco.editor.setTheme(mode === 'dark' ? 'developti-dark' : 'developti');
    }

    // Compartilhado entre Ctrl+Enter e F5: se o usuário selecionou texto manualmente, roda
    // exatamente isso; senão tenta adivinhar o statement pelo cursor. Se não achar nada (editor
    // vazio ou cursor fora de qualquer statement), avisa em vez de ficar quieto — F5/Ctrl+Enter
    // sem feedback nenhum passa a impressão de que o atalho não funcionou.
    function runActiveStatement() {
        if (!execDotNetRef || !activeTabId) return;

        // Mesma checagem do botão Executar: se o texto SELECIONADO tem mais de um statement, roda em
        // paralelo em vez de mandar tudo colado como um comando só (o que dava ORA-00933 no Oracle).
        const selectedText = getSelectedText(activeTabId);
        if (selectedText) {
            const statements = splitIntoStatements(selectedText);
            if (statements.length > 1) {
                execDotNetRef.invokeMethodAsync('ExecuteMultipleStatementsFromEditorAsync', activeTabId, statements);
                return;
            }
        }

        const sql = selectedText || getStatementAtCursor(activeTabId);
        if (!sql) {
            execDotNetRef.invokeMethodAsync('NotifyNoStatementToRunAsync');
            return;
        }
        execDotNetRef.invokeMethodAsync('ExecuteStatementFromEditorAsync', activeTabId, sql);
    }

    function init(containerId, dotNetCompletionRef, dotNetExecRef) {
        execDotNetRef = dotNetExecRef || execDotNetRef;
        return new Promise((resolve) => {
            if (editor) {
                // Navegar para fora da tela e voltar pode recriar a div no DOM; se o editor antigo
                // ainda apontar para um nó desconectado, descarta e recria no container atual.
                registerCompletionProvider(dotNetCompletionRef);
                if (editor.getDomNode() && document.contains(editor.getDomNode())) {
                    resolve();
                    return;
                }
                editor.dispose();
                editor = null;
            }
            ensureLoader();
            require(['vs/editor/editor.main'], function () {
                const container = document.getElementById(containerId);
                if (!container) {
                    console.error('[DevelopTi] developtiMonaco.init: elemento #' + containerId + ' não existe no DOM ainda.');
                    resolve();
                    return;
                }
                defineThemes();
                registerCompletionProvider(dotNetCompletionRef);
                editor = monaco.editor.create(container, {
                    language: 'sql',
                    theme: resolveInitialEditorTheme(),
                    automaticLayout: true,
                    minimap: { enabled: false },
                    fontSize: 13,
                    fontFamily: "Cascadia Code, Consolas, 'SFMono-Regular', Menlo, monospace",
                    scrollBeyondLastLine: false,
                    renderLineHighlight: 'all',
                    suggestOnTriggerCharacters: true
                });
                editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, runActiveStatement);
                editor.addCommand(monaco.KeyCode.F5, runActiveStatement);
                editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyF, function () {
                    formatAtCursor(activeTabId);
                });
                resolve();
            });
        });
    }

    // Percorre o texto de trás do ponto de inserção (que não muda com a edição atual) pra saber em que
    // estado ela começa: dentro de comentário "--", comentário "/* */" ainda aberto, ou dentro de uma
    // string 'entre aspas'. Sem isso o auto-maiúsculas estragaria comentários e valores digitados.
    function classifySqlStateAt(text, offset) {
        let state = 'normal';
        for (let i = 0; i < offset; i++) {
            const ch = text[i];
            const next = text[i + 1];
            if (state === 'normal') {
                if (ch === '-' && next === '-') { state = 'line-comment'; i++; }
                else if (ch === '/' && next === '*') { state = 'block-comment'; i++; }
                else if (ch === "'") { state = 'string'; }
            } else if (state === 'line-comment') {
                if (ch === '\n') state = 'normal';
            } else if (state === 'block-comment') {
                if (ch === '*' && next === '/') { state = 'normal'; i++; }
            } else if (state === 'string') {
                if (ch === "'") {
                    if (next === "'") i++; // '' escapado dentro da string — continua em 'string'
                    else state = 'normal';
                }
            }
        }
        return state;
    }

    // Maiusculiza um trecho de texto respeitando comentários/strings que comecem (ou continuem) dentro
    // dele, a partir do estado inicial calculado por classifySqlStateAt.
    function uppercaseRespectingComments(text, startState) {
        let state = startState;
        let result = '';
        for (let i = 0; i < text.length; i++) {
            const ch = text[i];
            const next = text[i + 1];
            if (state === 'normal') {
                if (ch === '-' && next === '-') { state = 'line-comment'; result += ch; continue; }
                if (ch === '/' && next === '*') { state = 'block-comment'; result += ch; continue; }
                if (ch === "'") { state = 'string'; result += ch; continue; }
                result += ch.toUpperCase();
            } else if (state === 'line-comment') {
                if (ch === '\n') state = 'normal';
                result += ch;
            } else if (state === 'block-comment') {
                if (ch === '*' && next === '/') { state = 'normal'; result += ch + next; i++; continue; }
                result += ch;
            } else if (state === 'string') {
                if (ch === "'") {
                    if (next === "'") { result += ch + next; i++; continue; }
                    state = 'normal';
                }
                result += ch;
            }
        }
        return result;
    }

    // Converte o que for digitado pra maiúsculo automaticamente (padrão dos consoles SQL de ERP), exceto
    // dentro de comentários ("--" e "/* */") e de strings 'entre aspas' — comentário e valor de texto
    // devem ficar do jeito que a pessoa digitou.
    function attachUppercaseTransform(model) {
        let applying = false;
        model.onDidChangeContent(function (e) {
            if (applying) return;
            const edits = [];
            for (const change of e.changes) {
                if (!change.text) continue;
                const startOffset = change.rangeOffset;
                const state = classifySqlStateAt(model.getValue(), startOffset);
                const transformed = uppercaseRespectingComments(change.text, state);
                if (transformed === change.text) continue;
                const startPos = model.getPositionAt(startOffset);
                const endPos = model.getPositionAt(startOffset + change.text.length);
                edits.push({
                    range: new monaco.Range(startPos.lineNumber, startPos.column, endPos.lineNumber, endPos.column),
                    text: transformed
                });
            }
            if (edits.length > 0) {
                applying = true;
                model.applyEdits(edits);
                applying = false;
            }
        });
    }

    function setActiveTab(tabId, initialValue, connectionProfileId) {
        if (!editor) return;
        activeConnectionId = connectionProfileId || null;
        activeTabId = tabId;
        console.log('[DevelopTi] aba ativa=' + tabId + ' conexao=' + activeConnectionId);
        let model = models[tabId];
        if (!model) {
            model = monaco.editor.createModel(uppercaseRespectingComments(initialValue || '', 'normal'), 'sql');
            attachUppercaseTransform(model);
            models[tabId] = model;
        }
        editor.setModel(model);
        editor.focus();
    }

    function getValue(tabId) {
        const model = models[tabId];
        return model ? model.getValue() : '';
    }

    // Texto atualmente selecionado com o mouse/teclado nessa aba, ou '' se não há seleção (ou o
    // model dessa aba não é o que está aberto no editor agora).
    function getSelectedText(tabId) {
        const model = models[tabId];
        if (!model || !editor || editor.getModel() !== model) return '';
        const selection = editor.getSelection();
        if (!selection || selection.isEmpty()) return '';
        return model.getValueInRange(selection).trim();
    }

    // Usado pelo botão "Executar": se houver texto selecionado, roda só ele; senão, o editor inteiro.
    function getExecutionValue(tabId) {
        return getSelectedText(tabId) || getValue(tabId);
    }

    // Quebra um texto em vários statements: primeiro por linha em branco (mesmo critério de bloco
    // usado em getStatementAtCursor), depois cada bloco por ';' se tiver mais de um comando dentro.
    function splitIntoStatements(text) {
        const lines = text.replace(/\r\n/g, '\n').split('\n');
        const blocks = [];
        let current = [];
        for (const line of lines) {
            if (line.trim() === '') {
                if (current.length) { blocks.push(current.join('\n')); current = []; }
            } else {
                current.push(line);
            }
        }
        if (current.length) blocks.push(current.join('\n'));

        const statements = [];
        blocks.forEach(function (block) {
            block.split(';').forEach(function (part) {
                const trimmed = part.trim();
                if (trimmed) statements.push(trimmed);
            });
        });
        return statements;
    }

    // Usado pelo botão "Executar" pra decidir entre rodar uma consulta só (comportamento de sempre)
    // ou várias em paralelo — só entra em jogo quando a pessoa SELECIONA manualmente um trecho com
    // mais de um statement; sem seleção (ou seleção de um statement só) devolve lista vazia/1 item
    // e o C# cai no fluxo normal de execução única.
    function getSelectedStatements(tabId) {
        const text = getSelectedText(tabId);
        if (!text) return [];
        return splitIntoStatements(text);
    }

    // Extrai só o statement SQL onde o cursor está (delimitado por linhas em branco e, dentro
    // do bloco, por ';'), pra permitir empilhar várias consultas no mesmo editor e rodar uma
    // de cada vez com Ctrl+Enter, sem precisar selecionar o texto manualmente.
    function getStatementAtCursor(tabId) {
        const model = models[tabId];
        if (!model || !editor || editor.getModel() !== model) return '';

        const position = editor.getPosition();
        if (!position) return model.getValue().trim();

        const lineCount = model.getLineCount();
        let startLine = position.lineNumber;
        while (startLine > 1 && model.getLineContent(startLine - 1).trim() !== '') {
            startLine--;
        }
        let endLine = position.lineNumber;
        while (endLine < lineCount && model.getLineContent(endLine + 1).trim() !== '') {
            endLine++;
        }

        const blockStartOffset = model.getOffsetAt({ lineNumber: startLine, column: 1 });
        const block = model.getValueInRange({
            startLineNumber: startLine,
            startColumn: 1,
            endLineNumber: endLine,
            endColumn: model.getLineMaxColumn(endLine)
        });

        if (block.indexOf(';') === -1) {
            return block.trim();
        }

        // O bloco tem mais de um statement separado por ';' — pega só o que contém o cursor.
        const cursorOffset = model.getOffsetAt(position) - blockStartOffset;
        const parts = block.split(';');
        let consumed = 0;
        for (let i = 0; i < parts.length; i++) {
            const partEnd = consumed + parts[i].length + 1;
            const isLast = i === parts.length - 1;
            if (cursorOffset <= partEnd || isLast) {
                const trimmed = parts[i].trim();
                if (trimmed) return trimmed;
            }
            consumed = partEnd;
        }

        return block.trim();
    }

    // Mesma lógica de getStatementAtCursor, mas devolvendo o Range exato (não só o texto), pra dar
    // pra substituir só aquele trecho no model depois de formatar (usado por formatAtCursor).
    function getStatementRangeAtCursor(tabId) {
        const model = models[tabId];
        if (!model || !editor || editor.getModel() !== model) return null;

        const position = editor.getPosition();
        if (!position) return null;

        const lineCount = model.getLineCount();
        let startLine = position.lineNumber;
        while (startLine > 1 && model.getLineContent(startLine - 1).trim() !== '') {
            startLine--;
        }
        let endLine = position.lineNumber;
        while (endLine < lineCount && model.getLineContent(endLine + 1).trim() !== '') {
            endLine++;
        }

        const blockStartOffset = model.getOffsetAt({ lineNumber: startLine, column: 1 });
        const block = model.getValueInRange({
            startLineNumber: startLine,
            startColumn: 1,
            endLineNumber: endLine,
            endColumn: model.getLineMaxColumn(endLine)
        });

        function rangeFor(trimmedStartOffset, trimmedEndOffset) {
            const startPos = model.getPositionAt(blockStartOffset + trimmedStartOffset);
            const endPos = model.getPositionAt(blockStartOffset + trimmedEndOffset);
            return new monaco.Range(startPos.lineNumber, startPos.column, endPos.lineNumber, endPos.column);
        }

        if (block.indexOf(';') === -1) {
            const start = block.length - block.trimStart().length;
            const end = block.trimEnd().length;
            return end > start ? { range: rangeFor(start, end), text: block.slice(start, end) } : null;
        }

        const cursorOffset = model.getOffsetAt(position) - blockStartOffset;
        const parts = block.split(';');
        let consumed = 0;
        for (let i = 0; i < parts.length; i++) {
            const part = parts[i];
            const partEnd = consumed + part.length + 1;
            const isLast = i === parts.length - 1;
            if (cursorOffset <= partEnd || isLast) {
                const start = consumed + (part.length - part.trimStart().length);
                const end = consumed + part.trimEnd().length;
                if (end > start) return { range: rangeFor(start, end), text: block.slice(start, end) };
            }
            consumed = partEnd;
        }

        const start = block.length - block.trimStart().length;
        const end = block.trimEnd().length;
        return end > start ? { range: rangeFor(start, end), text: block.slice(start, end) } : null;
    }

    // --- Formatador de SQL (Ctrl+Shift+F) -----------------------------------------------------
    // O Monaco não vem com um formatador embutido pra SQL (só JS/TS/JSON/CSS/HTML têm), então é
    // um tokenizer + regras de quebra de linha própria. Não é um parser completo — não entende a
    // fundo a árvore da consulta, então casos bem aninhados/exóticos podem não ficar perfeitos —
    // mas é seguro: nunca altera comentários, strings ou identificadores entre aspas, só re-arruma
    // espaços e quebras de linha em torno deles.

    function isSymbolChar(ch) {
        return ch !== undefined && !/\s/.test(ch) && !/[A-Za-z0-9_]/.test(ch)
            && ch !== '(' && ch !== ')' && ch !== ',' && ch !== "'" && ch !== '"';
    }

    function tokenizeSql(text) {
        const tokens = [];
        let i = 0;
        let buf = '';

        function flushBuf() {
            if (buf) { tokens.push({ type: 'word', value: buf }); buf = ''; }
        }

        while (i < text.length) {
            const ch = text[i];
            const next = text[i + 1];

            if (ch === '-' && next === '-') {
                flushBuf();
                let j = i;
                while (j < text.length && text[j] !== '\n') j++;
                tokens.push({ type: 'comment', value: text.slice(i, j) });
                i = j;
                continue;
            }
            if (ch === '/' && next === '*') {
                flushBuf();
                let j = text.indexOf('*/', i + 2);
                j = j === -1 ? text.length : j + 2;
                tokens.push({ type: 'comment', value: text.slice(i, j) });
                i = j;
                continue;
            }
            if (ch === "'" || ch === '"') {
                flushBuf();
                const quote = ch;
                let j = i + 1;
                while (j < text.length) {
                    if (text[j] === quote && text[j + 1] === quote) { j += 2; continue; }
                    if (text[j] === quote) { j++; break; }
                    j++;
                }
                tokens.push({ type: 'string', value: text.slice(i, j) });
                i = j;
                continue;
            }
            if (/\s/.test(ch)) {
                flushBuf();
                i++;
                continue;
            }
            if (ch === '(' || ch === ')' || ch === ',') {
                flushBuf();
                tokens.push({ type: 'punct', value: ch });
                i++;
                continue;
            }
            if (/[A-Za-z0-9_]/.test(ch)) {
                buf += ch;
                i++;
                continue;
            }

            // Operador/símbolo (=, <=, <>, ||, +, -, ::, etc.) — junta caracteres consecutivos
            // desse tipo num único token, senão "<=" quebraria em dois tokens com espaço no meio.
            flushBuf();
            let j = i;
            while (j < text.length && isSymbolChar(text[j])) j++;
            tokens.push({ type: 'sym', value: text.slice(i, j) });
            i = j;
        }
        flushBuf();
        return tokens;
    }

    const FORMAT_CLAUSE_KEYWORDS = new Set(['SELECT', 'FROM', 'WHERE', 'SET', 'VALUES', 'HAVING']);
    const FORMAT_JOIN_MODIFIERS = new Set(['INNER', 'LEFT', 'RIGHT', 'FULL', 'CROSS', 'OUTER']);
    const FORMAT_AND_OR = new Set(['AND', 'OR']);

    // Palavras depois das quais "(" é abertura de grupo/subquery (mantém espaço antes) — qualquer
    // outra palavra antes de "(" é tratada como nome de função, sem espaço ("nvl(x)", não "nvl (x)").
    const FORMAT_KEYWORDS_SPACE_BEFORE_PAREN = new Set([
        'AND', 'OR', 'WHERE', 'IN', 'NOT', 'ON', 'HAVING', 'VALUES', 'EXISTS', 'FROM', 'UNION',
        'SELECT', 'BY', 'ALL', 'ANY', 'THEN', 'WHEN', 'ELSE'
    ]);

    // Contexto onde "+"/"-" é sinal unário (sem espaço antes do número), não operador binário.
    const FORMAT_UNARY_CONTEXT_KEYWORDS = new Set([
        'AND', 'OR', 'WHERE', 'THEN', 'WHEN', 'ELSE', 'SELECT', 'IN', 'NOT', 'ON', 'HAVING', 'BY'
    ]);

    function isUnaryContext(prevTok) {
        if (!prevTok) return true;
        if (prevTok.type === 'punct' && (prevTok.value === '(' || prevTok.value === ',')) return true;
        if (prevTok.type === 'sym') return true;
        if (prevTok.type === 'word') return FORMAT_UNARY_CONTEXT_KEYWORDS.has(prevTok.value.toUpperCase());
        return false;
    }

    function formatSql(text) {
        const tokens = tokenizeSql(text);
        let out = '';
        let depth = 0;
        let atLineStart = true;

        function indentStr(level) {
            return '    '.repeat(Math.max(0, level));
        }

        function newLine(level) {
            out = out.replace(/[ \t]+$/, '');
            out += '\n' + indentStr(level);
            atLineStart = true;
        }

        function upper(tok) { return tok && tok.type === 'word' ? tok.value.toUpperCase() : null; }

        for (let i = 0; i < tokens.length; i++) {
            const tok = tokens[i];
            const u = upper(tok);

            if (tok.type === 'punct' && tok.value === ')') {
                depth = Math.max(0, depth - 1);
            }

            let breakBefore = false;
            let breakIndent = depth;

            if (tok.type === 'word') {
                const nextU = upper(tokens[i + 1]);
                if (FORMAT_CLAUSE_KEYWORDS.has(u) || u === 'UNION') {
                    breakBefore = true;
                } else if ((u === 'GROUP' || u === 'ORDER') && nextU === 'BY') {
                    breakBefore = true;
                } else if (u === 'INSERT' && nextU === 'INTO') {
                    breakBefore = true;
                } else if (u === 'DELETE' && nextU === 'FROM') {
                    breakBefore = true;
                } else if (FORMAT_JOIN_MODIFIERS.has(u)) {
                    const n1 = upper(tokens[i + 1]);
                    const n2 = upper(tokens[i + 2]);
                    if (n1 === 'JOIN' || (n1 === 'OUTER' && n2 === 'JOIN')) {
                        breakBefore = true;
                    }
                } else if (u === 'JOIN') {
                    const prevU = upper(tokens[i - 1]);
                    if (!FORMAT_JOIN_MODIFIERS.has(prevU)) {
                        breakBefore = true;
                    }
                } else if (u === 'ON' && depth === 0) {
                    breakBefore = true;
                    breakIndent = depth + 1;
                } else if (FORMAT_AND_OR.has(u)) {
                    breakBefore = true;
                    breakIndent = depth + 1;
                }
            }

            if (breakBefore && !atLineStart) {
                newLine(breakIndent);
            }

            if (!atLineStart) {
                const prevTok = tokens[i - 1];
                const prevIsOpenParen = prevTok && prevTok.type === 'punct' && prevTok.value === '(';
                // "insert into tabela (a, b)" — o nome da tabela logo depois de INTO não é chamada
                // de função, então mantém o espaço antes da lista de colunas.
                const afterIntoTableName = prevTok && prevTok.type === 'word'
                    && tokens[i - 2] && tokens[i - 2].type === 'word' && tokens[i - 2].value.toUpperCase() === 'INTO';
                const isFunctionCallParen = tok.type === 'punct' && tok.value === '('
                    && prevTok && prevTok.type === 'word' && !afterIntoTableName
                    && !FORMAT_KEYWORDS_SPACE_BEFORE_PAREN.has(prevTok.value.toUpperCase());
                const isUnarySign = prevTok && prevTok.type === 'sym' && (prevTok.value === '-' || prevTok.value === '+')
                    && isUnaryContext(tokens[i - 2]);
                const needsSpace = !(tok.type === 'punct' && (tok.value === ')' || tok.value === ','))
                    && !prevIsOpenParen
                    && !isFunctionCallParen
                    && !isUnarySign
                    && !(tok.type === 'sym' && tok.value === '.')
                    && !(prevTok && prevTok.type === 'sym' && prevTok.value === '.');
                if (needsSpace) out += ' ';
            }

            out += tok.value;
            atLineStart = false;

            if (tok.type === 'punct' && tok.value === '(') {
                depth++;
            }

            if (tok.type === 'comment' && tok.value.slice(0, 2) === '--') {
                // Comentário de linha vai até o fim da linha original — se o próximo token
                // continuar na mesma linha de saída, ele seria engolido pelo comentário.
                newLine(depth);
            }

            if (tok.type === 'punct' && tok.value === ',' && depth === 0) {
                newLine(depth + 1);
            }
        }

        return out.trim();
    }

    // Formata a seleção atual (se houver) ou o statement onde o cursor está — mesmo escopo usado
    // pelo Ctrl+Enter pra executar, então o comportamento já é familiar.
    function formatAtCursor(tabId) {
        const model = models[tabId];
        if (!model || !editor || editor.getModel() !== model) return;

        const selection = editor.getSelection();
        let range, original;
        if (selection && !selection.isEmpty()) {
            range = selection;
            original = model.getValueInRange(selection);
        } else {
            const found = getStatementRangeAtCursor(tabId);
            if (!found) return;
            range = found.range;
            original = found.text;
        }

        const formatted = formatSql(original);
        if (!formatted || formatted === original) return;
        editor.executeEdits('developti-format', [{ range: range, text: formatted }]);
        editor.focus();
    }

    function setValue(tabId, value) {
        const model = models[tabId];
        if (model) model.setValue(value || '');
    }

    function closeTab(tabId) {
        const model = models[tabId];
        if (model) {
            model.dispose();
            delete models[tabId];
        }
    }

    return { init, setActiveTab, getValue, getExecutionValue, getSelectedStatements, getStatementAtCursor, setValue, closeTab, setColorTheme };
})();
