window.developtiTheme = (function () {
    var STORAGE_KEY = 'developti-theme';

    function get() {
        try {
            return localStorage.getItem(STORAGE_KEY);
        } catch (e) {
            return null;
        }
    }

    function apply(theme) {
        var resolved = (theme === 'dark' || theme === 'freshcut-contrast') ? theme : 'light';
        document.documentElement.setAttribute('data-theme', resolved);
        try {
            localStorage.setItem(STORAGE_KEY, resolved);
        } catch (e) { /* armazenamento indisponível: tema ainda é aplicado nesta sessão */ }
        // Monaco não lê variáveis CSS — precisa ser avisado explicitamente pra trocar de tema junto
        // com o resto do app. Se nenhuma aba de query foi aberta ainda, isso é um no-op seguro: o
        // editor nasce com a cor certa de qualquer forma (ver resolveColorMode em monaco-interop.js).
        if (window.developtiMonaco && typeof window.developtiMonaco.setColorTheme === 'function') {
            window.developtiMonaco.setColorTheme(resolved);
        }
    }

    return { get: get, apply: apply };
})();
