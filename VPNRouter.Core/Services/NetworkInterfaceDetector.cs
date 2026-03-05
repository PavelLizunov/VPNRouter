using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Detects WireGuard / AmneziaWG network interfaces and returns their IPv4 subnets
/// so VPNRouter can exclude them from TUN routing (route_exclude_address).
/// This allows both VPNs to coexist without route conflicts.
/// </summary>
public static class NetworkInterfaceDetector
{
    /// <summary>
    /// Keywords to match in interface Description (case-insensitive).
    /// Covers: WireGuard, AmneziaWG, and common WG tunnel adapters.
    /// </summary>
    private static readonly string[] WgKeywords =
    {
        "WireGuard",
        "Amnezia",
        "AWG",
        "WG Tunnel"
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

                logger?.Debug("[NetDetect] Found WG/AWG interface: {Name} ({Desc}), Status: {Status}",
                    iface.Name, iface.Description, iface.OperationalStatus);

                // Extract IPv4 subnets
                try
                {
                    var ipProps = iface.GetIPProperties();
                    foreach (var unicast in ipProps.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue; // Skip IPv6

                        var subnet = CalculateSubnet(unicast.Address, unicast.IPv4Mask);
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
    {
        foreach (var keyword in WgKeywords)
        {
            if (iface.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
            if (iface.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Calculates the network address from an IP + subnet mask and returns CIDR notation.
    /// Example: 10.9.1.2 + 255.255.255.0 → "10.9.1.0/24"
    ///
    /// WireGuard/AmneziaWG often uses point-to-point /32 masks. In that case we widen
    /// to /24 so the entire VPN subnet (e.g. 10.9.1.0/24) is excluded from TUN routing,
    /// otherwise only the local peer IP is excluded and traffic to other peers
    /// (like the gateway at 10.9.1.1) gets captured by TUN.
    /// </summary>
    private static string? CalculateSubnet(IPAddress address, IPAddress mask)
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
    /// </summary>
    private static int CountBits(byte[] maskBytes)
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
