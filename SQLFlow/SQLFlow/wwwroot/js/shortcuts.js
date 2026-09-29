// Atalhos globais do workspace (funcionam com o foco em qualquer lugar da tela — árvore, editor,
// grid de resultado — diferente dos atalhos do Monaco em monaco-interop.js, que só disparam com o
// foco dentro do próprio editor SQL).
window.sqlflowShortcuts = (function () {
    let queryTabsRef = null;

    function registerQueryTabsRef(dotNetRef) {
        queryTabsRef = dotNetRef;
    }

    // Posição pro menu "Qual será a conexão?" (só aparece com mais de uma conexão cadastrada) — usa o
    // canto do botão "+" da barra de abas como referência, igual clicar nele; sem nenhuma aba aberta
    // ainda esse botão não existe na tela, então cai num ponto fixo perto do topo.
    function menuAnchorPosition() {
        var addButton = document.querySelector('.query-tab-add');
        if (addButton) {
            var rect = addButton.getBoundingClientRect();
            return { x: rect.left, y: rect.bottom };
        }
        return { x: 24, y: 90 };
    }

    document.addEventListener('keydown', function (e) {
        // Ctrl+] (Cmd+] no mac): nova aba de script. Checa pelo CARACTERE produzido (e.key), não pelo
        // código físico da tecla — mesmo motivo do fallback de Ctrl+/ no Monaco (ver monaco-interop.js).
        if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && e.key === ']') {
            e.preventDefault();
            if (queryTabsRef) {
                var pos = menuAnchorPosition();
                queryTabsRef.invokeMethodAsync('RequestNewScriptTabAsync', pos.x, pos.y);
            }
        }
    });

    return { registerQueryTabsRef };
})();
