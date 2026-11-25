namespace VPNRouter.Core.Services;

// Business-only preference. It never changes the standard urltest setting.
internal static class BusinessServerSelectionMode
{
    internal const string Automatic = "automatic";
    internal const string Manual = "manual";

    internal static bool IsConfigured(string? value) =>
        string.Equals(value, Automatic, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, Manual, StringComparison.OrdinalIgnoreCase);

    internal static bool IsAutomatic(string? value) =>
        string.Equals(value, Automatic, StringComparison.OrdinalIgnoreCase);

    internal static string Normalize(string? value) =>
        string.Equals(value, Manual, StringComparison.OrdinalIgnoreCase) ? Manual : Automatic;
}
