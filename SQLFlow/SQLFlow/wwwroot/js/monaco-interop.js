console.log('[SQLFlow] monaco-interop.js carregado — build-tag: update-alias-completion-v1');

window.sqlflowMonaco = (function () {
    let editor = null;
    let loaderConfigured = false;
    let completionRegistered = false;
    let completionDotNetRef = null;
    let execDotNetRef = null;
    let activeConnectionId = null;
    let activeTabId = null;
    let lastSelectionLength = -1;
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
        // UPDATE entra na lista porque "UPDATE tabela alias SET ..." apelida a tabela sem usar FROM/JOIN
        // (diferente de DELETE, que nos três bancos sempre passa por "DELETE FROM tabela alias").
        const regex = /\b(?:FROM|JOIN|UPDATE)\s+([A-Za-z0-9_."[\]`]+)(?:\s+(?:AS\s+)?([A-Za-z_][A-Za-z0-9_]*))?/gi;
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
                    console.warn('[SQLFlow] completion abortado: activeConnectionId=' + activeConnectionId + ', completionDotNetRef=' + (completionDotNetRef ? 'ok' : 'null'));
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
                        console.error('[SQLFlow] GetFromCompletionsAsync(' + JSON.stringify(segments) + ') falhou:', e);
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
                    console.log('[SQLFlow] coluna: conexao=' + activeConnectionId + ' alias="' + token + '" -> tabela="' + tableRef + '" aliasMap=' + JSON.stringify(aliasMap));

                    let columns = [];
                    try {
                        columns = await completionDotNetRef.invokeMethodAsync('GetColumnsAsync', activeConnectionId, tableRef);
                        console.log('[SQLFlow] GetColumnsAsync devolveu ' + (columns ? columns.length : 0) + ' coluna(s):', columns);
                    } catch (e) {
                        console.error('[SQLFlow] GetColumnsAsync("' + tableRef + '") falhou:', e);
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
        monaco.editor.defineTheme('sqlflow', {
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

        monaco.editor.defineTheme('sqlflow-dark', {
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

        // Tema "Juju": preto e cinzas neutros com acento rosa — mesma paleta do tema geral do app
        // (ver :root[data-theme="juju"] em app.css).
        monaco.editor.defineTheme('sqlflow-juju', {
            base: 'vs-dark',
            inherit: true,
            rules: [
                { token: 'keyword.sql', foreground: 'f472b6', fontStyle: 'bold' },
                { token: 'comment.sql', foreground: '9b8f97', fontStyle: 'italic' },
                { token: 'string.sql', foreground: '4cbf7d' },
                { token: 'number.sql', foreground: 'e0a83f' }
            ],
            colors: {
                'editor.background': '#1f1b25',
                'editor.foreground': '#f3eef1',
                'editor.lineHighlightBackground': '#2a1522',
                'editorLineNumber.foreground': '#766b72',
                'editorLineNumber.activeForeground': '#f472b6',
                'editorCursor.foreground': '#f472b6',
                'editor.selectionBackground': '#3a1930',
                'editorWidget.background': '#1f1b25',
                'editorWidget.foreground': '#f3eef1',
                'editorWidget.border': '#3a3540',
                'widget.shadow': 'rgba(0, 0, 0, 0.4)',
                'input.background': '#1f1b25',
                'input.foreground': '#f3eef1',
                'input.border': '#3a3540',
                'inputOption.activeBackground': '#3a1930',
                'inputOption.activeBorder': '#ec4899',
                'inputOption.activeForeground': '#f472b6',
                'inputValidation.errorBackground': '#3a1f1c',
                'inputValidation.errorBorder': '#e2685c',
                'inputValidation.errorForeground': '#e2685c',
                'editor.findMatchBackground': '#ffd600',
                'editor.findMatchBorder': '#c79a00',
                'editor.findMatchHighlightBackground': 'rgba(255, 214, 0, 0.28)',
                'editor.findMatchHighlightBorder': 'rgba(255, 214, 0, 0.6)',
                'editor.findRangeHighlightBackground': 'rgba(255, 214, 0, 0.12)',
                'toolbar.hoverBackground': '#272330',
                'icon.foreground': '#9b8f97',
                'focusBorder': '#ec4899'
            }
        });

        // Tema "GreenWeslin": variante clara (branco e cinzas neutros, como o tema padrão) com
        // acento verde — mesma paleta do tema geral do app (ver :root[data-theme="green-weslin"]
        // em app.css).
        monaco.editor.defineTheme('sqlflow-green-weslin', {
            base: 'vs',
            inherit: true,
            rules: [
                { token: 'keyword.sql', foreground: '16a34a', fontStyle: 'bold' },
                { token: 'comment.sql', foreground: '6b7785', fontStyle: 'italic' },
                { token: 'string.sql', foreground: 'b8860b' },
                { token: 'number.sql', foreground: '1d4ed8' }
            ],
            colors: {
                'editor.background': '#ffffff',
                'editor.foreground': '#1f2933',
                'editor.lineHighlightBackground': '#f0fdf4',
                'editorLineNumber.foreground': '#9aa5b1',
                'editorLineNumber.activeForeground': '#16a34a',
                'editorCursor.foreground': '#16a34a',
                'editor.selectionBackground': '#dcfce7',
                'editorWidget.background': '#ffffff',
                'editorWidget.foreground': '#1f2933',
                'editorWidget.border': '#dfe4ea',
                'widget.shadow': 'rgba(22, 33, 44, 0.16)',
                'input.background': '#ffffff',
                'input.foreground': '#1f2933',
                'input.border': '#dfe4ea',
                'inputOption.activeBackground': '#dcfce7',
                'inputOption.activeBorder': '#22c55e',
                'inputOption.activeForeground': '#16a34a',
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
                'focusBorder': '#22c55e'
            }
        });

        // Tema "GreenWeslinDark": preto e cinzas neutros com acento verde claro (versão escura do
        // GreenWeslin) — mesma paleta do tema geral do app (ver :root[data-theme="green-weslin-dark"]
        // em app.css).
        monaco.editor.defineTheme('sqlflow-green-weslin-dark', {
            base: 'vs-dark',
            inherit: true,
            rules: [
                { token: 'keyword.sql', foreground: '4ade80', fontStyle: 'bold' },
                { token: 'comment.sql', foreground: '8f9b91', fontStyle: 'italic' },
                { token: 'string.sql', foreground: 'e0a83f' },
                { token: 'number.sql', foreground: '7dd3fc' }
            ],
            colors: {
                'editor.background': '#000000',
                'editor.foreground': '#f8f8f2',
                'editor.lineHighlightBackground': '#0a0a0a',
                'editorLineNumber.foreground': '#555b62',
                'editorLineNumber.activeForeground': '#4ade80',
                'editorCursor.foreground': '#4ade80',
                'editor.selectionBackground': '#163a20',
                'editorWidget.background': '#000000',
                'editorWidget.foreground': '#f8f8f2',
                'editorWidget.border': '#333333',
                'widget.shadow': 'rgba(0, 0, 0, 0.4)',
                'input.background': '#000000',
                'input.foreground': '#f8f8f2',
                'input.border': '#333333',
                'inputOption.activeBackground': '#163a20',
                'inputOption.activeBorder': '#4ade80',
                'inputOption.activeForeground': '#86efac',
                'inputValidation.errorBackground': '#3a1f1c',
                'inputValidation.errorBorder': '#e2685c',
                'inputValidation.errorForeground': '#e2685c',
                'editor.findMatchBackground': '#ffd600',
                'editor.findMatchBorder': '#c79a00',
                'editor.findMatchHighlightBackground': 'rgba(255, 214, 0, 0.28)',
                'editor.findMatchHighlightBorder': 'rgba(255, 214, 0, 0.6)',
                'editor.findRangeHighlightBackground': 'rgba(255, 214, 0, 0.12)',
                'toolbar.hoverBackground': '#1a1a1a',
                'icon.foreground': '#737b84',
                'focusBorder': '#4ade80'
            }
        });
    }

    // O tema do editor sempre segue o tema geral do app (Claro/Escuro/Freshcut Contrast) — cada um
    // já tem um tema de editor com as cores correspondentes (ver defineThemes).
    function resolveInitialEditorTheme() {
        var mode = resolveColorMode();
        if (mode === 'freshcut-contrast') return 'freshcut-contrast';
        if (mode === 'juju') return 'sqlflow-juju';
        if (mode === 'green-weslin') return 'sqlflow-green-weslin';
        if (mode === 'green-weslin-dark') return 'sqlflow-green-weslin-dark';
        return mode === 'dark' ? 'sqlflow-dark' : 'sqlflow';
    }

    // Lê o tema atual direto do atributo aplicado pelo sqlflowTheme (theme.js) — assim o Monaco
    // nasce já na cor certa mesmo se ele inicializar depois da troca de tema, sem precisar coordenar
    // ordem de carregamento entre os dois scripts.
    function resolveColorMode() {
        var attr = document.documentElement.getAttribute('data-theme');
        return (attr === 'dark' || attr === 'freshcut-contrast' || attr === 'juju' || attr === 'green-weslin' || attr === 'green-weslin-dark') ? attr : 'light';
    }

    // Chamado pelo sqlflowTheme.apply (theme.js) toda vez que o usuário troca claro/escuro nas
    // Configurações — se o Monaco ainda não foi inicializado (nenhuma aba de query aberta ainda),
    // é um no-op seguro: o init() abaixo já nasce com a cor certa via resolveColorMode().
    function setColorTheme(mode) {
        if (typeof monaco === 'undefined' || !monaco.editor) return;
        if (mode === 'freshcut-contrast') {
            monaco.editor.setTheme('freshcut-contrast');
            return;
        }
        if (mode === 'juju') {
            monaco.editor.setTheme('sqlflow-juju');
            return;
        }
        if (mode === 'green-weslin') {
            monaco.editor.setTheme('sqlflow-green-weslin');
            return;
        }
        if (mode === 'green-weslin-dark') {
            monaco.editor.setTheme('sqlflow-green-weslin-dark');
            return;
        }
        monaco.editor.setTheme(mode === 'dark' ? 'sqlflow-dark' : 'sqlflow');
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

        // Manda a linha inicial de verdade (não recalculada depois em C#) porque a decisão de onde o
        // texto começa é diferente daqui pra getExecutionStartLine: com seleção, é a linha da seleção;
        // sem seleção, é o início do BLOCO sob o cursor (não a linha 1 do documento inteiro, que é o
        // que o botão Executar manda nesse caso) — sem isso o sublinhado de erro (setErrorMarker)
        // saía deslocado sempre que havia mais statements empilhados acima no mesmo editor.
        let sql = selectedText;
        let startLine = 1;
        if (sql) {
            startLine = editor.getSelection().startLineNumber;
        } else {
            const model = models[activeTabId];
            const position = editor.getPosition();
            if (model && position) {
                startLine = expandToStatementBlock(model, position.lineNumber).startLine;
            }
            sql = getStatementAtCursor(activeTabId);
        }
        if (!sql) {
            execDotNetRef.invokeMethodAsync('NotifyNoStatementToRunAsync');
            return;
        }
        execDotNetRef.invokeMethodAsync('ExecuteStatementFromEditorAsync', activeTabId, sql, startLine);
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
                    console.error('[SQLFlow] sqlflowMonaco.init: elemento #' + containerId + ' não existe no DOM ainda.');
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
                    suggestOnTriggerCharacters: true,
                    fixedOverflowWidgets: true
                });
                editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, runActiveStatement);
                editor.addCommand(monaco.KeyCode.F5, runActiveStatement);
                editor.onDidChangeCursorSelection(notifySelectionChange);
                editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyF, function () {
                    formatAtCursor(activeTabId);
                });
                // Fallback pro Ctrl+/ (comentar/descomentar a seleção): o binding padrão do Monaco usa o
                // código FÍSICO da tecla (posição do "/" no layout norte-americano) — em teclado ABNT2
                // (BR) o "/" fica em outra tecla física, então esse binding nunca dispara. Aqui detecta
                // pelo CARACTERE produzido (e.key === '/'), que funciona em qualquer layout, e só age se
                // o binding padrão ainda não tiver tratado o evento (senão comentaria em dobro nos
                // layouts onde ele já funciona).
                editor.onKeyDown(function (e) {
                    const browserEvent = e.browserEvent;
                    if (browserEvent.defaultPrevented) return;
                    if (!(browserEvent.ctrlKey || browserEvent.metaKey) || browserEvent.shiftKey || browserEvent.altKey) return;
                    if (browserEvent.key !== '/') return;
                    e.preventDefault();
                    e.stopPropagation();
                    editor.trigger('sqlflow', 'editor.action.commentLine', null);
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
        console.log('[SQLFlow] aba ativa=' + tabId + ' conexao=' + activeConnectionId);
        let model = models[tabId];
        if (!model) {
            model = monaco.editor.createModel(uppercaseRespectingComments(initialValue || '', 'normal'), 'sql');
            attachUppercaseTransform(model);
            // Avisa o C# que essa aba tem alterações não salvas assim que o texto muda (digitação,
            // formatação, colar etc) — usado pra perguntar "salvar antes de fechar?" igual o DBeaver.
            // Também limpa o sublinhado de erro de execução anterior (ver setErrorMarker) — depois que
            // a pessoa mexeu no texto, o erro antigo não corresponde mais ao que está escrito ali.
            model.onDidChangeContent(function () {
                monaco.editor.setModelMarkers(model, 'sqlflow-exec-error', []);
                if (execDotNetRef) execDotNetRef.invokeMethodAsync('MarkTabDirty', tabId);
            });
            models[tabId] = model;
        }
        editor.setModel(model);
        editor.focus();
        // Model novo nasce sem seleção — força reavisar o C# (senão a contagem da aba anterior
        // ficaria exibida até o usuário mexer o mouse/cursor nessa aba).
        lastSelectionLength = -1;
        notifySelectionChange();
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

    // Linha (1-based, no documento inteiro) onde começa o texto que getExecutionValue vai rodar —
    // MESMA decisão dela (seleção manual vence; sem seleção manda o editor INTEIRO, a partir da linha
    // 1 — não o statement sob o cursor, que é só o comportamento do Ctrl+Enter/runActiveStatement).
    // Usado só pelo botão "Executar" (ExecuteAsync) pra traduzir a linha relativa que o banco aponta
    // num erro (relativa só ao texto enviado) pra linha de verdade no editor (ver setErrorMarker) —
    // precisa ser chamado ANTES de rodar a consulta, com a seleção ainda intacta. O caminho do
    // Ctrl+Enter (runActiveStatement) já manda sua própria linha inicial direto pro C#, sem passar
    // por aqui, porque a decisão de onde começa o texto é diferente nesse caso.
    function getExecutionStartLine(tabId) {
        const model = models[tabId];
        if (!model || !editor || editor.getModel() !== model) return 1;
        const selection = editor.getSelection();
        return selection && !selection.isEmpty() ? selection.startLineNumber : 1;
    }

    // Sublinha em vermelho (squiggly, igual erro de compilação) a linha do editor onde o banco apontou
    // o erro, e rola a tela até ela ficar visível — sem isso, o erro só aparecia no modal/painel,
    // exigindo contar linha manualmente pra achar o trecho problemático na consulta de verdade.
    function setErrorMarker(tabId, line, message) {
        const model = models[tabId];
        if (!model || !line || line < 1 || line > model.getLineCount()) return;
        monaco.editor.setModelMarkers(model, 'sqlflow-exec-error', [{
            startLineNumber: line,
            startColumn: 1,
            endLineNumber: line,
            endColumn: model.getLineMaxColumn(line),
            message: message || 'Erro ao executar a consulta',
            severity: monaco.MarkerSeverity.Error
        }]);
        if (editor && editor.getModel() === model) {
            editor.revealLineInCenterIfOutsideViewport(line);
        }
    }

    // Limpa o sublinhado de erro — chamado no início de toda nova execução (ver ExecuteSqlAsync),
    // pra um erro antigo não continuar marcado depois que a consulta rodou com sucesso.
    function clearErrorMarker(tabId) {
        const model = models[tabId];
        if (model) monaco.editor.setModelMarkers(model, 'sqlflow-exec-error', []);
    }

    // Tamanho (em caracteres) do texto selecionado agora, sem aparar espaços — é literalmente o que
    // está destacado na tela, usado só pra exibir "N caracteres selecionados" no editor-toolbar.
    function getSelectionLength(tabId) {
        const model = models[tabId];
        if (!model || !editor || editor.getModel() !== model) return 0;
        const selection = editor.getSelection();
        if (!selection || selection.isEmpty()) return 0;
        return model.getValueInRange(selection).length;
    }

    // Avisa o C# (QueryTabsPanel.OnEditorSelectionChanged) quando o tamanho da seleção muda — só
    // manda quando o valor realmente muda, pra não spammar invokeMethodAsync a cada pixel do mouse.
    function notifySelectionChange() {
        if (!execDotNetRef || !activeTabId) return;
        const length = getSelectionLength(activeTabId);
        if (length === lastSelectionLength) return;
        lastSelectionLength = length;
        execDotNetRef.invokeMethodAsync('OnEditorSelectionChanged', activeTabId, length);
    }

    // Palavras que iniciam um comando SQL novo — usado pra decidir se uma linha em branco separa
    // dois comandos empilhados de verdade, ou é só um respiro entre cláusulas (JOIN/WHERE/ON/...)
    // do MESMO comando. Sem essa distinção, uma consulta só formatada com linha em branco entre
    // cláusulas (estilo comum) era fatiada em vários "comandos" falsos.
    const STATEMENT_START_RE = /^\s*(SELECT|INSERT|UPDATE|DELETE|MERGE|WITH|CREATE|ALTER|DROP|TRUNCATE|BEGIN|DECLARE|EXEC|EXECUTE|CALL|GRANT|REVOKE|COMMIT|ROLLBACK)\b/i;

    function isStatementStartLine(lineText) {
        return STATEMENT_START_RE.test(lineText);
    }

    // Palavras que só aparecem CONTINUANDO um comando já iniciado (WHERE/AND/JOIN/ORDER BY/...) —
    // usado junto com isStatementStartLine pra decidir se uma linha em branco é fronteira de
    // verdade entre dois comandos. Sem isso, texto solto sem nenhuma cara de SQL (um número perdido,
    // uma nota, lixo de copiar/colar) deixado embaixo da consulta — separado só por uma linha em
    // branco — era tratado como se fosse "continuação" e ia junto no F5/Ctrl+Enter, quebrando o SQL
    // enviado ao banco (ex.: ORA-00933, comando SQL não encerrado adequadamente).
    const CONTINUATION_START_RE = /^\s*(WHERE|AND|OR|ON|GROUP\s+BY|ORDER\s+BY|HAVING|UNION(\s+ALL)?|INTERSECT|MINUS|SET|VALUES|FROM|JOIN|INNER|LEFT|RIGHT|OUTER|FULL|CROSS|WHEN|THEN|ELSE|CONNECT\s+BY|START\s+WITH|FOR\s+UPDATE)\b/i;

    // Uma linha só é fronteira genuína entre dois comandos se a linha em questão inicia um comando
    // novo OU se ela nem de longe parece continuação de cláusula do comando anterior — nesse segundo
    // caso, mesmo sem ela "começar" nada reconhecível, ainda encerra o comando ali (é mais seguro
    // cortar fora do que arrastar lixo pro banco).
    // Exceção: se a linha não-vazia ANTERIOR à linha em branco é ela mesma uma cláusula "pendurada"
    // (ex.: "WHERE" ou "AND" sozinho na linha, com a condição só na linha seguinte — formatação comum),
    // a linha em branco nunca separa comandos, não importa como a próxima linha começa. Sem isso, um
    // WHERE cuja primeira condição vem numa linha própria (sem AND/OR na frente, já que o WHERE já
    // "abriu" a cláusula) era lido como fim de um comando e início de outro.
    function isGenuineSeparatorLine(lineText, prevNonBlankLine) {
        if (isStatementStartLine(lineText)) return true;
        if (prevNonBlankLine !== undefined && CONTINUATION_START_RE.test(prevNonBlankLine)) return false;
        return !CONTINUATION_START_RE.test(lineText);
    }

    // Quebra um texto em vários statements só por ';' — cada comando precisa terminar
    // explicitamente com ';' pra contar como "mais de uma consulta". Antes também tentava adivinhar
    // fronteira por linha em branco (mesma heurística de isGenuineSeparatorLine/expandToStatementBlock),
    // mas uma consulta SÓ, formatada com blocos de colunas separados por comentário + linha em branco
    // (comum em SELECTs grandes, ex.: "-- VALOR-CHAVE" seguido de linha em branco entre seções), acabava
    // sendo fatiada em vários "comandos" falsos — cada bloco de comentário parecia início de um comando
    // novo, disparando a execução paralela (e o limite de MaxParallelQueries) numa única SELECT.
    function splitIntoStatements(text) {
        return text.split(';')
            .map(function (part) { return part.trim(); })
            .filter(function (part) { return part.length > 0; });
    }

    // Acha o bloco de linhas do comando SQL onde `lineNumber` está, tratando linha em branco como
    // fronteira só quando a próxima linha não-vazia inicia um comando novo (mesmo critério de
    // splitIntoStatements) — senão a linha em branco só separa cláusulas do mesmo comando.
    function expandToStatementBlock(model, lineNumber) {
        const lineCount = model.getLineCount();

        function prevNonBlankLineContent(fromLine) {
            let p = fromLine - 1;
            while (p >= 1 && model.getLineContent(p).trim() === '') p--;
            return p >= 1 ? model.getLineContent(p) : undefined;
        }

        function isGenuineBlankSeparator(blankLine) {
            let next = blankLine + 1;
            while (next <= lineCount && model.getLineContent(next).trim() === '') next++;
            if (next > lineCount) return false;
            return isGenuineSeparatorLine(model.getLineContent(next), prevNonBlankLineContent(blankLine));
        }

        let startLine = lineNumber;
        while (startLine > 1) {
            const prevLine = startLine - 1;
            if (model.getLineContent(prevLine).trim() === '' && isGenuineBlankSeparator(prevLine)) break;
            startLine--;
        }

        let endLine = lineNumber;
        while (endLine < lineCount) {
            const nextLine = endLine + 1;
            if (model.getLineContent(nextLine).trim() === '' && isGenuineBlankSeparator(nextLine)) break;
            endLine++;
        }

        return { startLine: startLine, endLine: endLine };
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

        const { startLine, endLine } = expandToStatementBlock(model, position.lineNumber);

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

        const { startLine, endLine } = expandToStatementBlock(model, position.lineNumber);

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
        editor.executeEdits('sqlflow-format', [{ range: range, text: formatted }]);
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

    return { init, setActiveTab, getValue, getExecutionValue, getExecutionStartLine, setErrorMarker, clearErrorMarker, getSelectedStatements, getStatementAtCursor, setValue, closeTab, setColorTheme };
})();
