namespace VPNRouter.Core.Models;

public static class ConnectionIntent
{
    public const string General = "general";
    public const string Gaming = "gaming";
    public const string Privacy = "privacy";
    public const string Compatibility = "compatibility";

    public static string Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v is Gaming or Privacy or Compatibility ? v : General;
    }
}
