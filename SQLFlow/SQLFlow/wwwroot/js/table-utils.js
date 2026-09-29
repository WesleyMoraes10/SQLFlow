window.sqlflowTable = (function () {
    function selectAll(tableId) {
        var table = document.getElementById(tableId);
        if (!table) return;

        var selection = window.getSelection();
        var range = document.createRange();
        range.selectNodeContents(table);
        selection.removeAllRanges();
        selection.addRange(range);
    }

    // Seleção de linha/célula na grid de resultado: aplicada 100% aqui no JS (troca só a classe CSS
    // do alvo), sem passar pelo ciclo de render do Blazor — clicar numa célula NÃO pode custar um
    // re-render da tabela inteira (centenas de linhas x dezenas de colunas), senão cada clique de
    // leitura na grid fica perceptivelmente lento. Clicar no "#" da linha seleciona a linha inteira
    // (igual DBeaver); clicar em qualquer outra célula seleciona só aquela célula. O clique ainda
    // avisa o C# (via invokeMethodAsync, que — ao contrário de @onclick do Blazor — não dispara
    // re-render sozinho) só pra guardar o que está selecionado, usado depois pelo Ctrl+C (copiar).
    var selectedRowEl = null;
    var selectedCellEl = null;
    var rowSelectDotNetRef = null;
    var rowSelectWired = false;

    function focusResultPanel() {
        var panel = document.querySelector('.result-panel');
        if (panel) panel.focus();
    }

    function selectRowEl(tr) {
        clearRowSelection();
        clearCellSelection();
        tr.classList.add('row-selected');
        selectedRowEl = tr;
        tr.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        focusResultPanel();
        if (rowSelectDotNetRef) {
            rowSelectDotNetRef.invokeMethodAsync('SetSelectedRowFromJs', parseInt(tr.getAttribute('data-row-index'), 10));
        }
    }

    function selectCellEl(cell) {
        var tr = cell.closest('tr[data-row-index]');
        var column = cell.getAttribute('data-column');
        if (!tr || column === null) return;
        clearRowSelection();
        clearCellSelection();
        cell.classList.add('cell-selected');
        selectedCellEl = cell;
        cell.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        focusResultPanel();
        if (rowSelectDotNetRef) {
            rowSelectDotNetRef.invokeMethodAsync('SetSelectedCellFromJs', parseInt(tr.getAttribute('data-row-index'), 10), column);
        }
    }

    function initRowSelection(dotNetRef) {
        rowSelectDotNetRef = dotNetRef;
        if (rowSelectWired) return;
        rowSelectWired = true;
        document.addEventListener('click', function (e) {
            if (!e.target.closest('#result-table')) return;
            var cell = e.target.closest('td');
            if (!cell || cell.classList.contains('row-actions-col')) return;
            var tr = cell.closest('tr[data-row-index]');
            if (!tr) return;

            if (cell.classList.contains('row-number-col')) {
                if (tr === selectedRowEl) return;
                selectRowEl(tr);
                return;
            }

            if (cell.getAttribute('data-column') === null || cell === selectedCellEl) return;
            selectCellEl(cell);
        });

        // Teclado no painel de resultado (setinhas pra navegar, Esc pra sair da tela cheia): tratado
        // 100% aqui em JS puro, de propósito — o .result-panel NÃO tem @onkeydown do Blazor (ver
        // comentário em QueryTabsPanel.razor perto de ExitResultFullscreenFromJs). Célula em edição
        // (input aberto) é a exceção: aí a tecla precisa mover o cursor de texto normalmente, então
        // deixamos passar sem interceptar.
        document.addEventListener('keydown', function (e) {
            if (e.target.classList && e.target.classList.contains('cell-edit-input')) return;
            var panel = e.target.closest('.result-panel');
            if (!panel) return;

            if (e.key === 'ArrowUp' || e.key === 'ArrowDown' || e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
                e.preventDefault();
                moveSelection(e.key);
                return;
            }

            if (e.key === 'Escape' && panel.classList.contains('result-panel-fullscreen') && rowSelectDotNetRef) {
                rowSelectDotNetRef.invokeMethodAsync('ExitResultFullscreenFromJs');
            }
        });
    }

    // Move a seleção de célula (ou de linha, se uma linha inteira estiver selecionada) pro vizinho na
    // direção da seta. Linhas de INSERT pendente (sem data-row-index) ficam de fora, mesma limitação
    // que já existia pro clique — essas células não têm data-column/data-row-index pra identificar
    // unicamente a posição.
    function nextDataRow(tr, forward) {
        var next = forward ? tr.nextElementSibling : tr.previousElementSibling;
        while (next && !next.hasAttribute('data-row-index')) {
            next = forward ? next.nextElementSibling : next.previousElementSibling;
        }
        return next;
    }

    function moveSelection(key) {
        var table = document.getElementById('result-table');
        if (!table) return;

        if (selectedCellEl) {
            var tr = selectedCellEl.closest('tr[data-row-index]');
            if (!tr) return;

            if (key === 'ArrowLeft' || key === 'ArrowRight') {
                var cellsInRow = Array.from(tr.querySelectorAll('td[data-column]'));
                var idx = cellsInRow.indexOf(selectedCellEl);
                var targetIdx = key === 'ArrowLeft' ? idx - 1 : idx + 1;
                if (targetIdx >= 0 && targetIdx < cellsInRow.length) selectCellEl(cellsInRow[targetIdx]);
                return;
            }

            if (key === 'ArrowUp' || key === 'ArrowDown') {
                var targetTr = nextDataRow(tr, key === 'ArrowDown');
                if (!targetTr) return;
                var column = selectedCellEl.getAttribute('data-column');
                var targetCell = targetTr.querySelector('td[data-column="' + CSS.escape(column) + '"]');
                if (targetCell) selectCellEl(targetCell);
            }
            return;
        }

        if (selectedRowEl) {
            if (key === 'ArrowUp' || key === 'ArrowDown') {
                var targetRow = nextDataRow(selectedRowEl, key === 'ArrowDown');
                if (targetRow) selectRowEl(targetRow);
            }
            return;
        }

        var firstCell = table.querySelector('tbody td[data-column]');
        if (firstCell) selectCellEl(firstCell);
    }

    function clearRowSelection() {
        if (selectedRowEl) {
            selectedRowEl.classList.remove('row-selected');
            selectedRowEl = null;
        }
    }

    function clearCellSelection() {
        if (selectedCellEl) {
            selectedCellEl.classList.remove('cell-selected');
            selectedCellEl = null;
        }
    }

    // Foca o input de edição da célula com o cursor no INÍCIO do texto, não no fim — o padrão do
    // .focus() deixaria o cursor no fim, o que "perde" o início de campos com muito texto.
    function focusInputStart(el) {
        if (!el) return;
        el.focus();
        try { el.setSelectionRange(0, 0); } catch (e) { /* tipos de input sem suporte a seleção (ex: number) */ }
        el.scrollLeft = 0;
    }

    return {
        selectAll: selectAll,
        focusInputStart: focusInputStart,
        initRowSelection: initRowSelection,
        clearRowSelection: clearRowSelection,
        clearCellSelection: clearCellSelection
    };
})();
