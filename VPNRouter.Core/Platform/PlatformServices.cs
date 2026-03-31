using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Services;

#if !PLATFORM_WINDOWS
using VPNRouter.Core.Platform.macOS;
#endif

namespace VPNRouter.Core.Platform;

/// <summary>
/// Factory for platform-specific service implementations.
/// Centralizes all #if PLATFORM_WINDOWS branching so callers don't need to.
///
/// Usage (in VPNRouter.Mac Program.cs):
///   var engine = PlatformServices.CreateVpnEngine(logger);
/// </summary>
public static class PlatformServices
{
    public static IProcessScanner CreateProcessScanner(ILogger? logger = null)
    {
#if PLATFORM_WINDOWS
        return new ProcessScanner(logger);
#else
        return new MacProcessScanner(logger);
#endif
    }

    public static Func<IFirewallManager> CreateFirewallFactory(ILogger? logger = null)
    {
#if PLATFORM_WINDOWS
        return () => new FirewallManager(logger);
#else
        return () => new NullFirewallManager(logger);
#endif
    }

    public static Func<IProcessMonitor> CreateMonitorFactory(ILogger? logger = null)
    {
#if PLATFORM_WINDOWS
        return () => new EtwProcessMonitor(logger);
#else
        return () => new MacProcessMonitor(logger: logger);
#endif
    }

    /// <summary>
    /// Convenience: create a fully wired VpnEngine with platform services.
    /// </summary>
    public static VpnEngine CreateVpnEngine(ILogger? logger = null)
    {
        return new VpnEngine(
            CreateProcessScanner(logger),
            CreateFirewallFactory(logger),
            CreateMonitorFactory(logger),
            logger);
    }
}
