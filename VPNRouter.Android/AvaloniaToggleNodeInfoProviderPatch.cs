using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VPNRouter.Android;

internal static class AvaloniaToggleNodeInfoProviderPatch
{
    private static bool _applied;

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;

        try
        {
            var avaloniaAndroidAsm = typeof(global::Avalonia.Android.AvaloniaView).Assembly;
            var toggleType = avaloniaAndroidAsm.GetType(
                "Avalonia.Android.Automation.ToggleNodeInfoProvider");
            if (toggleType is null)
            {
                LogWarn("ToggleNodeInfoProvider type not found in Avalonia.Android — skipping patch");
                return;
            }

            RuntimeHelpers.RunClassConstructor(toggleType.TypeHandle);

            var field = toggleType.GetField(
                "s_checkedProperty",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (field is null)
            {
                LogWarn("s_checkedProperty field not found — Avalonia field name may have changed");
                return;
            }

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
            LogWarn($"patch failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogWarn(string msg)
    {
        try { global::Android.Util.Log.Warn("VpnRouter.A11y", $"DEFCT-001: {msg}"); }
        catch {  }
    }
}
