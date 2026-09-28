using System;
using System.Collections.Generic;

namespace VPNRouter.Core.Platform.Unix;

internal static class MacDnsParsers
{
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
            if (line.StartsWith("There aren't any", System.StringComparison.OrdinalIgnoreCase))
                continue;
            if (LooksLikeIpAddress(line))
                result.Add(line);
        }
        return result;
    }

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
                var close = line.IndexOf(')');
                var name = line.Substring(close + 1).Trim();
                pendingService = name.Length > 0 ? name : null;
            }
            else if (line.StartsWith("(Hardware Port:", System.StringComparison.OrdinalIgnoreCase))
            {
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
        rest = rest.TrimEnd(')').Trim();
        var sp = rest.IndexOfAny(new[] { ' ', '\t', ',' });
        if (sp >= 0)
            rest = rest.Substring(0, sp);
        return rest.Length > 0 ? rest : null;
    }

    private static bool LooksLikeIpAddress(string s)
    {
        var quad = s.Split('.');
        if (quad.Length == 4)
        {
            foreach (var p in quad)
                if (!int.TryParse(p, out var n) || n < 0 || n > 255)
                    return false;
            return true;
        }
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
