namespace VPNRouter.Core.Services;

/// <summary>
/// Type scale of the Android UI. Source sizes in the code were written for a denser layout (9-16); the scale lifts them
/// to readable sizes and follows the system font-scale setting, so large-text users are not ignored.
/// </summary>
public static class UiTypeScale
{
    public const double Lift = 2.0;
    public const double MinFactor = 1.0;
    public const double MaxFactor = 1.4;

    public static double ClampFactor(double systemFontScale)
    {
        if (double.IsNaN(systemFontScale) || double.IsInfinity(systemFontScale)) return MinFactor;
        return Math.Clamp(systemFontScale, MinFactor, MaxFactor);
    }

    public static double FontSize(double sourceSize, double systemFontScale) =>
        Math.Round((sourceSize + Lift) * ClampFactor(systemFontScale), 1);

    public static double LineHeight(double sourceLineHeight, double systemFontScale) =>
        Math.Round((sourceLineHeight + Lift) * ClampFactor(systemFontScale), 1);
}
