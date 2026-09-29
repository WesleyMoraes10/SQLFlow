using SQLFlow.Shared.Services;

namespace SQLFlow.Web.Client.Services
{
    /// <summary>"Sair" não se aplica a uma página web rodando no navegador; fica sem ação neste host.</summary>
    public class WebAppLifecycle : IAppLifecycle
    {
        public void Exit()
        {
        }
    }
}
