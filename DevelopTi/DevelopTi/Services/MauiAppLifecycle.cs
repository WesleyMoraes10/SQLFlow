using DevelopTi.Shared.Services;

namespace DevelopTi.Services
{
    public class MauiAppLifecycle : IAppLifecycle
    {
        public void Exit()
        {
            Application.Current?.Quit();
        }
    }
}
