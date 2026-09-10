using DevelopTi.Shared.Services;
using DevelopTi.Web.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Add device-specific services used by the DevelopTi.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();
builder.Services.AddSingleton<IAppLifecycle, WebAppLifecycle>();
builder.Services.AddScoped<ThemeService>();

// Este host já é um navegador de verdade: não existe a corrida de inicialização do WebView do MAUI.
WebViewReadyGate.MarkReady();

await builder.Build().RunAsync();
