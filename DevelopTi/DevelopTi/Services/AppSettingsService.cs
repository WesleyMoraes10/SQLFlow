namespace DevelopTi.Services;

/// <summary>Preferências gerais do app, persistidas via Preferences.Default (chave-valor nativo do
/// MAUI) — diferente do ThemeService porque não precisa de JS interop (não mexe em atributo/CSS do
/// DOM, só num valor lido pelos componentes Razor).</summary>
public class AppSettingsService
{
    private const string ParallelResultsHorizontalKey = "developti.parallel-results-horizontal";

    public bool ParallelResultsHorizontal { get; private set; } = Preferences.Default.Get(ParallelResultsHorizontalKey, true);

    public event Action? Changed;

    public void SetParallelResultsHorizontal(bool horizontal)
    {
        if (ParallelResultsHorizontal == horizontal) return;

        ParallelResultsHorizontal = horizontal;
        Preferences.Default.Set(ParallelResultsHorizontalKey, horizontal);
        Changed?.Invoke();
    }
}
