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

    /// <summary>Icon size in dp next to text: the size times the same clamped system font scale as the text.</summary>
    internal static double Ic(double size) => Math.Round(size * UiTypeScale.ClampFactor(SystemFontScale), 1);

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
