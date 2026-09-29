using SQLFlow.Shared.Services;

namespace SQLFlow.Services
{
    public class MauiAppLifecycle : IAppLifecycle
    {
        public void Exit()
        {
            Application.Current?.Quit();
        }
    }
}
