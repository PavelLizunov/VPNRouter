using System.Security.Principal;

namespace VPNRouter.CLI.Commands;

public static class AdminHelper
{
    /// <summary>
    /// True when the process has the elevation needed for TUN / firewall / ETW.
    ///
    /// <para>Windows: member of the Administrators role (unchanged behaviour).
    /// Linux/macOS: root (euid 0) via <see cref="System.Environment.IsPrivilegedProcess"/>.</para>
    ///
    /// <para>Pre-fix this called <see cref="WindowsIdentity"/> unconditionally, which
    /// throws <c>PlatformNotSupportedException</c> on Linux — so <c>vpnrouter start</c>
    /// crashed at the admin gate on Linux/macOS ("Windows Principal functionality is
    /// not supported on this platform"). Found via a live test on the Debian VM.</para>
    /// </summary>
    public static bool IsAdmin()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        // Linux/macOS: elevation == root (euid 0). Environment.IsPrivilegedProcess
        // (net7+) is the cross-platform privilege check.
        return Environment.IsPrivilegedProcess;
    }
}
