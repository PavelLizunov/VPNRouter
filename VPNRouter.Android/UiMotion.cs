namespace VPNRouter.Android;

/// <summary>
/// Follows the Android "Remove animations" accessibility setting (animator duration scale 0). Read at the start of
/// every animation, so a change applies without a restart.
/// </summary>
internal static class UiMotion
{
    internal static bool Enabled
    {
        get
        {
            try
            {
                var resolver = global::Android.App.Application.Context?.ContentResolver;
                if (resolver is null) return true;
                return global::Android.Provider.Settings.Global.GetFloat(resolver, "animator_duration_scale", 1f) > 0f;
            }
            catch
            {
                return true;
            }
        }
    }
}
