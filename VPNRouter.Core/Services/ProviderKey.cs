#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services;

/// <summary>
/// urltest R3 — OFFLINE provider/subnet grouping key for
/// <see cref="ServerHealthClassifier.AnalyzeProviderRisk"/>.
///
/// <para>The audit's detection needs a GROUPING KEY ("several servers in the same
/// ASN/provider/prefix fail at the protocol phase"), not a registry-accurate ASN.
/// A real GeoLite2-ASN lookup would need either a bundled ~10 MB mmdb (+ a new
/// reader dependency + licensing) or an online API — and sending the user's
/// PRIVATE subscription-server IPs to a third party is exactly what the R3 gate
/// forbids (FreeConfigGeoIp's ip-api.com path is acceptable for PUBLIC free
/// configs only). The IP prefix (/24 for v4 — typical hoster allocation
/// granularity, /48 for v6) groups same-subnet servers with ZERO external data,
/// zero network, zero licensing. Keys are opaque strings ("net:1.2.3.0/24") —
/// if a real-ASN source ever lands, only this producer changes.</para>
/// </summary>
public static class ProviderKey
{
    /// <summary>Grouping key for a literal IP string, or null when it isn't one.</summary>
    public static string? ForIp(string? ipLiteral)
        => !string.IsNullOrWhiteSpace(ipLiteral) && IPAddress.TryParse(ipLiteral, out var ip)
            ? For(ip)
            : null;

    /// <summary>Grouping key for an address: v4 → /24, v6 → /48.</summary>
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
            // First 3 hextets (48 bits), canonical lowercase, zero-padded.
            return $"net:{b[0]:x2}{b[1]:x2}:{b[2]:x2}{b[3]:x2}:{b[4]:x2}{b[5]:x2}::/48";
        }
        return $"net:{ip}";   // exotic family — still a stable opaque key
    }

    /// <summary>
    /// Resolve a host (literal IP fast-path, else one bounded DNS lookup) to its
    /// grouping key. Never throws; null when unresolvable. The DNS query goes to
    /// the OS resolver only — nothing leaves the machine beyond a lookup the
    /// probes already performed.
    /// </summary>
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
