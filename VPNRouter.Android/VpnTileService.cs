using System.Runtime.Versioning;
using Android.App;
using Android.Content;
using Android.Graphics.Drawables;
using Android.Net;
using Android.Service.QuickSettings;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

/// <summary>
/// The quick-settings tile. It never keeps its own idea of the connection: it reads the record that
/// <c>VpnRouterService</c> writes (see <see cref="VpnStateCodec"/>), resolves it through
/// <see cref="VpnStateResolver"/>, and asks <see cref="TileClickPlanner"/> what a tap should do.
/// </summary>
[SupportedOSPlatform("android24.0")]
[Service(
    Name = "com.ninitux.vpnrouter.VpnTileService",
    Label = "VPNRouter",
    Icon = "@drawable/ic_qs_vpn",
    Permission = "android.permission.BIND_QUICK_SETTINGS_TILE",
    Exported = true)]
[IntentFilter(new[] { "android.service.quicksettings.action.QS_TILE" })]
[MetaData("android.service.quicksettings.ACTIVE_TILE", Value = "true")]
public sealed class VpnTileService : TileService
{
    public const string ActionOpenFromTile = "com.ninitux.vpnrouter.OPEN_FROM_TILE";
    public const string ExtraTileReason = "tile_reason";
    public const string ReasonPermission = "permission";
    public const string ReasonSetup = "setup";
    public const string ReasonConnect = "connect";

    private const string PrefsName = "vpnrouter_settings";
    private const string ServiceClass = "com.ninitux.vpnrouter.VpnRouterService";
    private const string ActionStart = "com.ninitux.vpnrouter.TILE_START";
    private const string ActionStop = "com.ninitux.vpnrouter.STOP";
    private const string KeyState = "vpn_state";
    private const string KeyStateReason = "vpn_state_reason";
    private const string KeyStatePid = "vpn_state_pid";
    private const string KeyStateAtMs = "vpn_state_at_ms";
    private const string KeyLastGoodConfig = "last_good_config_json";
    private const string KeyLanguage = "language";
    private const string LogTag = "VpnRouter";

    public override void OnTileAdded()
    {
        base.OnTileAdded();
        Refresh();
    }

    public override void OnStartListening()
    {
        base.OnStartListening();
        Refresh();
    }

    public override void OnClick()
    {
        base.OnClick();

        var resolved = ResolveState();
        var permissionGranted = VpnService.Prepare(this) is null;
        var hasSavedConfig = !string.IsNullOrEmpty(Prefs().GetString(KeyLastGoodConfig, null));

        switch (TileClickPlanner.Plan(resolved.State, permissionGranted, hasSavedConfig))
        {
            case TileClickAction.StartVpn:
                StartVpn();
                break;
            case TileClickAction.StopVpn:
                StopVpn();
                break;
            case TileClickAction.OpenAppForPermission:
                OpenApp(ReasonPermission);
                break;
            case TileClickAction.OpenAppForSetup:
                OpenApp(ReasonSetup);
                break;
        }

        Refresh();
    }

    private void StartVpn()
    {
        // Mark "connecting" before the service is even reached, so a second tap in the same moment is ignored.
        WriteState(VpnConnectionState.Connecting, null);
        try
        {
            var intent = new Intent().SetClassName(PackageName!, ServiceClass).SetAction(ActionStart);
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                StartForegroundService(intent);
            else
                StartService(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(LogTag, $"Tile could not start the VPN service: {ex.GetType().Name}");
            WriteState(VpnConnectionState.Error, TileAppearanceFactory.ReasonForegroundStartBlocked);
            OpenApp(ReasonConnect);
        }
    }

    private void StopVpn()
    {
        try
        {
            StartService(new Intent().SetClassName(PackageName!, ServiceClass).SetAction(ActionStop));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(LogTag, $"Tile could not stop the VPN service: {ex.GetType().Name}");
        }
    }

    private void OpenApp(string reason)
    {
        var intent = new Intent(this, typeof(MainActivity))
            .SetAction(ActionOpenFromTile)
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop)
            .PutExtra(ExtraTileReason, reason);

        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            var pending = PendingIntent.GetActivity(
                this, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            StartActivityAndCollapse(pending!);
        }
        else
        {
#pragma warning disable CA1422 // the Intent overload is deprecated from API 34 but is the only one below it
            StartActivityAndCollapse(intent);
#pragma warning restore CA1422
        }
    }

    private void Refresh()
    {
        var tile = QsTile;
        if (tile is null)
            return;

        var appearance = TileAppearanceFactory.For(ResolveState(), IsRussian());
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            tile.Label = "VPNRouter";
            tile.Subtitle = appearance.Subtitle;
        }
        else
        {
            tile.Label = $"VPNRouter · {appearance.Subtitle}";
        }

        tile.State = appearance.Visual switch
        {
            TileVisual.On => TileState.Active,
            TileVisual.Busy => TileState.Unavailable,
            _ => TileState.Inactive,
        };

        var iconId = Resources!.GetIdentifier("ic_qs_vpn", "drawable", PackageName);
        if (iconId != 0)
            tile.Icon = Icon.CreateWithResource(this, iconId);

        tile.UpdateTile();
    }

    private VpnStateSnapshot ResolveState()
    {
        var prefs = Prefs();
        var stored = VpnStateCodec.TryParse(
            prefs.GetString(KeyState, null),
            prefs.GetString(KeyStateReason, null),
            prefs.GetInt(KeyStatePid, 0),
            prefs.GetLong(KeyStateAtMs, 0));
        return VpnStateResolver.Resolve(stored, global::Android.OS.Process.MyPid(), DateTimeOffset.UtcNow);
    }

    private void WriteState(VpnConnectionState state, string? reason)
    {
        Prefs().Edit()!
            .PutString(KeyState, VpnStateCodec.Encode(state))!
            .PutString(KeyStateReason, reason)!
            .PutInt(KeyStatePid, global::Android.OS.Process.MyPid())!
            .PutLong(KeyStateAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())!
            .Apply();
    }

    private bool IsRussian()
    {
        var language = Prefs().GetString(KeyLanguage, null);
        return language is not null
            ? language.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
            : string.Equals(Java.Util.Locale.Default?.Language, "ru", StringComparison.OrdinalIgnoreCase);
    }

    private ISharedPreferences Prefs() => GetSharedPreferences(PrefsName, FileCreationMode.Private)!;
}
