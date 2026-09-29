using System;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private void OnReliabilityAlwaysOnClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var activity = MainActivity.Instance;
            if (activity is null) return;
            var intent = new global::Android.Content.Intent(
                global::Android.Provider.Settings.ActionVpnSettings);
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            activity.StartActivity(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"AND-NETRES: open VPN settings failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnReliabilityBatteryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var activity = MainActivity.Instance;
            if (activity is null) return;
            if (IsIgnoringBatteryOptimizations(activity))
            {
                var intent = new global::Android.Content.Intent(
                    global::Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings);
                intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
                activity.StartActivity(intent);
            }
            else
            {
                RequestBatteryOptimizationExemption(activity);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"AND-NETRES: battery opt deep-link failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RequestBatteryOptimizationExemption(global::Android.App.Activity activity)
    {
        var intent = new global::Android.Content.Intent(
            global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations);
        intent.SetData(global::Android.Net.Uri.Parse($"package:{activity.PackageName}"));
        intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
        activity.StartActivity(intent);
    }

    private void MaybePromptBatteryOptimizationExemption()
    {
        try
        {
            var activity = MainActivity.Instance;
            if (activity is null) return;
            if (IsIgnoringBatteryOptimizations(activity))
            {
                AndroidStorage.SetBatteryOptPromptShown(true);
                return;
            }
            var lastIso = AndroidStorage.GetBatteryOptLastPrompt();
            if (!string.IsNullOrEmpty(lastIso)
                && DateTimeOffset.TryParse(lastIso, out var last)
                && (DateTimeOffset.UtcNow - last) < TimeSpan.FromHours(24))
                return;
            AndroidStorage.SetBatteryOptPromptShown(true);
            AndroidStorage.SetBatteryOptLastPrompt(DateTimeOffset.UtcNow.ToString("o"));
            RequestBatteryOptimizationExemption(activity);
            global::Android.Util.Log.Info("VpnRouter",
                "AND-NODOZE: battery-opt exemption prompt fired (not exempt; 24h-throttled re-prompt)");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"AND-NODOZE: proactive battery-opt prompt failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void MaybePromptAlwaysOnLockdown()
    {
        try
        {
            if (AndroidStorage.GetAlwaysOnPromptShown()) return;
            var activity = MainActivity.Instance;
            if (activity is null) return;
            AndroidStorage.SetAlwaysOnPromptShown(true);
            new global::Android.App.AlertDialog.Builder(activity)
                .SetTitle(Localization.AlwaysOnNudgeTitle)
                ?.SetMessage(Localization.AlwaysOnNudgeBody)
                ?.SetPositiveButton(Localization.AlwaysOnNudgeOpen, (s, e) => OnReliabilityAlwaysOnClicked(this, null!))
                ?.SetNegativeButton(Localization.AlwaysOnNudgeLater, (s, e) => { })
                ?.Show();
            global::Android.Util.Log.Info("VpnRouter", "AND-KILLSWITCH: Always-on+Lockdown nudge shown (one-time)");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"AND-KILLSWITCH: always-on nudge failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnReliabilityAutoReconnectChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _reliabilityAutoReconnect is null) return;
        AndroidStorage.SetAutoReconnectOnNetworkChange(
            _reliabilityAutoReconnect.IsChecked == true);
    }

    private void OnExternalControlChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _externalControlToggle is null) return;
        AndroidStorage.SetExternalControlEnabled(_externalControlToggle.IsChecked == true);
    }

    private void UpdateBatteryOptimizationStatus()
    {
        var activity = MainActivity.Instance;
        if (activity is null) return;
        bool isExempt = IsIgnoringBatteryOptimizations(activity);

        if (_reliabilityBatteryStatusLabel is not null)
        {
            _reliabilityBatteryStatusLabel.Text = isExempt
                ? Localization.ReliabilityBatteryOptStatusExempt
                : Localization.ReliabilityBatteryOptStatusOptimized;
            _reliabilityBatteryStatusLabel.Foreground = GetBrush(
                isExempt ? "SuccessFgBrush" : "WarningFgBrush");
        }
        if (_reliabilityBatteryButton is not null)
        {
            _reliabilityBatteryButton.Content = isExempt
                ? Localization.ReliabilityBatteryOptButtonOpen
                : Localization.ReliabilityBatteryOptButtonGrant;
        }
    }

    private static bool IsIgnoringBatteryOptimizations(global::Android.App.Activity activity)
    {
        try
        {
            var pm = (global::Android.OS.PowerManager?)activity.GetSystemService(
                global::Android.Content.Context.PowerService);
            if (pm is null) return false;
            return pm.IsIgnoringBatteryOptimizations(activity.PackageName ?? "");
        }
        catch
        {
            return false;
        }
    }
}
