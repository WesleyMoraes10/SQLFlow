window.developtiResizer = (function () {
    function clamp(value, min, max) {
        return Math.min(Math.max(value, min), max);
    }

    function initHorizontal(handleId, targetId, options) {
        var handle = document.getElementById(handleId);
        var target = document.getElementById(targetId);
        if (!handle || !target || handle.dataset.resizerBound) return;
        handle.dataset.resizerBound = '1';

        var min = (options && options.min) || 160;
        var max = (options && options.max) || 600;

        handle.addEventListener('mousedown', function (e) {
            e.preventDefault();
            var startX = e.clientX;
            var startWidth = target.getBoundingClientRect().width;
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';

            function onMove(ev) {
                var newWidth = clamp(startWidth + (ev.clientX - startX), min, max);
                target.style.width = newWidth + 'px';
            }
            function onUp() {
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
            }
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
        });
    }

    function initVertical(handleId, targetId, options) {
        var handle = document.getElementById(handleId);
        var target = document.getElementById(targetId);
        if (!handle || !target || handle.dataset.resizerBound) return;
        handle.dataset.resizerBound = '1';

        var min = (options && options.min) || 100;
        var max = (options && options.max) || 800;

        handle.addEventListener('mousedown', function (e) {
            e.preventDefault();
            var startY = e.clientY;
            var startHeight = target.getBoundingClientRect().height;
            document.body.style.cursor = 'row-resize';
            document.body.style.userSelect = 'none';

            function onMove(ev) {
                var newHeight = clamp(startHeight + (ev.clientY - startY), min, max);
                target.style.height = newHeight + 'px';
            }
            function onUp() {
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
            }
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
        });
    }

    function initCollapseToggle(toggleId, targetId, options) {
        var toggleBtn = document.getElementById(toggleId);
        var target = document.getElementById(targetId);
        if (!toggleBtn || !target || toggleBtn.dataset.resizerBound) return;
        toggleBtn.dataset.resizerBound = '1';

        var defaultWidth = (options && options.defaultWidth) || 280;
        var lastWidth = target.getBoundingClientRect().width || defaultWidth;
        var collapsed = false;

        // Impede que o clique no botão também dispare o arraste do divisor por baixo dele.
        toggleBtn.addEventListener('mousedown', function (e) {
            e.stopPropagation();
        });

        toggleBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            collapsed = !collapsed;
            if (collapsed) {
                var currentWidth = target.getBoundingClientRect().width;
                if (currentWidth > 0) lastWidth = currentWidth;
                target.style.width = '0px';
                target.style.borderRightWidth = '0px';
                toggleBtn.classList.add('collapsed');
                toggleBtn.title = 'Mostrar árvore de conexões';
            } else {
                target.style.width = lastWidth + 'px';
                target.style.borderRightWidth = '';
                toggleBtn.classList.remove('collapsed');
                toggleBtn.title = 'Esconder árvore de conexões';
            }
        });
    }

    // Cresce a largura do alvo pra caber o item mais largo do conteúdo (ex: nome de conexão + host:porta),
    // sem passar do "max" — só aumenta, nunca encolhe o que a pessoa já ajustou arrastando na mão.
    function autoFitWidth(targetId, options) {
        var target = document.getElementById(targetId);
        if (!target) return;

        var min = (options && options.min) || 200;
        var max = (options && options.max) || 520;
        var padding = (options && options.padding) || 0;

        var previousWidth = target.style.width;
        var previousMaxWidth = target.style.maxWidth;
        target.style.maxWidth = 'none';
        target.style.width = 'max-content';
        var natural = target.scrollWidth + padding;
        target.style.width = previousWidth;
        target.style.maxWidth = previousMaxWidth;

        var current = target.getBoundingClientRect().width;
        var fitted = Math.min(max, Math.max(min, natural));
        if (fitted > current) {
            target.style.width = fitted + 'px';
        }
    }

    return { initHorizontal: initHorizontal, initVertical: initVertical, initCollapseToggle: initCollapseToggle, autoFitWidth: autoFitWidth };
})();
