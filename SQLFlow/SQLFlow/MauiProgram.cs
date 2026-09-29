using SQLFlow.Data.Abstractions;
using SQLFlow.Data.Providers;
using SQLFlow.Data.Services;
using SQLFlow.Services;
using SQLFlow.Shared.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;

namespace SQLFlow
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            // WebView2 renderiza por padrão com aceleração de GPU (DirectComposition). Ferramentas de
            // print de tela baseadas em BitBlt/GDI legado (ex.: Lightshot) não leem esse tipo de janela
            // e falham em capturar a área do app - a Ferramenta de Captura nativa do Windows (que usa
            // Windows.Graphics.Capture) não tem esse problema. --disable-gpu força o WebView2 a
            // renderizar por software, trocando um pouco de performance de UI por compatibilidade com
            // essas ferramentas mais antigas. Precisa ser setado antes do CoreWebView2Environment ser
            // criado (aqui, antes até do builder), senão o WebView2 já sobe sem o argumento.
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-gpu");

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            // Add device-specific services used by the SQLFlow.Shared project
            builder.Services.AddSingleton<IFormFactor, FormFactor>();
            builder.Services.AddSingleton<IAppLifecycle, MauiAppLifecycle>();
            // Scoped (não Singleton): ThemeService injeta IJSRuntime, e no BlazorWebView do MAUI o
            // IJSRuntime "conectado de verdade" só existe no escopo de serviços da própria WebView.
            builder.Services.AddScoped<ThemeService>();

            builder.Services.AddMauiBlazorWebView();

            // Camada de acesso a dados (SQLFlow.Data)
            builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
            builder.Services.AddSingleton<OracleMetadataProvider>();
            builder.Services.AddSingleton<SqlServerMetadataProvider>();
            builder.Services.AddSingleton<MySqlMetadataProvider>();
            builder.Services.AddSingleton<SqliteMetadataProvider>();
            builder.Services.AddSingleton<IMetadataProviderFactory, MetadataProviderFactory>();
            builder.Services.AddSingleton<QueryExecutionService>();
            builder.Services.AddSingleton<IConnectionProfileStore>(
                _ => new JsonFileConnectionProfileStore(FileSystem.Current.AppDataDirectory));
            builder.Services.AddSingleton<IQueryHistoryStore>(
                _ => new JsonFileQueryHistoryStore(FileSystem.Current.AppDataDirectory));

            // Serviços específicos do MAUI
            builder.Services.AddSingleton<ICredentialStore, SecureCredentialStore>();
            builder.Services.AddSingleton<QueryTabState>();
            builder.Services.AddSingleton<SqlCompletionService>();
            builder.Services.AddSingleton<GridEditService>();
            builder.Services.AddSingleton<AppSettingsService>();
            builder.Services.AddSingleton<SqlFileService>();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            // Log em arquivo texto — não depende do DevTools do WebView2 (às vezes bloqueado por
            // política de grupo) nem de rodar o app via Visual Studio pra ver a janela de Saída.
            string logFilePath;
            try
            {
                logFilePath = Path.Combine(FileSystem.Current.AppDataDirectory, "sqlflow.log");
            }
            catch
            {
                logFilePath = Path.Combine(Path.GetTempPath(), "sqlflow.log");
            }
            builder.Logging.AddProvider(new FileLoggerProvider(logFilePath));

#if WINDOWS
            builder.ConfigureLifecycleEvents(events =>
            {
                events.AddWindows(windows => windows.OnWindowCreated(window =>
                {
                    // Adiado: maximizar em cima da criação da janela pode disputar com a inicialização
                    // assíncrona do WebView2 e deixar o BlazorWebView sem referência nativa válida
                    // (erro "Cannot invoke JavaScript outside of a WebView context." no primeiro JS interop).
                    window.DispatcherQueue.TryEnqueue(async () =>
                    {
                        await Task.Delay(500);
                        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
                        if (appWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                        {
                            presenter.Maximize();
                        }
                    });
                }));
            });
#endif

            return builder.Build();
        }
    }
}
