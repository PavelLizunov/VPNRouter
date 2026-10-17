using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public enum DetectedProblemKind
{
    GameTrafficThroughFullTunnel,
    MtuAbovePath,
    ServerTimeouts
}

public sealed record DetectedProblem(DetectedProblemKind Kind, int? SuggestedMtu = null);

public sealed record ProblemFixPlan(bool SwitchToSplit, int? NewMtu, bool Reconnect)
{
    public bool IsEmpty => !SwitchToSplit && NewMtu == null && !Reconnect;
}

/// <summary>
/// Turns the current routing mode, MTU, path-MTU probe and sing-box log into problems a non-technical user can fix
/// with one button on the home screen. Pure: the caller reads files and runs the probe.
/// </summary>
public static class ProblemDetector
{
    public const int MinimumAutoMtu = 1332;
    internal const int GameFlowPortThreshold = 3;
    internal const int TimeoutThreshold = 3;

    // Web, DNS, NTP, mail and push ports: proxied traffic to them is ordinary browsing, not a game or a call.
    private static readonly HashSet<int> OrdinaryPorts = new()
    {
        25, 53, 80, 110, 123, 143, 443, 465, 587, 853, 993, 995, 5222, 5223, 5228, 8080, 8443
    };

    private static readonly Regex OutboundLine = new(
        @"outbound/(?<type>[A-Za-z0-9_-]+)\[[^\]]*\]: outbound (?:packet )?connection to (?<dest>\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<DetectedProblem> Detect(
        bool fullTunnel,
        int configuredMtu,
        HealthCheck.PathMtuProbeResult? pathMtu,
        string? currentJson,
        string? singBoxLog)
    {
        var problems = new List<DetectedProblem>();
        var session = CurrentSession(singBoxLog ?? string.Empty);

        if (fullTunnel && CountProxiedGamePorts(session) >= GameFlowPortThreshold)
            problems.Add(new(DetectedProblemKind.GameTrafficThroughFullTunnel));

        if (SuggestMtu(configuredMtu, pathMtu) is { } mtu)
            problems.Add(new(DetectedProblemKind.MtuAbovePath, mtu));

        if (!string.IsNullOrWhiteSpace(currentJson) && session.Length > 0)
        {
            HashSet<string> endpoints;
            try { endpoints = HealthCheck.ExtractProxyEndpointsFromCurrentJson(currentJson); }
            catch (System.Text.Json.JsonException) { endpoints = new(); }
            if (endpoints.Count > 0 && HealthCheck.CountProxyDialTimeouts(session, endpoints) >= TimeoutThreshold)
                problems.Add(new(DetectedProblemKind.ServerTimeouts));
        }

        return problems;
    }

    public static ProblemFixPlan PlanFix(IReadOnlyList<DetectedProblem> problems, bool connected)
    {
        var split = problems.Any(p => p.Kind == DetectedProblemKind.GameTrafficThroughFullTunnel);
        var mtu = problems.FirstOrDefault(p => p.Kind == DetectedProblemKind.MtuAbovePath)?.SuggestedMtu;
        return new(split, mtu, connected && problems.Count > 0);
    }

    /// <summary>
    /// The same conservative value the "Pick from IPv4 ping" button writes, offered only when the configured MTU is
    /// above the measured IPv4 path (payload + 28) and the payload is not below <see cref="MinimumAutoMtu"/>.
    /// Only a lower value is ever suggested, and only from a measurement taken with the tunnel down, so a fixed MTU
    /// stays fixed: the next measurement of the same path suggests nothing.
    /// </summary>
    internal static int? SuggestMtu(int configuredMtu, HealthCheck.PathMtuProbeResult? pathMtu)
    {
        if (pathMtu is not { PlainPingBlocked: false, SkippedTunnelUp: false, BestPayload: { } payload })
            return null;
        if (configuredMtu <= payload + 28 || payload < MinimumAutoMtu)
            return null;
        var suggested = Math.Clamp(payload, MinimumAutoMtu, TunSettings.DefaultMtu);
        return suggested < configuredMtu ? suggested : null;
    }

    /// <summary>The log since the last core start, so flows of an earlier mode or session do not count.</summary>
    internal static string CurrentSession(string log)
    {
        var marker = log.LastIndexOf("sing-box started", StringComparison.Ordinal);
        if (marker < 0) return log;
        var lineStart = log.LastIndexOf('\n', marker);
        return log[(lineStart + 1)..];
    }

    /// <summary>Distinct destination ports outside <see cref="OrdinaryPorts"/> reached through a proxy outbound.</summary>
    internal static int CountProxiedGamePorts(string log)
    {
        var ports = new HashSet<int>();
        foreach (var line in log.Replace("\r\n", "\n").Split('\n'))
        {
            var match = OutboundLine.Match(line);
            if (!match.Success) continue;

            var type = match.Groups["type"].Value;
            if (type.Equals("direct", StringComparison.OrdinalIgnoreCase)
                || type.Equals("dns", StringComparison.OrdinalIgnoreCase)
                || type.Equals("block", StringComparison.OrdinalIgnoreCase))
                continue;

            var dest = match.Groups["dest"].Value;
            var colon = dest.LastIndexOf(':');
            if (colon < 0
                || !int.TryParse(dest.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || OrdinaryPorts.Contains(port))
                continue;

            ports.Add(port);
        }
        return ports.Count;
    }
}
