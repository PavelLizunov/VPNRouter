namespace VPNRouter.App.ViewModels;

// The home screen names the server the tunnel really uses: the engine knows the address it connected to, the lists know the names.
internal static class ActiveServerMatcher
{
    internal static string? FindName(string? address, IEnumerable<(string Name, string Host)> candidates)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        foreach (var (name, host) in candidates)
        {
            if (!string.IsNullOrEmpty(host) && string.Equals(host, address, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return null;
    }
}
