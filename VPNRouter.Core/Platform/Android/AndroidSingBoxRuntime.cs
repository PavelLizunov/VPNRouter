#if PLATFORM_ANDROID
using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.OS;
using Java.Interop;
using Serilog;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Platform.Android;

public sealed class AndroidSingBoxRuntime
{
    private const string ActionStart = "com.ninitux.vpnrouter.START";
    private const string ActionStop = "com.ninitux.vpnrouter.STOP";
    private const string ExtraConfigJson = "config_json";
    private const string ExtraAllowedPackages = "allowed_packages";

    private static Type? _serviceType;

    public static void RegisterServiceType(Type serviceType) => _serviceType = serviceType;

    private readonly ILogger _logger;

    public AndroidSingBoxRuntime(ILogger logger)
    {
        _logger = logger;
    }

    public void Start(string configJson, IReadOnlyList<string> allowedPackages)
    {
        ArgumentNullException.ThrowIfNull(configJson);
        ArgumentNullException.ThrowIfNull(allowedPackages);

        var context = Application.Context
            ?? throw new InvalidOperationException(
                "Application.Context is null — AndroidSingBoxRuntime.Start called " +
                "before Avalonia attached to the Activity?");

        var pkgArray = new string[allowedPackages.Count];
        for (var i = 0; i < allowedPackages.Count; i++)
        {
            pkgArray[i] = allowedPackages[i];
        }

        var serviceType = _serviceType ?? throw new InvalidOperationException(
            "AndroidSingBoxRuntime.RegisterServiceType must be called before Start.");

        using var intent = new Intent(context, serviceType)
            .SetAction(ActionStart)
            .PutExtra(ExtraConfigJson, configJson)
            .PutExtra(ExtraAllowedPackages, pkgArray);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }

        _logger.Information(
            "[AndroidSingBoxRuntime] Start dispatched ({Pkgs} allowed packages, configJson {Bytes} bytes)",
            allowedPackages.Count,
            configJson.Length);
    }

    public void Stop()
    {
        var context = Application.Context;
        if (context is null)
        {
            return;
        }

        if (_serviceType is null)
        {
            return;
        }

        using var intent = new Intent(context, _serviceType)
            .SetAction(ActionStop);

        context.StartService(intent);

        _logger.Information("[AndroidSingBoxRuntime] Stop dispatched");
    }

    public static VpnStateSnapshot ResolveVpnState()
    {
        VpnStateSnapshot? stored = null;
        try
        {
            var prefs = Application.Context?.GetSharedPreferences("vpnrouter_settings", FileCreationMode.Private);
            if (prefs is not null)
            {
                stored = VpnStateCodec.TryParse(
                    prefs.GetString("vpn_state", null),
                    prefs.GetString("vpn_state_reason", null),
                    prefs.GetInt("vpn_state_pid", 0),
                    prefs.GetLong("vpn_state_at_ms", 0));
            }
        }
        catch
        {
            stored = null;
        }
        return VpnStateResolver.Resolve(stored, global::Android.OS.Process.MyPid(), DateTimeOffset.UtcNow);
    }

    public bool IsRunning() => HasLiveTunnel();

    public bool IsHealthy() => HasRecentTunnelStatus();

    public static bool HasLiveTunnel() => QueryService("hasLiveTunnel");
    public static bool HasRecentTunnelStatus() => QueryService("hasRecentTunnelStatus");

    private static JniType? _serviceClass;

    // JNI owns this global class reference for the process. Both the command monitor and
    // status queries need it across Stop, even when Android destroys the service instance.
    internal static JniObjectReference ServiceClassReference =>
        JniType.GetCachedJniType(ref _serviceClass, "com/ninitux/vpnrouter/VpnRouterService").PeerReference;

    private static bool QueryService(string methodName)
    {
        try
        {
            var service = ServiceClassReference;
            var method = JniEnvironment.StaticMethods.GetStaticMethodID(service, methodName, "()Z");
            return JniEnvironment.StaticMethods.CallStaticBooleanMethod(service, method);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
#endif
