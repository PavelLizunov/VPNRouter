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
    private string? EnsureMacPrivilege()
    {
        StatusText = Strings.MacPrivilegeSetupRunning;
        var result = MacPrivilegedSetup.EnsureInstalled(_logger);
        if (result == MacPrivilegeSetupResult.Ready)
            return null;

        _logger.Error("[VM] macOS privileged helper setup blocked Connect ({Result})", result);
        return result == MacPrivilegeSetupResult.LegacySessionActive
            ? Strings.MacPrivilegeLegacySessionActive
            : Strings.MacPrivilegeSetupRequired;
    }
}
