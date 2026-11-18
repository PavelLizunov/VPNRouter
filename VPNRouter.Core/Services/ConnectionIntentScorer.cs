using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public sealed record ConnectionIntentPick(VlessServerEntry Server, string Reason);

public static class ConnectionIntentScorer
{
    public static ConnectionIntentPick? Pick(
        IEnumerable<VlessServerEntry> servers,
        string? intent,
        string? activeName = null)
    {
        var list = servers?.Where(s => s != null).ToList() ?? new List<VlessServerEntry>();
        if (list.Count == 0) return null;

        var normalized = ConnectionIntent.Normalize(intent);
        var chosen = list
            .OrderByDescending(s => Score(s, normalized, string.Equals(s.Name, activeName, StringComparison.Ordinal)))
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .First();

        return new(chosen, Reason(chosen, normalized));
    }

    public static VlessServerEntry? PickServer(
        IEnumerable<ServerLiveness> results,
        string? intent,
        string? activeName) =>
        PickLiveServer(results, intent, row => string.Equals(row.Name, activeName, StringComparison.Ordinal) &&
            (!string.IsNullOrEmpty(activeName) || ConnectionIntent.Normalize(intent) != ConnectionIntent.General));

    internal static VlessServerEntry? PickRegisteredServer(IEnumerable<ServerLiveness> results,
        string? intent, VlessServerEntry selected) =>
        PickLiveServer(results, intent, row => ReferenceEquals(row, selected));

    private static VlessServerEntry? PickLiveServer(IEnumerable<ServerLiveness> results, string? intent,
        Func<VlessServerEntry, bool> isSelected)
    {
        var alive = results?.Where(r => r.Alive).ToList() ?? new List<ServerLiveness>();
        if (alive.Count == 0) return null;

        if (ConnectionIntent.Normalize(intent) == ConnectionIntent.General)
            return alive.FirstOrDefault(r => isSelected(r.Server))?.Server ?? ServerHealthProbe.PickBest(alive);

        return alive
            .OrderByDescending(r => Score(r.Server, intent, isSelected(r.Server)))
            .ThenBy(r => r.LatencyMs)
            .Select(r => r.Server)
            .FirstOrDefault();
    }

    private static int Score(VlessServerEntry s, string? intent, bool selected)
    {
        var score = selected ? 10 : 0;
        var proto = (s.Protocol ?? "vless").Trim().ToLowerInvariant();

        return ConnectionIntent.Normalize(intent) switch
        {
            ConnectionIntent.Gaming => score + proto switch
            {
                "amneziawg" or "awg" => 100,
                "hysteria2" or "hy2" or "tuic" => 90,
                "naive" => 50,
                "vless" => string.IsNullOrWhiteSpace(s.Flow) ? 35 : 25,
                _ => 40
            },
            ConnectionIntent.Privacy => score + proto switch
            {
                "amneziawg" or "awg" => 80,
                "hysteria2" or "hy2" or "tuic" => 75,
                "vless" => 70,
                _ => 50
            },
            ConnectionIntent.Compatibility => score + proto switch
            {
                "vless" => 90,
                "naive" => 85,
                "shadowsocks" or "ss" => 75,
                "hysteria2" or "hy2" or "tuic" => 60,
                _ => 50
            },
            _ => score
        };
    }

    private static string Reason(VlessServerEntry s, string intent)
    {
        var proto = (s.Protocol ?? "vless").Trim();
        return intent switch
        {
            ConnectionIntent.Gaming => $"{proto}: better fit for games/voice than TCP-only VLESS",
            ConnectionIntent.Privacy => $"{proto}: keeps traffic through VPN; direct bypass is not automatic",
            ConnectionIntent.Compatibility => $"{proto}: conservative compatibility pick",
            _ => "kept the existing/manual priority"
        };
    }
}
