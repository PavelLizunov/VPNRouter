#if !PLATFORM_WINDOWS
using Serilog;
using VPNRouter.Core.Interfaces;

namespace VPNRouter.Core.Platform.macOS;

public class NullFirewallManager : IFirewallManager
{
    private readonly ILogger _logger;
    private bool _disposed;

    public NullFirewallManager(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true)
    {
        _logger.Debug("[NullFirewall] CreateBlockRules called — block_on_vpn_fail not supported on this platform");
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
