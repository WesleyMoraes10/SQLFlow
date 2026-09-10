window.developtiTable = (function () {
    function selectAll(tableId) {
        var table = document.getElementById(tableId);
        if (!table) return;

        var selection = window.getSelection();
        var range = document.createRange();
        range.selectNodeContents(table);
        selection.removeAllRanges();
        selection.addRange(range);
    }

    // Foca o input de edição da célula com o cursor no INÍCIO do texto, não no fim — o padrão do
    // .focus() deixaria o cursor no fim, o que "perde" o início de campos com muito texto.
    function focusInputStart(el) {
        if (!el) return;
        el.focus();
        try { el.setSelectionRange(0, 0); } catch (e) { /* tipos de input sem suporte a seleção (ex: number) */ }
        el.scrollLeft = 0;
    }

    return { selectAll: selectAll, focusInputStart: focusInputStart };
})();
