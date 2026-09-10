using DevelopTi.Shared.Services;
#if WINDOWS
using Microsoft.UI.Xaml;
#endif

namespace DevelopTi
{
    public partial class MainPage : ContentPage
    {
        public MainPage()
        {
            InitializeComponent();
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
    }
}
