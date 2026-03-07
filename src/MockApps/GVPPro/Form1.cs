using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GVPPro;

public partial class Form1 : Form
{
    // SHA256 hash of the service password "N1viH@rd0$Secure!"
    private const string ServicePasswordHash = "24E61F4F537AD5E44E89DB2D9F3B4A0287DE9AB30F1AF5CC624FB39C8A31A449";
    
    // ServiceMode.bat expected location
    private static readonly string ServiceModeBatPath = @"D:\GVP-Pro\App\ServiceMode.bat";
    private static readonly string ServiceModeBatFallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServiceMode.bat");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    private const uint EWX_LOGOFF = 0x00000000;
    private const uint EWX_FORCE = 0x00000004;

    public Form1()
    {
        InitializeComponent();
        btnService.Click += BtnService_Click;
        btnLogOff.Click += BtnLogOff_Click;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Reposition anchored controls for fullscreen
        if (pnlFooter != null && btnService != null)
        {
            btnService.Location = new Point(pnlFooter.ClientSize.Width - btnService.Width - 16, 8);
            btnLogOff.Location = new Point(btnService.Left - btnLogOff.Width - 8, 8);
            lblStatus.Location = new Point(pnlHeader.ClientSize.Width - lblStatus.Width - 20, 20);
        }
    }

    protected override void WndProc(ref Message m)
    {
        // Block Alt+F4 (WM_SYSCOMMAND + SC_CLOSE) during normal kiosk operation
        // Only allow close from our own code (logoff or service mode)
        const int WM_SYSCOMMAND = 0x0112;
        const int SC_CLOSE = 0xF060;
        if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt32() & 0xFFF0) == SC_CLOSE)
        {
            return; // eat the message
        }
        base.WndProc(ref m);
    }

    private void BtnLogOff_Click(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            "Are you sure you want to log off?\n\nThe application will restart automatically.",
            "GVP-Pro - Log Off",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (result == DialogResult.Yes)
        {
            PerformLogOff();
        }
    }

    private void BtnService_Click(object? sender, EventArgs e)
    {
        using var dialog = new ServicePasswordDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        // Validate password
        if (!ValidateServicePassword(dialog.EnteredPassword))
        {
            MessageBox.Show(
                "Invalid service password.",
                "GVP-Pro - Access Denied",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // Find ServiceMode.bat
        string batPath = FindServiceModeBat();
        if (batPath == null)
        {
            MessageBox.Show(
                "ServiceMode.bat not found.\n\nExpected locations:\n" +
                $"  {ServiceModeBatPath}\n  {ServiceModeBatFallback}\n\n" +
                "Copy ServiceMode.bat from the service USB to D:\\GVP-Pro\\App\\",
                "GVP-Pro - Script Missing",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // Confirm with engineer
        var confirm = MessageBox.Show(
            "This will enter Service Mode:\n\n" +
            "  • Enable EikonAdmin account\n" +
            "  • Disable kiosk lockdown\n" +
            "  • Disable UWF write filter\n" +
            "  • Reboot into maintenance desktop\n\n" +
            "The system will reboot. Continue?",
            "GVP-Pro - Enter Service Mode",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (confirm != DialogResult.Yes)
            return;

        try
        {
            // Launch ServiceMode.bat elevated
            var psi = new ProcessStartInfo
            {
                FileName = batPath,
                Verb = "runas",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(batPath)
            };
            Process.Start(psi);
            
            // ServiceMode.bat will handle the reboot — we just need to exit gracefully
            // Give the bat a moment to start, then exit the app
            System.Threading.Thread.Sleep(1000);
            Application.Exit();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User cancelled UAC prompt
            MessageBox.Show(
                "Service Mode requires administrator privileges.\nThe UAC prompt was cancelled.",
                "GVP-Pro - Cancelled",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to launch Service Mode:\n\n{ex.Message}",
                "GVP-Pro - Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static bool ValidateServicePassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        string hash = Convert.ToHexString(hashBytes);
        
        return hash.Equals(ServicePasswordHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindServiceModeBat()
    {
        if (File.Exists(ServiceModeBatPath)) return ServiceModeBatPath;
        if (File.Exists(ServiceModeBatFallback)) return ServiceModeBatFallback;
        return null;
    }

    private static void PerformLogOff()
    {
        // EWX_LOGOFF | EWX_FORCE — force close all apps and log off
        ExitWindowsEx(EWX_LOGOFF | EWX_FORCE, 0);
    }
}
