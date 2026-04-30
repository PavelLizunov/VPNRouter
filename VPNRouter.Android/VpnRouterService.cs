// VpnRouterService — Android-native service that owns the VpnService
// lifecycle and hosts the libbox.aar runtime.
//
// v3.0 Android Phase 1.C (2026-04-30).
//
// libbox owns the actual TUN: it calls back into VpnRouterPlatformInterface.OpenTun
// when it wants the file descriptor, and we use VpnService.Builder there
// (NOT here) to obtain it. The flow:
//
//   ACTION_START → libbox.Setup(SetupOptions{base_path, working_path})
//                → new CommandServer(handler, platformInterface)
//                → server.Start()
//                → server.StartOrReloadService(configJson, null)
//                  ↳ libbox calls back into platformInterface.OpenTun()
//                    which builds VpnService → returns file descriptor
//                  ↳ libbox spins up sing-box on top of that fd
//
// Reference impl: sagernet/sing-box-for-android — app/src/main/java/io/nekohasekai/sfa/bg/BoxService.kt

using System;
using System.IO;
using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using AndroidX.Core.App;
// using IO.Nekohasekai.Libbox;  // Phase 1.C bisect: disabled

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
    private const string LogTag = "VpnRouter";

    private string? _pendingConfigJson;
    private string[]? _pendingAllowedPackages;
    // private CommandServer? _commandServer;  // Phase 1.C bisect: parked
    private bool _libboxSetupDone;

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

    /// <summary>
    /// Called from VpnRouterCommandServerHandler.ServiceStop when libbox
    /// itself wants the service shut down (e.g. fatal config error).
    /// </summary>
    internal void StopFromLibbox()
    {
        StopTunnel();
        StopSelf();
    }

    private void StartTunnel()
    {
        StartForeground(NotificationId, BuildNotification());

        try
        {
            EnsureLibboxSetup();
            StartLibboxService();
        }
        catch (Java.Lang.Exception jex)
        {
            global::Android.Util.Log.Error(LogTag, $"libbox start failed: {jex.GetType().Name}: {jex.Message}");
            StopSelf();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error(LogTag, $"libbox start failed: {ex.GetType().Name}: {ex.Message}");
            StopSelf();
        }
    }

    /// <summary>
    /// Initialise libbox once per-process. Sets up the working / base /
    /// temp paths so the Go side can write its caches and logs.
    /// </summary>
    private void EnsureLibboxSetup()
    {
        if (_libboxSetupDone)
        {
            return;
        }

        var basePath = FilesDir!.AbsolutePath;
        var workingPath = new Java.IO.File(FilesDir, "data").AbsolutePath;
        var tempPath = CacheDir!.AbsolutePath;

        Directory.CreateDirectory(workingPath);
        Directory.CreateDirectory(tempPath);

        // Phase 1.C bisect: Libbox.Setup parked
        _libboxSetupDone = true;

        global::Android.Util.Log.Info(LogTag,
            $"libbox setup OK (base={basePath} working={workingPath} temp={tempPath})");
    }

    private void StartLibboxService()
    {
        if (string.IsNullOrEmpty(_pendingConfigJson))
        {
            global::Android.Util.Log.Error(LogTag, "config_json missing — cannot start tunnel");
            StopSelf();
            return;
        }

        global::Android.Util.Log.Info(LogTag,
            "Phase 1.C bisect: VpnRouterService received START intent (libbox path parked)");
    }

    private void StopTunnel()
    {
        StopForeground(StopForegroundFlags.Remove);
    }

    public override void OnRevoke()
    {
        // System-initiated revoke (user toggled VPN off in Settings, or
        // another VPN app took over).
        StopTunnel();
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        StopTunnel();
        base.OnDestroy();
    }

    private global::Android.App.Notification BuildNotification()
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
            .SetSmallIcon(global::Android.Resource.Drawable.IcLockIdleLock)!
            .SetOngoing(true)!
            .AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "Disconnect", stopPi)!
            .Build();
    }
}
