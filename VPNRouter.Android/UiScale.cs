using System;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

/// <summary>
/// Reads the system font scale once and applies the shared type scale. The value is read when the UI is first built,
/// so a change of the system setting takes effect after the app is restarted.
/// </summary>
internal static class UiScale
{
    private static double? _systemFontScale;

    private static double SystemFontScale => _systemFontScale ??= Read();

    internal static double Fs(double sourceSize) => UiTypeScale.FontSize(sourceSize, SystemFontScale);

    internal static double Lh(double sourceLineHeight) => UiTypeScale.LineHeight(sourceLineHeight, SystemFontScale);

    private static double Read()
    {
        try
        {
            var scale = global::Android.App.Application.Context?.Resources?.Configuration?.FontScale ?? 1f;
            return scale;
        }
        catch
        {
            return 1.0;
        }
    }
}
