using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Detects WireGuard / AmneziaWG / Tailscale network interfaces and returns their
/// IPv4 subnets so VPNRouter can exclude them from TUN routing
/// (route_exclude_address). This lets the VPNs coexist without route conflicts.
/// For Tailscale the whole CGNAT range (100.64.0.0/10) is excluded so every
/// tailnet peer stays reachable while VPNRouter's tunnel is up.
/// </summary>
public static class NetworkInterfaceDetector
{
    /// <summary>
    /// Keywords to match in interface Description (case-insensitive).
    /// Covers: WireGuard, AmneziaWG, common WG tunnel adapters, and Tailscale
    /// (adapter description "Tailscale Tunnel").
    /// </summary>
    private static readonly string[] WgKeywords =
    {
        "WireGuard",
        "Amnezia",
        "AWG",
        "WG Tunnel",
        "Tailscale"
    };

    /// <summary>
    /// Scans all network interfaces for active WireGuard/AmneziaWG adapters
    /// and returns their IPv4 subnets in CIDR notation.
    /// </summary>
    /// <param name="ownTunName">VPNRouter's own TUN interface name to exclude (e.g. "VPNRouter-TUN")</param>
    /// <param name="logger">Serilog logger</param>
    /// <returns>List of CIDR subnets (e.g. ["10.9.1.0/24"]). Empty if none found.</returns>
    public static List<string> DetectWireGuardSubnets(string ownTunName, ILogger? logger)
    {
        var subnets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var iface in interfaces)
            {
                // Skip our own TUN interface
                if (iface.Name.Equals(ownTunName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Only check active interfaces
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;

                // Check if this is a WireGuard/AmneziaWG interface by description or name
                if (!IsWireGuardInterface(iface))
                    continue;

                logger?.Debug("[NetDetect] Found WG/AWG/Tailscale interface: {Name} ({Desc}), Status: {Status}",
                    iface.Name, iface.Description, iface.OperationalStatus);

                // Only a real Tailscale adapter gets the whole-/10 CGNAT exclusion
                // below; a WG/AWG tunnel that happens to be numbered out of 100.64.x
                // still gets its own /24, never the full /10 (avoids over-excluding
                // ISP CGNAT traffic — review nit, 2026-06-26).
                var isTailscale = IsTailscaleName(iface.Name, iface.Description);

                // Extract IPv4 subnets
                try
                {
                    var ipProps = iface.GetIPProperties();
                    foreach (var unicast in ipProps.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue; // Skip IPv6

                        // Tailscale hands out CGNAT 100.64.0.0/10 (RFC 6598) addresses with
                        // /32 masks; the whole tailnet lives in that /10, so for a Tailscale
                        // adapter exclude the full CGNAT range (the /24-widening below would
                        // miss peers outside the local /24, e.g. the Mac host at 100.116.x).
                        // Gated on isTailscale so a CGNAT-numbered WG/AWG tunnel can't
                        // over-exclude the /10 and leak real ISP-CGNAT traffic direct.
                        var subnet = (isTailscale && IsTailscaleCgnat(unicast.Address))
                            ? "100.64.0.0/10"
                            : CalculateSubnet(unicast.Address, unicast.IPv4Mask);
                        if (subnet != null)
                        {
                            subnets.Add(subnet);
                            logger?.Debug("[NetDetect]   Subnet: {Subnet} (from {Addr}/{Mask})",
                                subnet, unicast.Address, unicast.IPv4Mask);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warning("[NetDetect] Failed to read IP properties for {Name}: {Error}",
                        iface.Name, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Warning("[NetDetect] Failed to enumerate network interfaces: {Error}", ex.Message);
        }

        return subnets.ToList();
    }

    /// <summary>
    /// Checks if a network interface is a WireGuard or AmneziaWG adapter.
    /// Matches against known keywords in both Name and Description.
    /// </summary>
    private static bool IsWireGuardInterface(NetworkInterface iface)
        => IsWireGuardName(iface.Name, iface.Description);

    /// <summary>
    /// v3.0 Phase 2G (sub-wave 7b-1, 2026-05-18): name/description keyword
    /// match split out from <see cref="IsWireGuardInterface"/> so it can be
    /// unit-tested without instantiating a real <see cref="NetworkInterface"/>
    /// (which is abstract and only constructed by the OS network stack).
    /// </summary>
    internal static bool IsWireGuardName(string? name, string? description)
    {
        foreach (var keyword in WgKeywords)
        {
            if (description != null && description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
            if (name != null && name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the interface name/description identifies a Tailscale adapter
    /// (description "Tailscale Tunnel"). Distinct from <see cref="IsWireGuardName"/>
    /// — which also matches Tailscale via the shared keyword list — because ONLY a
    /// real Tailscale adapter may trigger the whole-/10 CGNAT exclusion; a WG/AWG
    /// tunnel numbered out of 100.64.x must keep its own /24. internal for tests.
    /// </summary>
    internal static bool IsTailscaleName(string? name, string? description)
        => (description != null && description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase))
        || (name != null && name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when <paramref name="address"/> is in Tailscale's CGNAT range
    /// (RFC 6598, 100.64.0.0/10 = 100.64.0.0 – 100.127.255.255). Used so a
    /// detected Tailscale adapter excludes the WHOLE tailnet, not just the local
    /// peer's /24. internal for unit-test coverage.
    /// </summary>
    internal static bool IsTailscaleCgnat(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    /// <summary>
    /// Calculates the network address from an IP + subnet mask and returns CIDR notation.
    /// Example: 10.9.1.2 + 255.255.255.0 → "10.9.1.0/24"
    ///
    /// WireGuard/AmneziaWG often uses point-to-point /32 masks. In that case we widen
    /// to /24 so the entire VPN subnet (e.g. 10.9.1.0/24) is excluded from TUN routing,
    /// otherwise only the local peer IP is excluded and traffic to other peers
    /// (like the gateway at 10.9.1.1) gets captured by TUN.
    ///
    /// v3.0 Phase 2G (sub-wave 7b-1, 2026-05-18): visibility widened from
    /// <c>private</c> to <c>internal</c> so unit tests can pin the
    /// edge cases (regular /24, point-to-point /32 widening, IPv6 reject).
    /// </summary>
    internal static string? CalculateSubnet(IPAddress address, IPAddress mask)
    {
        try
        {
            var addrBytes = address.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();

            if (addrBytes.Length != 4 || maskBytes.Length != 4)
                return null;

            int prefixLen = CountBits(maskBytes);

            // WireGuard point-to-point: /32 mask means only this peer's IP.
            // Widen to /24 to cover the whole VPN subnet (gateway, other peers).
            if (prefixLen >= 31)
            {
                maskBytes = new byte[] { 255, 255, 255, 0 };
                prefixLen = 24;
            }

            // Calculate network address: addr AND mask
            var networkBytes = new byte[4];
            for (int i = 0; i < 4; i++)
                networkBytes[i] = (byte)(addrBytes[i] & maskBytes[i]);

            var networkAddr = new IPAddress(networkBytes);

            return $"{networkAddr}/{prefixLen}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Counts the number of set bits (1s) in a subnet mask to get prefix length.
    /// Example: 255.255.255.0 → 24
    ///
    /// v3.0 Phase 2G (sub-wave 7b-1, 2026-05-18): visibility widened to
    /// <c>internal</c> for unit-test coverage.
    /// </summary>
    internal static int CountBits(byte[] maskBytes)
    {
        int count = 0;
        foreach (var b in maskBytes)
        {
            byte val = b;
            while (val != 0)
            {
                count += val & 1;
                val >>= 1;
            }
        }
        return count;
    }
}
