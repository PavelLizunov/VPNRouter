// VpnRouterService — Android-native service that owns the VpnService
// lifecycle and hosts the libbox.aar runtime.
//
// v3.0 Android Phase 1 (2026-04-30) — C# port of the original Kotlin
// skeleton (VpnRouterService.kt is now a forward-declaration / docs-only
// file kept for cross-language reference). Mono.Android lets us derive
// from android.net.VpnService directly in C# and emits the Java callable
// wrapper at build time — no Kotlin compiler in the toolchain.
//
// Intent contract (paired with Core/Platform/Android/AndroidSingBoxRuntime.cs):
//   Start: ACTION_START + EXTRA_CONFIG_JSON (string) + EXTRA_ALLOWED_PACKAGES (string[])
//   Stop:  ACTION_STOP
//
// libbox.aar wiring is gated on the file actually being present at
// build time. When libbox.aar lands in VPNRouter.Android/Lib/ and the
// csproj's <AndroidLibrary> ItemGroup is uncommented, we'll add a
// matching using directive + Libbox.NewService(...) call inside
// StartTunnel(). For now StartTunnel obtains the TUN file descriptor
// from VpnService.Builder, then closes it (no-op tunnel) — this lets us
// exercise the OS-level consent + foreground flow on hardware without
// crashing on missing classes.
//
// Reference impl (Kotlin equivalent in upstream SFA):
//   sagernet/sing-box-for-android — app/src/main/java/io/nekohasekai/sfa/bg/BoxService.kt

using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using AndroidX.Core.App;

namespace VPNRouter.Android;

[Service(
    Name = "com.ninitux.vpnrouter.VpnRouterService",
    Permission = "android.permission.BIND_VPN_SERVICE",
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeSystemExempted,
    Exported = false)]
[IntentFilter(new[] { "android.net.VpnService" })]
public sealed class VpnRouterService : VpnService
{
    public const string ActionStart = "com.ninitux.vpnrouter.START";
    public const string ActionStop = "com.ninitux.vpnrouter.STOP";
    public const string ExtraConfigJson = "config_json";
    public const string ExtraAllowedPackages = "allowed_packages";

    private const int NotificationId = 100;
    private const string NotificationChannelId = "vpnrouter_tunnel";

    private string? _pendingConfigJson;
    private string[]? _pendingAllowedPackages;
    // private Libbox.IService? _libboxService;  // Phase 1.B — once libbox.aar is present.

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action;
        switch (action)
        {
            case ActionStart:
                _pendingConfigJson = intent?.GetStringExtra(ExtraConfigJson);
                _pendingAllowedPackages = intent?.GetStringArrayExtra(ExtraAllowedPackages);
                StartTunnel();
                break;

            case ActionStop:
                StopTunnel();
                StopSelf();
                break;
        }

        return StartCommandResult.NotSticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private void StartTunnel()
    {
        // Foreground notification — mandatory for VpnService on API 26+.
        StartForeground(NotificationId, BuildNotification());

        // Build the VpnService configuration. Address space mirrors
        // sing-box's default TUN inbound (172.19.0.1/30) so libbox's
        // TunService finds the right interface on FD handoff.
        var builder = new Builder(this)
            .SetSession("VPNRouter")
            !.SetMtu(1500)
            !.AddAddress("172.19.0.1", 30)
            !.AddAddress("fdfe:dcba:9876::1", 126)  // IPv6 leak protection.
            !.AddRoute("0.0.0.0", 0)
            !.AddRoute("::", 0)
            // Default DNS — libbox can override per-config.
            !.AddDnsServer("1.1.1.1")
            !.AddDnsServer("1.0.0.1");

        // Per-app routing: only route the listed packages through TUN.
        // Empty list = full-tunnel (everything routes).
        if (_pendingAllowedPackages is { Length: > 0 } pkgs)
        {
            foreach (var pkg in pkgs)
            {
                try
                {
                    builder.AddAllowedApplication(pkg);
                }
                catch (global::Android.Content.PM.PackageManager.NameNotFoundException)
                {
                    global::Android.Util.Log.Warn("VpnRouter", $"Package not found: {pkg}");
                }
            }
        }

        // CRITICAL: exclude OUR OWN package — otherwise the OS would
        // route our own SOCKS / DNS queries back through the TUN, which
        // would loop infinitely into libbox and collapse the tunnel.
        try
        {
            builder.AddDisallowedApplication(PackageName!);
        }
        catch (global::Android.Content.PM.PackageManager.NameNotFoundException ex)
        {
            global::Android.Util.Log.Error("VpnRouter", $"FATAL: cannot exclude self: {ex.Message}");
        }

        var pfd = builder.Establish();
        if (pfd is null)
        {
            global::Android.Util.Log.Error("VpnRouter",
                "VpnService.Builder.establish returned null — user denied or system rejected");
            StopSelf();
            return;
        }

        // Phase 1.A — TUN obtained, but libbox not yet wired in. Close
        // the FD immediately so we don't leave a half-configured tunnel
        // dangling. Phase 1.B replaces this with the libbox handoff:
        //
        //   var tunFd = pfd.Fd;
        //   _libboxService = Libbox.NewService(_pendingConfigJson, new VpnRouterPlatformInterface(this));
        //   _libboxService.Start(tunFd);
        //
        try { pfd.Close(); } catch { /* swallow */ }
    }

    private void StopTunnel()
    {
        // _libboxService?.Close(); _libboxService = null;
        StopForeground(StopForegroundFlags.Remove);
    }

    public override void OnRevoke()
    {
        // System-initiated revoke (user toggled VPN off in Settings, or
        // another VPN app took over). libbox should clean up gracefully.
        StopTunnel();
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        StopTunnel();
        base.OnDestroy();
    }

    private Notification BuildNotification()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(
                NotificationChannelId,
                "VPNRouter Tunnel",
                NotificationImportance.Low)
            {
                Description = "VPN tunnel running",
            };
            channel.SetShowBadge(false);

            var nm = (NotificationManager?)GetSystemService(NotificationService);
            nm?.CreateNotificationChannel(channel);
        }

        var stopIntent = new Intent(this, typeof(VpnRouterService))
            .SetAction(ActionStop);
        var stopPi = PendingIntent.GetService(
            this, 0, stopIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        return new NotificationCompat.Builder(this, NotificationChannelId)
            .SetContentTitle("VPNRouter")!
            .SetContentText("Tunnel active")!
            .SetSmallIcon(global::Android.Resource.Drawable.IcLockIdleLock)!  // TODO: brand icon.
            .SetOngoing(true)!
            .AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "Disconnect", stopPi)!
            .Build();
    }
}
