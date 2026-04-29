#if PLATFORM_ANDROID
using Serilog;

namespace VPNRouter.Core.Platform.Android;

/// <summary>
/// v3.0 Android Phase 1 SKELETON (2026-04-29).
///
/// <para>Android-side equivalent of <see cref="VPNRouter.Core.Services.SingBoxManager"/>.
/// Where SingBoxManager spawns a real <c>sing-box.exe</c> process on
/// desktop, AndroidSingBoxRuntime sends Intents to the Android-native
/// <c>VpnRouterService</c> (Kotlin, in <c>VPNRouter.Android/VpnRouterService.kt</c>).
/// That service hosts the libbox.aar runtime in-process.</para>
///
/// <para>Intent contract (see VpnRouterService.Companion):</para>
/// <list type="bullet">
/// <item>Start: <c>ACTION_START</c> + <c>EXTRA_CONFIG_JSON</c> (string)
/// + <c>EXTRA_ALLOWED_PACKAGES</c> (string[]).</item>
/// <item>Stop: <c>ACTION_STOP</c>.</item>
/// </list>
///
/// <para>Phase 1 NOT YET IMPLEMENTED on this side either — the Intent
/// dispatch needs to hop through Mono.Android's
/// <c>Android.App.Application.Context</c> reference, which the Avalonia
/// Android target makes available via <c>Avalonia.Android.AvaloniaActivity</c>.
/// Fully wiring this up requires Phase 0's MainActivity to expose the
/// context, plus Phase 1's libbox.aar to be checked in. See the .kt file
/// for the build dependencies.</para>
/// </summary>
public sealed class AndroidSingBoxRuntime
{
    private readonly ILogger _logger;

    public AndroidSingBoxRuntime(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Send an ACTION_START intent to VpnRouterService.
    /// configJson is the sing-box JSON config (built by ConfigGenerator
    /// with PLATFORM_ANDROID branch — no process_name rules, since
    /// per-app routing happens at VpnService.Builder layer).
    /// allowedPackages is the list of Profile.AndroidPackages — empty
    /// means full-tunnel.
    /// </summary>
    public void Start(string configJson, IReadOnlyList<string> allowedPackages)
    {
        // TODO Phase 1 step 4 — wire via Mono.Android Context.
        // Reference impl pattern (will not compile until Mono.Android
        // bindings + libbox AAR are present):
        //
        //   var context = global::Android.App.Application.Context;
        //   var intent = new Intent(context, typeof(VpnRouterService))
        //       .SetAction(VpnRouterService.ActionStart)
        //       .PutExtra(VpnRouterService.ExtraConfigJson, configJson)
        //       .PutExtra(VpnRouterService.ExtraAllowedPackages,
        //                 allowedPackages.ToArray());
        //   context.StartForegroundService(intent);
        //
        // Note: VpnService requires user consent on first run via
        // VpnService.Prepare() returning an Intent we then startActivityForResult.
        // That dance happens in MainActivity.kt before the Start call here.

        _logger.Warning("[AndroidSingBoxRuntime] Phase 1 stub — Start({Pkgs} pkgs) called but Intent dispatch not wired",
            allowedPackages.Count);
    }

    /// <summary>Send ACTION_STOP intent.</summary>
    public void Stop()
    {
        // TODO Phase 1 step 4 — wire via Mono.Android Context.
        //   var context = global::Android.App.Application.Context;
        //   var intent = new Intent(context, typeof(VpnRouterService))
        //       .SetAction(VpnRouterService.ActionStop);
        //   context.StartService(intent);

        _logger.Warning("[AndroidSingBoxRuntime] Phase 1 stub — Stop() called but Intent dispatch not wired");
    }

    /// <summary>True if the tunnel is currently running. Phase 1 plan:
    /// query Clash API at 127.0.0.1:9090/configs (libbox exposes the
    /// same endpoint as desktop sing-box). Until libbox is wired, returns
    /// false unconditionally.</summary>
    public bool IsRunning()
    {
        // TODO: HTTP probe to http://127.0.0.1:9090/configs (5s timeout).
        return false;
    }

    /// <summary>Health probe via Clash API. Phase 1 plan: same as desktop
    /// SingBoxManager.IsHealthy.</summary>
    public bool IsHealthy() => IsRunning();
}
#endif
