using System.Windows;
using Pemail.UI;

namespace Pemail
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ThemeManager.LoadAndApply();
        }
    }
}
