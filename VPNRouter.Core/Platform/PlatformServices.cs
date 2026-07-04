using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;

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
        // macOS gets a pf kill-switch (r6); Linux gets an nft kill-switch — both
        // global egress block, full-tunnel only, default-OFF (engaged only when a
        // profile sets block_on_vpn_fail). Anything else → NullFirewallManager.
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

    /// <summary>
    /// Fix #1 (r3): the Unix DNS-leak hardening service. MacDnsHardening on macOS
    /// (pins the system resolver to the TUN gateway); LinuxDnsHardening on Linux
    /// (systemd-resolved: pins the TUN link's DNS + default routing-domain via
    /// resolvectl, fail-open); a no-op everywhere else (Windows uses
    /// IWindowsDnsHardening). Both Unix impls are best-effort and never throw.
    /// </summary>
    public static IUnixDnsHardening CreateUnixDnsHardening(ILogger? logger = null)
    {
        if (OperatingSystem.IsMacOS())
            return new macOS.MacDnsHardening();
        if (OperatingSystem.IsLinux())
            return new Linux.LinuxDnsHardening();
        return NullUnixDnsHardening.Default;
    }

    /// <summary>W1.2: the true-split kernel-driver manager on Windows (null on macOS/Linux). The
    /// manager is <c>[SupportedOSPlatform("windows")]</c>; the OS guard keeps CA1416 quiet while the
    /// returned <see cref="ISplitTunnelDriver"/> interface stays cross-platform for VpnEngine to hold.</summary>
    public static ISplitTunnelDriver? CreateSplitTunnelDriver(ILogger? logger = null)
        => OperatingSystem.IsWindows() ? new SplitTunnelDriverManager(logger: logger) : null;

    /// <summary>
    /// Convenience: create a fully wired VpnEngine with platform services.
    ///
    /// <para>3G-4 (v3.0 refactor): this factory is the SOLE blessed way to
    /// construct a <see cref="VpnEngine"/>. Direct construction is marked
    /// <c>[Obsolete(error: false)]</c> to surface warnings on the two
    /// legacy call sites (CLI <c>StartCommand</c>, <c>VPNRouterService</c>)
    /// that predate this factory. Migrate those to call this method.
    /// The <c>#pragma warning disable</c> below is the one approved
    /// suppression site for the deprecation — kept here so the factory
    /// itself doesn't trip the same warning it's enforcing.</para>
    /// </summary>
    public static VpnEngine CreateVpnEngine(ILogger? logger = null)
    {
#pragma warning disable CS0618 // VpnEngine ctor is [Obsolete] for callers outside this factory.
        return new VpnEngine(
            CreateProcessScanner(logger),
            CreateFirewallFactory(logger),
            CreateMonitorFactory(logger),
            logger,
            unixDnsHardening: CreateUnixDnsHardening(logger),
            splitDriver: CreateSplitTunnelDriver(logger));
#pragma warning restore CS0618
    }

    /// <summary>
    /// Phase 3 — 3F (v3.0 refactor): build a platform-appropriate
    /// <see cref="IUpdateSource"/>. Branches:
    /// <list type="bullet">
    ///   <item>Android (sideload variant, today's default) → <see cref="SideloadSource"/>.</item>
    ///   <item>Android (Play Store variant, Phase 4) → <see cref="PlayStoreSource"/>.</item>
    ///   <item>Win/Mac/Linux → <see cref="GitHubReleaseSource"/>.</item>
    /// </list>
    ///
    /// <para>The Android caller (<c>VPNRouter.Android.AndroidApp.AutoUpdate</c>)
    /// supplies an <see cref="IAndroidInstaller"/> adapter wrapping
    /// <c>AndroidUpdater</c>; desktop callers supply the legacy
    /// <see cref="UpdateChecker"/> as the
    /// <see cref="IDesktopInstaller"/>. Phase 4 will fold those
    /// adapters back into the source impls and retire the legacy
    /// installer interface.</para>
    /// </summary>
    /// <param name="settings">Update channel + GitHub repo settings.</param>
    /// <param name="currentVersion">Running app version (e.g.
    /// <c>AppVersion.Version</c>).</param>
    /// <param name="http">Shared HTTP client (typically
    /// <c>PolicyHttpClient.Shared</c>).</param>
    /// <param name="desktopInstaller">Desktop-side installer adapter.
    /// Required when not on Android.</param>
    /// <param name="androidInstaller">Android-side installer adapter.
    /// Required when on Android sideload.</param>
    /// <param name="preferPlayStore">Force the Play Store stub even on
    /// Android (Phase 4 build variant). Default <c>false</c> selects
    /// sideload.</param>
    public static IUpdateSource CreateUpdateSource(
        UpdateSettings settings,
        string currentVersion,
        IHttpClient http,
        IDesktopInstaller? desktopInstaller = null,
        IAndroidInstaller? androidInstaller = null,
        bool preferPlayStore = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(http);

        if (OperatingSystem.IsAndroid())
        {
            if (preferPlayStore)
                return new PlayStoreSource();
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
