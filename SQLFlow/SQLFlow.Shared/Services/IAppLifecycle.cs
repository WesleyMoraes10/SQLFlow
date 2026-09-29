namespace SQLFlow.Shared.Services;

/// <summary>Encerra o aplicativo. No MAUI, fecha a janela/processo; nos hosts web não faz sentido e é um no-op.</summary>
public interface IAppLifecycle
{
    void Exit();
}
