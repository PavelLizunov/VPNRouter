using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;

#if !PLATFORM_WINDOWS
using VPNRouter.Core.Platform.macOS;
#endif

namespace VPNRouter.Core.Platform;

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
        if (OperatingSystem.IsLinux())
            return () => new Linux.LinuxFirewallManager(logger);
        return OperatingSystem.IsMacOS()
            ? () => new MacFirewallManager(logger)
            : () => new NullFirewallManager(logger);
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

    public static IUnixDnsHardening CreateUnixDnsHardening(ILogger? logger = null)
    {
        if (OperatingSystem.IsMacOS())
            return new macOS.MacDnsHardening();
        if (OperatingSystem.IsLinux())
            return new Linux.LinuxDnsHardening();
        return NullUnixDnsHardening.Default;
    }

    public static ISplitTunnelDriver? CreateSplitTunnelDriver(ILogger? logger = null)
        => OperatingSystem.IsWindows() ? new SplitTunnelDriverManager(logger: logger) : null;

    public static VpnEngine CreateVpnEngine(ILogger? logger = null)
    {
#pragma warning disable CS0618
        return new VpnEngine(
            CreateProcessScanner(logger),
            CreateFirewallFactory(logger),
            CreateMonitorFactory(logger),
            logger,
            unixDnsHardening: CreateUnixDnsHardening(logger),
            splitDriver: CreateSplitTunnelDriver(logger));
#pragma warning restore CS0618
    }

    public static IUpdateSource CreateUpdateSource(
        UpdateSettings settings,
        string currentVersion,
        IHttpClient http,
        IDesktopInstaller? desktopInstaller = null,
        IAndroidInstaller? androidInstaller = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(http);

        if (OperatingSystem.IsAndroid())
        {
            if (androidInstaller is null)
                throw new InvalidOperationException(
                    "androidInstaller is required for Android sideload — " +
                    "wire VPNRouter.Android.AndroidInstallerAdapter at the call site.");
            return new SideloadSource(settings, currentVersion, http, androidInstaller);
        }

        if (desktopInstaller is null)
            throw new InvalidOperationException(
                "desktopInstaller is required on desktop platforms — " +
                "pass an UpdateChecker instance (which implements IDesktopInstaller).");
        return new GitHubReleaseSource(settings, currentVersion, http, desktopInstaller);
    }
}
