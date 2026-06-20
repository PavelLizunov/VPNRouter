using System;

namespace VPNRouter.Android;

/// <summary>
/// Phase 2C (Wave 9, 2026-05-18) — system-permission surfaces extracted
/// from <c>AndroidApp.axaml.cs</c>. The Android VpnService permission
/// itself is handled by <c>MainActivity.cs</c> (via
/// <c>VpnService.prepare()</c> + Activity.OnActivityResult), so this
/// partial only carries the AndroidApp-level secondary permissions:
///
/// <list type="bullet">
///   <item><strong>Always-on VPN</strong> — deep-link to system VPN
///   settings so the user can configure VPNRouter as the always-on
///   default. There's no programmatic way to set this from the app
///   (system-only since Android Q).</item>
///   <item><strong>Battery optimization exemption</strong> —
///   <c>PowerManager.IsIgnoringBatteryOptimizations</c> read +
///   <c>ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS</c> deep-link.
///   Required so the VpnService isn't killed by Doze.</item>
///   <item><strong>Auto-reconnect on network change</strong> — opt-in
///   checkbox that controls whether VpnRouterService re-broadcasts the
///   tunnel after a default-interface flip (Wi-Fi ↔ mobile).</item>
/// </list>
///
/// <para>Other permission flows (notification permission for foreground
/// service banner, location permission for Wi-Fi SSID lookup, etc.)
/// live in <c>VpnRouterService.java</c> or <c>MainActivity.cs</c> — they
/// run before AndroidApp's lifecycle even starts. This partial only
/// covers the runtime UX exposed in the Settings/Reliability section.</para>
/// </summary>
public partial class AndroidApp
{
    /// <summary>
    /// Deep-link to the Android Settings → VPN page so the user can find
    /// the gear next to "VPNRouter" and toggle Always-on. We use
    /// <c>Settings.ACTION_VPN_SETTINGS</c> (works on Android 4.0+).
    /// On API 24+ the same intent surfaces the new VPN list UI; older
    /// platforms get the per-app VPN settings dialog.
    /// </summary>
    private void OnReliabilityAlwaysOnClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var activity = MainActivity.Instance;
            if (activity is null) return;
            var intent = new global::Android.Content.Intent(
                global::Android.Provider.Settings.ActionVpnSettings);
            // Settings activities run in their own task — FLAG_ACTIVITY_NEW_TASK
            // is required when launching from a non-Activity Context, but
            // even from the Activity it's good practice for cross-task
            // settings deep-links.
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            activity.StartActivity(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"AND-NETRES: open VPN settings failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Battery-optimization deep-link. If we're already excluded
    /// (<c>PowerManager.IsIgnoringBatteryOptimizations</c> = true), open
    /// the system's "Battery optimization" list view so the user can
    /// inspect / revoke the exclusion. Otherwise fire
    /// <c>ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS</c> with our
    /// package URI to trigger the system's grant dialog. The user must
    /// confirm the prompt — we never auto-grant ourselves anything.
    /// </summary>
    private void OnReliabilityBatteryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var activity = MainActivity.Instance;
            if (activity is null) return;
            if (IsIgnoringBatteryOptimizations(activity))
            {
                // Open the system list view so the user can revoke the
                // exclusion if they want. ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS
                // is API 23+; we already gate the whole feature at
                // SDK 23 minimum (csproj SupportedOSPlatformVersion=23).
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

    /// <summary>
    /// v2.40.0 AND-NODOZE (2026-06-02) — fire the native battery-optimization
    /// exemption grant dialog (<c>ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS</c>
    /// with our <c>package:</c> URI). Shared by the explicit Settings →
    /// Reliability button (when currently optimized) and the proactive
    /// first-connect prompt (<see cref="MaybePromptBatteryOptimizationExemption"/>).
    /// The user must confirm the system dialog — we never auto-grant ourselves
    /// anything; this only surfaces the request.
    /// </summary>
    private static void RequestBatteryOptimizationExemption(global::Android.App.Activity activity)
    {
        var intent = new global::Android.Content.Intent(
            global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations);
        intent.SetData(global::Android.Net.Uri.Parse($"package:{activity.PackageName}"));
        intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
        activity.StartActivity(intent);
    }

    /// <summary>
    /// v2.40.0 AND-NODOZE — proactive exemption prompt, called from
    /// <c>UpdateConnectionState(connected: true)</c> at the first successful
    /// connect. Fires the native grant dialog exactly once (gated by the
    /// <c>battery_opt_prompt_shown</c> flag) when the app isn't already exempt.
    ///
    /// <para><strong>Why first-connect</strong>: it's the highest-intent
    /// moment — the user has a working tunnel and wants it to stay alive. The
    /// read-only device probe (KYOCERA A101BM / Android 12, 2026-06-02)
    /// confirmed the package is NOT in the <c>deviceidle</c> whitelist by
    /// default, so without this nudge the Doze bucket caches + kills the
    /// foreground service — exactly the "выключается раз в несколько
    /// минут-часов" symptom. The request was previously reachable only via
    /// Settings → Reliability (two taps into Advanced), so a normal user
    /// never granted it.</para>
    ///
    /// <para>Best-effort + heavily guarded: a thrown <c>StartActivity</c> (no
    /// Settings activity to resolve, OEM quirk, etc.) must never bubble into
    /// the connect UI path. The "shown" flag is set BEFORE launching so a
    /// re-entrant connect can't double-fire the dialog.</para>
    /// </summary>
    private void MaybePromptBatteryOptimizationExemption()
    {
        try
        {
            var activity = MainActivity.Instance;
            if (activity is null) return;
            if (IsIgnoringBatteryOptimizations(activity))
            {
                // Already granted (Settings, a prior install, or an OEM that
                // exempts VPNs) — record that there's nothing to ask so we
                // don't re-check PowerManager on every connect.
                AndroidStorage.SetBatteryOptPromptShown(true);
                return;
            }
            // B8 (2026-06-21): NOT exempt. Battery-opt is the single biggest
            // reliability lever — a one-shot prompt the user dismissed once left
            // the tunnel doze-killable forever. Re-prompt, but throttle to at most
            // once / 24h so it nudges without nagging.
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

    private void OnReliabilityAutoReconnectChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _reliabilityAutoReconnect is null) return;
        AndroidStorage.SetAutoReconnectOnNetworkChange(
            _reliabilityAutoReconnect.IsChecked == true);
        // VpnRouterService.fireUpdate reads this flag every default-interface
        // change; the running tunnel picks up new behaviour on the next
        // network event without a reconnect, so no dirty mark.
    }

    /// <summary>
    /// Re-read the live battery-optimization state and refresh the label
    /// + button. Called from <c>BuildSettingsAutostartSection</c> at build
    /// time (Phase C folded the old Reliability section into Autostart) AND
    /// from <c>ReseedNetworkTabState</c> each time the Settings tab is
    /// re-activated, so a user who just granted/revoked the exclusion in
    /// system settings sees the new state when they come back.
    /// </summary>
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
            // PowerManager.IsIgnoringBatteryOptimizations is API 23+. Our
            // minSdk is 23 (csproj SupportedOSPlatformVersion=23.0) so the
            // call always resolves; older Androids don't reach this code
            // path because the feature is hidden at the Reliability section
            // gate. Returns false on null PowerManager (fallback to "warn").
            if (pm is null) return false;
            return pm.IsIgnoringBatteryOptimizations(activity.PackageName ?? "");
        }
        catch
        {
            return false;
        }
    }
}
