using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;

namespace VPNRouter.Core.Services;

public static class NetworkInterfaceDetector
{
    private static readonly string[] WgKeywords =
    {
        "WireGuard",
        "Amnezia",
        "AWG",
        "WG Tunnel",
        "Tailscale"
    };

    public static List<string> DetectWireGuardSubnets(string ownTunName, ILogger? logger)
    {
        var subnets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var iface in interfaces)
            {
                if (iface.Name.Equals(ownTunName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (!IsWireGuardInterface(iface))
                    continue;

                logger?.Debug("[NetDetect] Found WG/AWG/Tailscale interface: {Name} ({Desc}), Status: {Status}",
                    iface.Name, iface.Description, iface.OperationalStatus);

                var isTailscale = IsTailscaleName(iface.Name, iface.Description);

                try
                {
                    var ipProps = iface.GetIPProperties();
                    foreach (var unicast in ipProps.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;

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

    public static (IPAddress? V4, IPAddress? V6) GetInternetInterfaceAddresses(string ownTunName, ILogger? logger)
    {
        var snapshots = new List<NicSnapshot>();
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.Name.Equals(ownTunName, StringComparison.OrdinalIgnoreCase))
                    continue;

                IPAddress? v4 = null, v6 = null;
                bool hasV4Gateway = false;
                try
                {
                    var ipProps = iface.GetIPProperties();
                    foreach (var g in ipProps.GatewayAddresses)
                    {
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !g.Address.Equals(IPAddress.Any))
                        {
                            hasV4Gateway = true;
                            break;
                        }
                    }
                    foreach (var u in ipProps.UnicastAddresses)
                    {
                        var a = u.Address;
                        if (v4 is null && a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                            v4 = a;
                        else if (v6 is null && a.AddressFamily == AddressFamily.InterNetworkV6 &&
                                 !a.IsIPv6LinkLocal && !IPAddress.IsLoopback(a))
                            v6 = a;
                    }
                }
                catch (Exception ex)
                {
                    logger?.Debug("[SplitTunnel] NIC {Name} IP-props read failed: {Err}", iface.Name, ex.Message);
                    continue;
                }

                snapshots.Add(new NicSnapshot(
                    iface.Name, iface.Description, iface.NetworkInterfaceType,
                    IsUp: iface.OperationalStatus == OperationalStatus.Up,
                    HasV4Gateway: hasV4Gateway, V4: v4, V6: v6));
            }
        }
        catch (Exception ex)
        {
            logger?.Warning("[SplitTunnel] Failed to enumerate NICs for internet-pick: {Err}", ex.Message);
            return (null, null);
        }

        var pick = SplitTunnelDriverProtocol.PickInternetInterface(snapshots);
        if (pick is null)
        {
            logger?.Warning("[SplitTunnel] No internet-facing NIC found for split-tunnel bind");
            return (null, null);
        }

        logger?.Information("[SplitTunnel] Internet NIC for split-bind: {Name} ({Desc}) v4={V4} v6={V6}",
            pick.Value.Name, pick.Value.Description, pick.Value.V4, pick.Value.V6);
        return (pick.Value.V4, pick.Value.V6);
    }

    private static bool IsWireGuardInterface(NetworkInterface iface)
        => IsWireGuardName(iface.Name, iface.Description);

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

    internal static bool IsTailscaleName(string? name, string? description)
        => (description != null && description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase))
        || (name != null && name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase));

    internal static bool IsTailscaleCgnat(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    internal static string? CalculateSubnet(IPAddress address, IPAddress mask)
    {
        try
        {
            var addrBytes = address.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();

            if (addrBytes.Length != 4 || maskBytes.Length != 4)
                return null;

            int prefixLen = CountBits(maskBytes);

            if (prefixLen >= 31)
            {
                maskBytes = new byte[] { 255, 255, 255, 0 };
                prefixLen = 24;
            }

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
