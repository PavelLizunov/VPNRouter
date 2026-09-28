using System;

namespace VPNRouter.Core.Models;

public static class PerAppFilterMode
{
    public const string Off = "off";
    public const string Include = "include";
    public const string Exclude = "exclude";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Off;
        if (string.Equals(value, Include, StringComparison.OrdinalIgnoreCase)) return Include;
        if (string.Equals(value, Exclude, StringComparison.OrdinalIgnoreCase)) return Exclude;
        if (string.Equals(value, Off, StringComparison.OrdinalIgnoreCase)) return Off;
        return Off;
    }

    public static string ResolveLastMode(string? value)
    {
        if (string.Equals(value, Exclude, StringComparison.OrdinalIgnoreCase)) return Exclude;
        return Include;
    }

    public static bool IsSplit(string? value)
    {
        var n = Normalize(value);
        return n == Include || n == Exclude;
    }

    public static string RoutingModeFor(string? perAppMode) =>
        IsSplit(perAppMode) ? "split" : "full";

    public static string? PerAppModeForRoutingChange(
        string? routingMode, string? currentPerAppMode, string? lastMode)
    {
        var current = Normalize(currentPerAppMode);
        var wantFull = string.Equals(routingMode, "full", StringComparison.OrdinalIgnoreCase);
        if (wantFull)
            return current == Off ? null : Off;
        if (current != Off) return null;
        return ResolveLastMode(lastMode);
    }
}
