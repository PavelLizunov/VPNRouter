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
