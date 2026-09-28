#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public sealed class ServerHealthRecordDto
{
    public string Key { get; set; } = string.Empty;
    public ServerHealthVerdict Verdict { get; set; }
    public DateTimeOffset RecordedAt { get; set; }

    public string? ProviderKey { get; set; }
}

public sealed class ServerHealthFileDto
{
    public int SchemaVersion { get; set; } = 1;
    public List<ServerHealthRecordDto> Records { get; set; } = new();
}

public static class ServerHealthStore
{
    public static readonly TimeSpan FreshTtl = TimeSpan.FromHours(12);

    private static readonly object Gate = new();
    private static Dictionary<string, ServerHealthRecordDto>? _cache;
    private static string? _cachePath;

    private static string StorePath => Path.Combine(AppPaths.CacheDir, "server_health.json");

    public static string KeyFor(VlessServerEntry entry)
        => $"{entry.Server}:{entry.Port}:{(entry.Protocol ?? "vless").Trim().ToLowerInvariant()}";

    public static void Record(VlessServerEntry entry, ServerHealthVerdict verdict,
        DateTimeOffset? now = null, string? providerKey = null)
    {
        if (entry is null || verdict == ServerHealthVerdict.Unknown) return;
        lock (Gate)
        {
            try
            {
                var map = LoadLocked();
                var key = KeyFor(entry);
                if (providerKey is null && map.TryGetValue(key, out var prior))
                    providerKey = prior.ProviderKey;
                map[key] = new ServerHealthRecordDto
                {
                    Key = key,
                    Verdict = verdict,
                    RecordedAt = now ?? DateTimeOffset.UtcNow,
                    ProviderKey = providerKey,
                };
                SaveLocked(map);
            }
            catch {  }
        }
    }

    public static ServerHealthVerdict? GetFresh(VlessServerEntry entry, DateTimeOffset? now = null)
        => GetFreshRecord(entry, now)?.Verdict;

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
        catch {  }

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
        File.Move(tmp, path, overwrite: true);
    }
}
