using System.Diagnostics;
using System.Security.Principal;

namespace VPNRouter.GUI;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Auto-elevate to admin (required for TUN + ETW + Firewall)
        if (!IsAdmin())
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
            }
            catch
            {
                MessageBox.Show(
                    "VPNRouter requires Administrator rights for TUN interface.\n" +
                    "Please run as Administrator.",
                    "VPNRouter",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }

    private static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
