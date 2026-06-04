using System;
using System.Collections.Generic;

namespace VPNRouter.Core.Platform.Unix;

/// <summary>
/// Pure parsers for the macOS DNS-hardening flow (Fix #1, deep-audit 2026-06-04).
/// Lives outside the <c>#if !PLATFORM_WINDOWS</c> guard on purpose: these are
/// string transforms with no OS dependency, so they compile into the Windows
/// test build and the bug-prone parsing of <c>networksetup</c> / <c>route</c>
/// output is pinned headless BEFORE the Mac-only orchestrator (MacDnsHardening)
/// wires them to real commands and gets verified on the Mac host.
///
/// <para>Why DNS hardening is needed: on macOS, mDNSResponder reads its upstream
/// resolver from the SystemConfiguration of the primary network service (en0 →
/// ISP), NOT from the routing table — so DNS leaves on en0 and never enters
/// utun99, bypassing sing-box's hijack-dns. The fix pins the system resolver to
/// the TUN gateway (e.g. 172.19.0.1) so queries enter the tunnel and get
/// hijacked, then restores the original on stop/crash.</para>
/// </summary>
internal static class MacDnsParsers
{
    /// <summary>
    /// Derive the DNS target address from the TUN's CIDR (e.g.
    /// <c>"172.19.0.1/30"</c> → <c>"172.19.0.1"</c>). The TUN's own address is
    /// the most reliable target: a packet to it is delivered locally to the TUN
    /// interface, so it always enters sing-box and gets hijack-dns'd, regardless
    /// of auto_route's upstream-DNS exclusions. Returns null if the input is not
    /// a plausible dotted-quad (+ optional /prefix).
    /// </summary>
    public static string? DeriveDnsTarget(string? tunIpv4Cidr)
    {
        if (string.IsNullOrWhiteSpace(tunIpv4Cidr))
            return null;

        var slash = tunIpv4Cidr.IndexOf('/');
        var addr = (slash >= 0 ? tunIpv4Cidr.Substring(0, slash) : tunIpv4Cidr).Trim();

        var parts = addr.Split('.');
        if (parts.Length != 4)
            return null;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var n) || n < 0 || n > 255)
                return null;
        }
        return addr;
    }

    /// <summary>
    /// Parse <c>networksetup -getdnsservers &lt;service&gt;</c> output into the
    /// list of configured resolver IPs. When no DNS is set the tool prints
    /// "There aren't any DNS Servers set on &lt;service&gt;." — represented here
    /// as an EMPTY list (which on restore maps back to <c>networksetup
    /// -setdnsservers &lt;service&gt; empty</c>, i.e. "use DHCP").
    /// </summary>
    public static List<string> ParseGetDnsServers(string? output)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
            return result;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            // The "There aren't any DNS Servers set on ..." sentinel — not an IP.
            if (line.StartsWith("There aren't any", System.StringComparison.OrdinalIgnoreCase))
                continue;
            // Only accept dotted-quad / hex-colon (IPv6) tokens; skip any stray prose.
            if (LooksLikeIpAddress(line))
                result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// Parse the interface device out of <c>route -n get default</c> output
    /// (the line "  interface: en0"). Returns null when absent (no default
    /// route — i.e. offline).
    /// </summary>
    public static string? ParseDefaultRouteDevice(string? routeGetDefaultOutput)
    {
        if (string.IsNullOrWhiteSpace(routeGetDefaultOutput))
            return null;

        foreach (var raw in routeGetDefaultOutput.Split('\n'))
        {
            var line = raw.Trim();
            const string key = "interface:";
            if (line.StartsWith(key, System.StringComparison.OrdinalIgnoreCase))
            {
                var dev = line.Substring(key.Length).Trim();
                return dev.Length > 0 ? dev : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Map a BSD device (e.g. <c>en0</c>) to its user-facing network service
    /// name (e.g. <c>Wi-Fi</c>) from <c>networksetup -listnetworkserviceorder</c>
    /// output. That tool prints repeating pairs:
    /// <code>
    /// (1) Wi-Fi
    /// (Hardware Port: Wi-Fi, Device: en0)
    /// </code>
    /// We need the SERVICE name (line 1, after the "(N) " prefix) for the entry
    /// whose "Device: &lt;device&gt;" matches. Returns null if not found.
    /// </summary>
    public static string? ParseServiceForDevice(string? listOrderOutput, string? device)
    {
        if (string.IsNullOrWhiteSpace(listOrderOutput) || string.IsNullOrWhiteSpace(device))
            return null;

        string? pendingService = null;
        foreach (var raw in listOrderOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("(") && line.Contains(')') && !line.Contains("Hardware Port:"))
            {
                // "(1) Wi-Fi" or "(*) Foo" (disabled) → take the name after ") ".
                var close = line.IndexOf(')');
                var name = line.Substring(close + 1).Trim();
                pendingService = name.Length > 0 ? name : null;
            }
            else if (line.StartsWith("(Hardware Port:", System.StringComparison.OrdinalIgnoreCase))
            {
                // "(Hardware Port: Wi-Fi, Device: en0)" — match the device.
                var dev = ExtractDevice(line);
                if (dev != null && string.Equals(dev, device, System.StringComparison.Ordinal))
                    return pendingService;
            }
        }
        return null;
    }

    private static string? ExtractDevice(string hardwarePortLine)
    {
        const string key = "Device:";
        var idx = hardwarePortLine.IndexOf(key, System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;
        var rest = hardwarePortLine.Substring(idx + key.Length).Trim();
        // Trim trailing ")" and any whitespace; device is a single token.
        rest = rest.TrimEnd(')').Trim();
        var sp = rest.IndexOfAny(new[] { ' ', '\t', ',' });
        if (sp >= 0)
            rest = rest.Substring(0, sp);
        return rest.Length > 0 ? rest : null;
    }

    private static bool LooksLikeIpAddress(string s)
    {
        // IPv4 dotted-quad.
        var quad = s.Split('.');
        if (quad.Length == 4)
        {
            foreach (var p in quad)
                if (!int.TryParse(p, out var n) || n < 0 || n > 255)
                    return false;
            return true;
        }
        // IPv6: contains a colon and only hex/colon chars.
        if (s.Contains(':'))
        {
            foreach (var c in s)
                if (!Uri.IsHexDigit(c) && c != ':')
                    return false;
            return true;
        }
        return false;
    }
}
