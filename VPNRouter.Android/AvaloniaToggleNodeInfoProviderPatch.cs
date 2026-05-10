using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VPNRouter.Android;

// DEFCT-001 partial (2026-05-10) — runtime workaround for an
// Avalonia 11.3.12 bug in
// Avalonia.Android.Automation.ToggleNodeInfoProvider.PopulateNodeInfo.
//
// Upstream source (release/11.3.12, ToggleNodeInfoProvider.cs):
//
//     s_checkedProperty ??= nodeInfo.GetType().GetProperty(nameof(nodeInfo.Checked));
//     if (s_checkedProperty?.PropertyType == typeof(int))
//     {
//         s_checkedProperty.SetValue(this, ...);    // BUG: 'this' should be 'nodeInfo'
//     }
//     else if (s_checkedProperty?.PropertyType == typeof(bool))
//     {
//         s_checkedProperty.SetValue(this, ...);    // BUG: same
//     }
//
// The SetValue calls pass `this` (the ToggleNodeInfoProvider instance)
// as the target object, but the resolved PropertyInfo belongs to
// AccessibilityNodeInfoCompat. Reflection throws
// System.Reflection.TargetException ("Object does not match target type")
// every time the method runs. On Android 12 the unhandled exception
// aborts the process whenever the accessibility framework's
// AccessibilityNodePrefetcher walks any Avalonia peer that implements
// IToggleProvider — for example when `adb shell uiautomator dump` is
// used to drive automation, or (we suspect) when TalkBack is enabled.
//
// The 2026-05-10 first-pass mitigation in AndroidApp
// (HideSubtreeFromAccessibility on the kebab popup subtree) plugs the
// direct ExploreByTouchHelper.createNodeForChild path that fires on a
// tap inside the popup, but the prefetcher uses a separate descendant
// walk that does NOT honour Avalonia's AccessibilityView=Raw filter —
// so `uiautomator dump` still reaches the buggy peer and crashes.
//
// Strategy: replace the static `s_checkedProperty` field on
// ToggleNodeInfoProvider with a sentinel PropertyInfo whose
// PropertyType is neither int nor bool. Both buggy if-branches in
// PopulateNodeInfo are gated on the PropertyType being one of those
// two, so a sentinel of another type skips both branches entirely
// without throwing. The remaining work the method does
// (AddAction, Clickable=true, Checkable=true) still runs — only the
// `Checked` state assignment is dropped.
//
// Cost: TalkBack does not read out "checked" state for toggle peers.
// We have no toggle controls on the Simple page that depend on this,
// and the alternative is the entire app crashing under uiautomator
// dump or TalkBack. Acceptable as a workaround until an upstream
// Avalonia release ships a real fix; remove this patch then.
internal static class AvaloniaToggleNodeInfoProviderPatch
{
    private static bool _applied;

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;

        try
        {
            // AvaloniaView is public on Avalonia.Android — using it as
            // the assembly probe is stable across point releases and
            // doesn't depend on internal type visibility.
            var avaloniaAndroidAsm = typeof(global::Avalonia.Android.AvaloniaView).Assembly;
            var toggleType = avaloniaAndroidAsm.GetType(
                "Avalonia.Android.Automation.ToggleNodeInfoProvider");
            if (toggleType is null)
            {
                LogWarn("ToggleNodeInfoProvider type not found in Avalonia.Android — skipping patch");
                return;
            }

            // Force the static constructor first so its assignment to
            // s_checkedProperty (= typeof(AccessibilityNodeInfoCompat)
            // .GetProperty("Checked")) doesn't overwrite our sentinel
            // afterwards. RunClassConstructor is idempotent — Avalonia's
            // own runtime path will see it as already-initialised.
            RuntimeHelpers.RunClassConstructor(toggleType.TypeHandle);

            var field = toggleType.GetField(
                "s_checkedProperty",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (field is null)
            {
                LogWarn("s_checkedProperty field not found — Avalonia field name may have changed");
                return;
            }

            // Sentinel: any PropertyInfo whose PropertyType is neither
            // int nor bool. Type.Name is a public, framework-fundamental
            // property whose PropertyType is string — the runtime can
            // resolve it in every environment we ship into.
            var sentinel = typeof(Type).GetProperty(nameof(Type.Name));
            if (sentinel is null)
            {
                LogWarn("sentinel PropertyInfo (typeof(Type).GetProperty(\"Name\")) not found");
                return;
            }

            var previous = field.GetValue(null) as PropertyInfo;
            field.SetValue(null, sentinel);

            global::Android.Util.Log.Info("VpnRouter.A11y",
                "DEFCT-001: ToggleNodeInfoProvider.s_checkedProperty patched " +
                $"(was {previous?.DeclaringType?.Name}.{previous?.Name} " +
                $"of {previous?.PropertyType.Name}, now sentinel " +
                $"{sentinel.DeclaringType?.Name}.{sentinel.Name} " +
                $"of {sentinel.PropertyType.Name})");
        }
        catch (Exception ex)
        {
            // Best-effort: if Avalonia changes the field name or layout
            // in a future release, fall through to the
            // AccessibilityView=Raw mitigation in AndroidApp's kebab
            // popup construction — the app still works for end users,
            // we just lose the uiautomator-dump-friendly behaviour.
            LogWarn($"patch failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogWarn(string msg)
    {
        try { global::Android.Util.Log.Warn("VpnRouter.A11y", $"DEFCT-001: {msg}"); }
        catch { /* logging itself may fail this early — best effort */ }
    }
}
