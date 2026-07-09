#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>One persisted health verdict for a server identity.</summary>
public sealed class ServerHealthRecordDto
{
    public string Key { get; set; } = string.Empty;
    public ServerHealthVerdict Verdict { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>Schema root for <c>cache/server_health.json</c>.</summary>
public sealed class ServerHealthFileDto
{
    public int SchemaVersion { get; set; } = 1;
    public List<ServerHealthRecordDto> Records { get; set; } = new();
}

/// <summary>
/// urltest R5 — tiny persisted store of per-server health verdicts so the
/// verdicts survive an app restart and can inform the Auto (urltest) pool:
/// <see cref="ConfigGenerator"/> drops pool members with a FRESH
/// <see cref="ServerHealthVerdict.ProtocolHandshakeBlockedLikely"/> verdict
/// (fail-open — never below one member), and the UI shows verdict age.
///
/// <para>Written by the App layer when probes complete (best-effort — a
/// locked/missing cache dir must never break testing), read at config-gen
/// time. Identity key = <c>server:port:protocol</c> — survives subscription
/// refreshes that recreate <see cref="VlessServerEntry"/> instances.
/// Freshness TTL <see cref="FreshTtl"/>: RU block state changes fast, a
/// stale blocked verdict must not keep excluding a recovered server.</para>
/// </summary>
public static class ServerHealthStore
{
    /// <summary>A blocked verdict older than this no longer influences the pool.</summary>
    public static readonly TimeSpan FreshTtl = TimeSpan.FromHours(12);

    private static readonly object Gate = new();
    private static Dictionary<string, ServerHealthRecordDto>? _cache;
    private static string? _cachePath;   // path the cache was loaded for (test-env swaps)

    private static string StorePath => Path.Combine(AppPaths.CacheDir, "server_health.json");

    /// <summary>Stable identity for a server entry (name-independent).</summary>
    public static string KeyFor(VlessServerEntry entry)
        => $"{entry.Server}:{entry.Port}:{(entry.Protocol ?? "vless").Trim().ToLowerInvariant()}";

    /// <summary>
    /// Persist a verdict (write-through). <see cref="ServerHealthVerdict.Unknown"/>
    /// is ignored — "no signal" must not overwrite a real one. Best-effort:
    /// any I/O failure is swallowed (verdicts also live in the UI session).
    /// </summary>
    public static void Record(VlessServerEntry entry, ServerHealthVerdict verdict, DateTimeOffset? now = null)
    {
        if (entry is null || verdict == ServerHealthVerdict.Unknown) return;
        lock (Gate)
        {
            try
            {
                var map = LoadLocked();
                map[KeyFor(entry)] = new ServerHealthRecordDto
                {
                    Key = KeyFor(entry),
                    Verdict = verdict,
                    RecordedAt = now ?? DateTimeOffset.UtcNow,
                };
                SaveLocked(map);
            }
            catch { /* best-effort persistence */ }
        }
    }

    /// <summary>Fresh (within TTL) verdict for the entry, or null.</summary>
    public static ServerHealthVerdict? GetFresh(VlessServerEntry entry, DateTimeOffset? now = null)
        => GetFreshRecord(entry, now)?.Verdict;

    /// <summary>Fresh (within TTL) record incl. its timestamp, or null.</summary>
    public static ServerHealthRecordDto? GetFreshRecord(VlessServerEntry entry, DateTimeOffset? now = null)
    {
        if (entry is null) return null;
        lock (Gate)
        {
            try
            {
                var map = LoadLocked();
                if (!map.TryGetValue(KeyFor(entry), out var rec)) return null;
                var t = now ?? DateTimeOffset.UtcNow;
                return (t - rec.RecordedAt) <= FreshTtl ? rec : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Drop the in-memory cache so the next call re-reads the file (tests / dir swaps).</summary>
    public static void ResetForTests()
    {
        lock (Gate) { _cache = null; _cachePath = null; }
    }

    private static Dictionary<string, ServerHealthRecordDto> LoadLocked()
    {
        var path = StorePath;
        if (_cache != null && string.Equals(_cachePath, path, StringComparison.OrdinalIgnoreCase))
            return _cache;

        var map = new Dictionary<string, ServerHealthRecordDto>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
            {
                var dto = JsonSerializer.Deserialize(
                    File.ReadAllText(path), Json.AppJsonContext.Default.ServerHealthFileDto);
                if (dto?.Records != null)
                    foreach (var r in dto.Records)
                        if (!string.IsNullOrEmpty(r.Key))
                            map[r.Key] = r;
            }
        }
        catch { /* corrupt/unreadable cache = start empty */ }

        _cache = map;
        _cachePath = path;
        return map;
    }

    private static void SaveLocked(Dictionary<string, ServerHealthRecordDto> map)
    {
        var path = StorePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var dto = new ServerHealthFileDto { Records = new List<ServerHealthRecordDto>(map.Values) };
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Json.AppJsonContext.Default.ServerHealthFileDto));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}
