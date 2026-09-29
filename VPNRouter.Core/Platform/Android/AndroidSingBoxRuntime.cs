#if PLATFORM_ANDROID
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Serilog;

namespace VPNRouter.Core.Platform.Android;

public sealed class AndroidSingBoxRuntime
{
    private const string ActionStart = "com.ninitux.vpnrouter.START";
    private const string ActionStop = "com.ninitux.vpnrouter.STOP";
    private const string ExtraConfigJson = "config_json";
    private const string ExtraAllowedPackages = "allowed_packages";

    private const string ClashApiUrl = "http://127.0.0.1:9090/configs";

    public static string? ClashApiSecret { get; set; }

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

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

    public async Task<bool> IsRunningAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ClashApiUrl);
            if (!string.IsNullOrEmpty(ClashApiSecret))
                req.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ClashApiSecret);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public bool IsRunning()
    {
        try
        {
            return IsRunningAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    public bool IsHealthy() => IsRunning();
}
#endif
