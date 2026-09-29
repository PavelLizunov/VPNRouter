using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Serilog;

namespace VPNRouter.Core.Platform;

internal static class UnixFirewallServerIps
{
    internal static List<string> Parse(string configJson, Func<string, IReadOnlyList<string>> resolveHost, ILogger logger, string tag)
    {
        var ips = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var candidate = raw.Trim();

            if (IPAddress.TryParse(candidate, out var parsedIp))
            {
                var canonical = parsedIp.ToString();
                if (seen.Add(canonical))
                {
                    ips.Add(canonical);
                }
                return;
            }

            try
            {
                var resolved = resolveHost(candidate);
                if (resolved == null) return;
                foreach (var rip in resolved)
                {
                    if (string.IsNullOrWhiteSpace(rip)) continue;
                    var trimmedRip = rip.Trim();
                    if (IPAddress.TryParse(trimmedRip, out var parsedResolvedIp))
                    {
                        var canonical = parsedResolvedIp.ToString();
                        if (seen.Add(canonical))
                        {
                            ips.Add(canonical);
                        }
                    }
                    else
                    {
                        logger.Debug("[" + tag + "] ignored invalid resolver literal for {Host}", candidate);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[" + tag + "] could not resolve server hostname {Host} — kill-switch reconnect may need manual cleanup", candidate);
            }
        }

        using var doc = JsonDocument.Parse(configJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Expected JSON object root, got {root.ValueKind}.");

        if (root.TryGetProperty("outbounds", out var obs) && obs.ValueKind == JsonValueKind.Array)
        {
            foreach (var ob in obs.EnumerateArray())
            {
                if (ob.ValueKind == JsonValueKind.Object &&
                    ob.TryGetProperty("server", out var srv) &&
                    srv.ValueKind == JsonValueKind.String)
                {
                    AddCandidate(srv.GetString());
                }
            }
        }

        if (root.TryGetProperty("endpoints", out var eps) && eps.ValueKind == JsonValueKind.Array)
        {
            foreach (var ep in eps.EnumerateArray())
            {
                if (ep.ValueKind != JsonValueKind.Object) continue;

                if (!ep.TryGetProperty("type", out var typeProp) ||
                    typeProp.ValueKind != JsonValueKind.String ||
                    !string.Equals(typeProp.GetString(), "wireguard", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!ep.TryGetProperty("peers", out var peers) || peers.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var peer in peers.EnumerateArray())
                {
                    if (peer.ValueKind == JsonValueKind.Object &&
                        peer.TryGetProperty("address", out var addrProp) &&
                        addrProp.ValueKind == JsonValueKind.String)
                    {
                        AddCandidate(addrProp.GetString());
                    }
                }
            }
        }

        return ips;
    }

    internal static IReadOnlyList<string> ResolveHost(string host, ILogger logger, string tag)
    {
        try
        {
            var task = System.Net.Dns.GetHostAddressesAsync(host);
            if (!task.Wait(TimeSpan.FromSeconds(3)))
            {
                logger.Warning("[" + tag + "] DNS resolve of {Host} timed out — kill-switch reconnect may need manual cleanup", host);
                return Array.Empty<string>();
            }
            return task.Result
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork ||
                            a.AddressFamily == AddressFamily.InterNetworkV6)
                .Select(a => a.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "[" + tag + "] could not resolve server hostname {Host} — kill-switch reconnect may need manual cleanup", host);
            return Array.Empty<string>();
        }
    }
}
