using System;
using System.Runtime.Versioning;
using Avalonia.Threading;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private const string TileServiceClass = "com.ninitux.vpnrouter.VpnTileService";

    // Result codes of StatusBarManager.requestAddTileService (API 33).
    private const int TileAddNotAdded = 0;
    private const int TileAddAlreadyAdded = 1;
    private const int TileAddAdded = 2;

    /// <summary>
    /// Android 13 and newer can show the system "add tile" prompt; older versions (and any failure) get the manual
    /// instruction. The tile's final place is always the user's choice.
    /// </summary>
    private void OnMenuAddTileClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;

        var activity = MainActivity.Instance;
        if (activity is null) return;

        if (OperatingSystem.IsAndroidVersionAtLeast(33) && TryRequestAddTile(activity))
            return;

        ShowMenuFeedback(Localization.TileAddInstruction);
    }

    [SupportedOSPlatform("android33.0")]
    private bool TryRequestAddTile(MainActivity activity)
    {
        try
        {
            var manager = activity.GetSystemService(global::Android.Content.Context.StatusBarService)
                as global::Android.App.StatusBarManager;
            var executor = activity.MainExecutor;
            var iconId = activity.Resources?.GetIdentifier("ic_qs_vpn", "drawable", activity.PackageName) ?? 0;
            if (manager is null || executor is null || iconId == 0)
                return false;

            manager.RequestAddTileService(
                new global::Android.Content.ComponentName(activity.PackageName!, TileServiceClass),
                "VPNRouter",
                global::Android.Graphics.Drawables.Icon.CreateWithResource(activity, iconId)!,
                executor,
                new AddTileResultConsumer(code => Dispatcher.UIThread.Post(() => ShowAddTileResult(code))));
            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter", $"requestAddTileService failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void ShowAddTileResult(int code)
    {
        switch (code)
        {
            case TileAddAdded:
                ShowMenuFeedback(Localization.TileAddResultAdded);
                break;
            case TileAddAlreadyAdded:
                ShowMenuFeedback(Localization.TileAddResultAlready);
                break;
            case TileAddNotAdded:
                ShowMenuFeedback(Localization.TileAddResultDeclined);
                break;
            default:
                ShowMenuFeedback(Localization.TileAddInstruction);
                break;
        }
    }

    private sealed class AddTileResultConsumer : Java.Lang.Object, Java.Util.Functions.IConsumer
    {
        private readonly Action<int> _onResult;

        public AddTileResultConsumer(Action<int> onResult) => _onResult = onResult;

        public void Accept(Java.Lang.Object? result)
        {
            var code = (result as Java.Lang.Integer)?.IntValue() ?? -1;
            _onResult(code);
        }
    }
}
