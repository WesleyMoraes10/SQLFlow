using DevelopTi.Shared.Services;
using DevelopTi.Web.Components;
using DevelopTi.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

// Add device-specific services used by the DevelopTi.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();
builder.Services.AddSingleton<IAppLifecycle, WebAppLifecycle>();
builder.Services.AddScoped<ThemeService>();

// Este host já é um navegador de verdade: não existe a corrida de inicialização do WebView do MAUI.
WebViewReadyGate.MarkReady();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(
        typeof(DevelopTi.Shared._Imports).Assembly,
        typeof(DevelopTi.Web.Client._Imports).Assembly);

app.Run();
