namespace DevelopTi.Shared.Services;

/// <summary>
/// Sinaliza quando é seguro fazer a primeira chamada de JS interop. No MAUI, o BlazorWebView nativo
/// pode ainda não estar anexado no instante do primeiro OnAfterRenderAsync, o que faz o JS interop
/// falhar com "Cannot invoke JavaScript outside of a WebView context.". Nos hosts que já são um
/// navegador de verdade (Web/Web.Client), não existe essa corrida, então ficam prontos de imediato.
/// </summary>
public static class WebViewReadyGate
{
    private static readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task Ready => _tcs.Task;

    public static void MarkReady() => _tcs.TrySetResult();
}
