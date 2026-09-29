using SQLFlow.Shared.Services;

namespace SQLFlow.Web.Services
{
    /// <summary>"Sair" não se aplica a uma página web hospedada; fica sem ação neste host.</summary>
    public class WebAppLifecycle : IAppLifecycle
    {
        public void Exit()
        {
        }
    }
}
