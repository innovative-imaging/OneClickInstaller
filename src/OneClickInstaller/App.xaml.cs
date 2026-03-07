using System;
using System.Windows;

namespace OneClickInstaller
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Check if running as administrator
            if (!IsRunningAsAdministrator())
            {
                MessageBox.Show(
                    "This installer requires Administrator privileges to install software.\n\n" +
                    "Please right-click the installer and select 'Run as administrator'.",
                    "Administrator Rights Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                
                Application.Current.Shutdown();
                return;
            }
        }

        private static bool IsRunningAsAdministrator()
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}