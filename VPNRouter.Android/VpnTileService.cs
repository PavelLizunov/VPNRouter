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
/// <para>
/// Android's request to start listening (<c>requestListeningState</c>, sent by the VPN service after every state
/// write) is not delivered while the tile already counts as listening, and an active tile gets no stop callback
/// from the shade. A tile that only refreshed in <c>OnStartListening</c> therefore showed the state from the moment
/// of the tap for good ("Connected" after the VPN was switched off from the tile). While the tile is listening it
/// now watches the state record itself and refreshes on every change.
/// </para>
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

    // SharedPreferences keeps its listeners weakly, so the tile has to hold the one it registers.
    private StateChangeListener? _stateListener;
    private global::Android.OS.Handler? _mainHandler;
    private Java.Lang.Runnable? _timeoutRefresh;
    private TileAppearance? _shown;

    public override void OnTileAdded()
    {
        base.OnTileAdded();
        Refresh(force: true);
    }

    public override void OnStartListening()
    {
        base.OnStartListening();
        StartWatchingState();
        Refresh(force: true);
    }

    public override void OnStopListening()
    {
        StopWatchingState();
        base.OnStopListening();
    }

    public override void OnDestroy()
    {
        StopWatchingState();
        base.OnDestroy();
    }

    public override void OnClick()
    {
        base.OnClick();
        StartWatchingState();

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
                OpenApp(TileIntentContract.ReasonPermission);
                break;
            case TileClickAction.OpenAppForSetup:
                OpenApp(TileIntentContract.ReasonSetup);
                break;
        }

        Refresh(force: true);
    }

    private void StartWatchingState()
    {
        if (_stateListener is not null)
            return;

        _stateListener = new StateChangeListener(() => Refresh(force: false));
        Prefs().RegisterOnSharedPreferenceChangeListener(_stateListener);
    }

    private void StopWatchingState()
    {
        if (_stateListener is not null)
        {
            Prefs().UnregisterOnSharedPreferenceChangeListener(_stateListener);
            _stateListener = null;
        }

        CancelTimeoutRefresh();
        _shown = null;
    }

    private void CancelTimeoutRefresh()
    {
        if (_timeoutRefresh is not null)
        {
            _mainHandler?.RemoveCallbacks(_timeoutRefresh);
            _timeoutRefresh = null;
        }
    }

    // A connect attempt that never ends turns into a timeout error without any write: refresh once more when it does,
    // so a "connecting" tile (which ignores taps) cannot stay frozen.
    private void ScheduleTimeoutRefresh(VpnStateSnapshot resolved)
    {
        CancelTimeoutRefresh();
        var delay = VpnStateResolver.NextChangeIn(resolved, DateTimeOffset.UtcNow);
        if (delay is null)
            return;

        _mainHandler ??= new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!);
        _timeoutRefresh = new Java.Lang.Runnable(() => Refresh(force: false));
        _mainHandler.PostDelayed(_timeoutRefresh, (long)delay.Value.TotalMilliseconds);
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
            OpenApp(TileIntentContract.ReasonConnect);
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
            .SetAction(TileIntentContract.ActionOpenFromTile)
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop)
            .PutExtra(TileIntentContract.ExtraReason, reason);

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

    private void Refresh(bool force)
    {
        var tile = QsTile;
        if (tile is null)
            return;

        var resolved = ResolveState();
        var appearance = TileAppearanceFactory.For(resolved, IsRussian());
        ScheduleTimeoutRefresh(resolved);

        // One state write changes several keys, so the watcher fires a few times: skip identical repeats. A forced
        // refresh (the system asked, or the tile was tapped) always pushes, in case SystemUI lost its copy.
        if (!force && appearance == _shown)
            return;
        _shown = appearance;

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

        // Android 16 hides the subtitle on a one-cell tile: the colour carries the state, and the spoken text the rest.
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            tile.ContentDescription = TileAppearanceFactory.Spoken(appearance);
            tile.StateDescription = appearance.Subtitle;
        }

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

    /// <summary>Calls back on the main thread when the VPN state record changes (the service writes it).</summary>
    private sealed class StateChangeListener : Java.Lang.Object, ISharedPreferencesOnSharedPreferenceChangeListener
    {
        private readonly Action _onChanged;

        public StateChangeListener(Action onChanged) => _onChanged = onChanged;

        public void OnSharedPreferenceChanged(ISharedPreferences? sharedPreferences, string? key)
        {
            if (key is null || key == KeyState || key == KeyStateReason || key == KeyStateAtMs)
                _onChanged();
        }
    }
}
