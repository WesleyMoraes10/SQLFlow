using SQLFlow.Shared.Services;
#if WINDOWS
using Microsoft.UI.Xaml;
using SQLFlow.Services;
#endif

namespace SQLFlow
{
    public partial class MainPage : ContentPage
    {
        public MainPage()
        {
            InitializeComponent();
#if WINDOWS
            Loaded += (_, _) => HookWindowClosing();
#endif
            blazorWebView.BlazorWebViewInitialized += (_, e) =>
            {
                WebViewReadyGate.MarkReady();
                // Sem isso o WebView2 trata F5/Ctrl+R como "recarregar página" antes mesmo do
                // keydown chegar no Monaco — o editor SQL usa F5 pra rodar o comando (ver
                // monaco-interop.js), então o atalho nativo do navegador precisa ficar desligado.
                e.WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

#if WINDOWS
                // O WebView2 (Microsoft.UI.Xaml.Controls.WebView2) não expõe nenhum jeito de tratar só
                // uma tecla de atalho (não tem CoreWebView2Controller/AcceleratorKeyPressed nessa versão
                // do controle XAML) — desligar AreBrowserAcceleratorKeysEnabled acima desliga F12 junto
                // com F5, e nada mais assume o F12 depois disso. Aqui a gente assume esse atalho na mão,
                // como um KeyboardAccelerator no elemento raiz da janela, chamando OpenDevToolsWindow
                // direto (o mesmo que "Inspecionar" no menu de contexto chamaria).
                if (e.WebView.XamlRoot?.Content is UIElement root)
                {
                    var devToolsAccelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.F12 };
                    devToolsAccelerator.Invoked += (_, args) =>
                    {
                        e.WebView.CoreWebView2.OpenDevToolsWindow();
                        args.Handled = true;
                    };
                    root.KeyboardAccelerators.Add(devToolsAccelerator);
                }
#endif
            };
        }

#if WINDOWS
        private bool _closingHooked;
        private bool _allowClose;

        // Intercepta o fechamento da janela (botão X) pra perguntar antes sobre abas de SQL não
        // salvas, igual o DBeaver faz — sem isso o app fechava direto e descartava qualquer script
        // ainda não salvo sem avisar nada.
        private void HookWindowClosing()
        {
            if (_closingHooked) return;

            var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
            var window = mauiWindow?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (mauiWindow is null || window is null) return;
            _closingHooked = true;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            appWindow.Closing += (sender, e) =>
            {
                if (_allowClose) return;
                e.Cancel = true;
                _ = HandleClosingAsync(mauiWindow);
            };
        }

        // Roda fora do callback nativo do evento Closing — deixar uma continuação assíncrona presa
        // direto num handler "async void" de evento WinRT derrubou o processo inteiro (exceção
        // escapando por um callback nativo = STATUS_FATAL_USER_CALLBACK_EXCEPTION) da primeira vez
        // que testei fechar pelo X. Qualquer erro aqui deixa o app fechar mesmo assim, em vez de
        // travar numa janela que não fecha nem abre de novo.
        private async Task HandleClosingAsync(Microsoft.Maui.Controls.Window mauiWindow)
        {
            var canClose = true;
            try
            {
                var tabState = IPlatformApplication.Current?.Services.GetService<QueryTabState>();
                canClose = tabState?.ConfirmCloseHandler is null || await tabState.ConfirmCloseHandler();
            }
            catch
            {
                canClose = true;
            }

            if (!canClose) return;

            // Fechar a AppWindow direto (Destroy/Close) apanhava o próprio MAUI no meio do próprio
            // processamento de mensagem nativa da janela (NavigationRootManager.SetTitleBarVisibility
            // reconsultando essa AppWindow) e derrubava o processo com ArgumentException. Passar pelo
            // Application.CloseWindow do MAUI deixa o próprio MAUI coordenar essa sequência de
            // fechamento do jeito que ele espera.
            _allowClose = true;
            Microsoft.Maui.Controls.Application.Current?.CloseWindow(mauiWindow);
        }
#endif
    }
}
