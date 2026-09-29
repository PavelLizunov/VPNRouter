#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services;

public static class ProviderKey
{
    public static string? ForIp(string? ipLiteral)
        => !string.IsNullOrWhiteSpace(ipLiteral) && IPAddress.TryParse(ipLiteral, out var ip)
            ? For(ip)
            : null;

    public static string For(IPAddress ip)
    {
        if (ip is null) throw new ArgumentNullException(nameof(ip));
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return $"net:{b[0]}.{b[1]}.{b[2]}.0/24";
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return $"net:{b[0]:x2}{b[1]:x2}:{b[2]:x2}{b[3]:x2}:{b[4]:x2}{b[5]:x2}::/48";
        }
        return $"net:{ip}";
    }

    public static async Task<string?> ResolveAsync(string? host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        if (IPAddress.TryParse(host, out var literal)) return For(literal);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var addrs = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            var pick = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                       ?? addrs.FirstOrDefault();
            return pick != null ? For(pick) : null;
        }
        catch { return null; }
    }
}
