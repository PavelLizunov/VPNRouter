using VPNRouter.Core.Localization;
using VPNRouter.Core.Platform.macOS;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// Ensures the managed root-owned helper is installed and current. The
    /// helper's probe is the only readiness authority; no user-writable marker
    /// or direct sudoers write exists here.
    /// </summary>
    private bool EnsureMacPrivilege()
    {
        StatusText = Strings.MacPrivilegeSetupRunning;
        var ready = MacPrivilegedSetup.EnsureInstalled(_logger);
        if (ready) return true;

        _logger.Error("[VM] macOS privileged helper is not ready; blocking Connect");
        StatusText = Strings.MacPrivilegeSetupRequired;
        return false;
    }
}
