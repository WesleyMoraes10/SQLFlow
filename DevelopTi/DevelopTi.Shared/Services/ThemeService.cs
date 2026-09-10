using Microsoft.JSInterop;

namespace DevelopTi.Shared.Services;

/// <summary>Controla o tema de aparência (claro/escuro) aplicado ao documento, persistido no navegador/WebView.</summary>
public class ThemeService
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string FreshcutContrast = "freshcut-contrast";

    private readonly IJSRuntime _js;

    public ThemeService(IJSRuntime js)
    {
        _js = js;
    }

    public string CurrentTheme { get; private set; } = Light;

    public event Action? ThemeChanged;

    public async Task InitializeAsync()
    {
        // Aguarda o WebView nativo (MAUI) terminar de anexar antes da primeira chamada de JS interop;
        // nos hosts que já são navegador (Web/Web.Client) o gate já está liberado. O timeout é só uma
        // rede de segurança caso o evento de inicialização nunca dispare por algum motivo.
        await Task.WhenAny(WebViewReadyGate.Ready, Task.Delay(8000));

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var stored = await _js.InvokeAsync<string?>("developtiTheme.get");
                CurrentTheme = stored is Dark or FreshcutContrast ? stored : Light;
                await _js.InvokeVoidAsync("developtiTheme.apply", CurrentTheme);
                return;
            }
            catch when (attempt < maxAttempts)
            {
                await Task.Delay(200);
            }
        }
    }

    public async Task SetThemeAsync(string theme)
    {
        CurrentTheme = theme is Dark or FreshcutContrast ? theme : Light;
        try
        {
            await _js.InvokeVoidAsync("developtiTheme.apply", CurrentTheme);
        }
        catch
        {
            // Ignora falha de interop pontual: o tema já foi atualizado em memória e na UI.
        }
        ThemeChanged?.Invoke();
    }
}
