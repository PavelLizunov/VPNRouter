#if !PLATFORM_WINDOWS
using Serilog;
using VPNRouter.Core.Interfaces;

namespace VPNRouter.Core.Platform.macOS;

/// <summary>
/// No-op firewall manager for macOS.
///
/// block_on_vpn_fail is not implemented on macOS yet.
/// On macOS, pfctl anchor rules could provide the same protection,
/// but require root and more complex rule management.
///
/// For the initial macOS release, this stub logs that the feature
/// is unavailable. Traffic will not be blocked if sing-box crashes.
///
/// TODO: implement MacFirewallManager with pfctl anchor rules.
/// </summary>
public class NullFirewallManager : IFirewallManager
{
    private readonly ILogger _logger;
    private bool _disposed;

    public NullFirewallManager(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    public void CreateBlockRules(IEnumerable<string> processNames)
    {
        _logger.Debug("[NullFirewall] CreateBlockRules called — block_on_vpn_fail not supported on macOS");
    }

    public void EnableBlockRules()
    {
        _logger.Warning("[NullFirewall] EnableBlockRules: VPN crashed but block_on_vpn_fail is not available on macOS — traffic may leak");
    }

    public void DisableBlockRules()
    {
        _logger.Debug("[NullFirewall] DisableBlockRules called (no-op)");
    }

    public void DeleteAllRules()
    {
        _logger.Debug("[NullFirewall] DeleteAllRules called (no-op)");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
#endif
