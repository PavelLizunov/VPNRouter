#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // Bound individual records so a large legacy history can be read a record at a time.
    private const int ReadBufferBytes = 64 * 1024;
    private const int MaxRecordStringLength = 4096;

    // Newest records win when the store is over the bound.
    internal const int MaxRecords = 2048;

    private static readonly object Gate = new();
    private static Dictionary<string, ServerHealthRecordDto>? _cache;
    private static string? _cachePath;
    private static bool _cacheNeedsSave;

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
                map.TryGetValue(key, out var prior);
                providerKey ??= prior?.ProviderKey;
                if (!IsRecordSizeValid(key, providerKey)) return;

                var record = new ServerHealthRecordDto
                {
                    Key = key,
                    Verdict = verdict,
                    RecordedAt = now ?? DateTimeOffset.UtcNow,
                    ProviderKey = providerKey,
                };

                if (prior is not null && RecordsEquivalent(prior, record) && !_cacheNeedsSave)
                    return;

                map[key] = record;
                try
                {
                    SaveLocked(map);
                    _cacheNeedsSave = false;
                }
                catch
                {
                    _cacheNeedsSave = true;
                    throw;
                }
            }
            catch { }
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
        lock (Gate) { _cache = null; _cachePath = null; _cacheNeedsSave = false; }
    }

    private static Dictionary<string, ServerHealthRecordDto> LoadLocked()
    {
        var path = StorePath;
        if (_cache != null && string.Equals(_cachePath, path, StringComparison.OrdinalIgnoreCase))
            return _cache;

        var map = new Dictionary<string, ServerHealthRecordDto>(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        try
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete, bufferSize: 4096, options: FileOptions.SequentialScan);
                changed = ReadRecords(stream, map, DateTimeOffset.UtcNow);
            }
        }
        catch
        {
            map.Clear();
        }

        _cache = map;
        _cachePath = path;
        _cacheNeedsSave = false;

        if (changed)
        {
            try { SaveLocked(map); }
            catch { _cacheNeedsSave = true; }
        }

        return map;
    }

    private static bool ReadRecords(Stream stream, Dictionary<string, ServerHealthRecordDto> map,
        DateTimeOffset now)
    {
        var buffer = new byte[ReadBufferBytes];
        var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 32 });
        var bytes = stream.ReadAtLeast(buffer, 3, throwOnEndOfStream: false);
        var offset = buffer.AsSpan(0, bytes).StartsWith("\uFEFF"u8) ? 3 : 0;
        var isFinal = bytes < 3;
        var inRecords = false;
        var pendingRecordsArray = false;
        var changed = false;
        var cutoff = now - FreshTtl;

        while (true)
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(offset, bytes - offset), isFinal, state);
            var consumed = offset;
            while (reader.Read())
            {
                if (pendingRecordsArray)
                {
                    map.Clear();
                    pendingRecordsArray = false;
                    inRecords = reader.TokenType == JsonTokenType.StartArray;
                    if (!inRecords && reader.TokenType != JsonTokenType.Null)
                        throw new JsonException("Expected a server health record array.");
                }
                else if (inRecords && reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 1)
                {
                    inRecords = false;
                }
                else if (inRecords)
                {
                    // Keep the reader at the last complete token until this record is buffered.
                    var end = reader;
                    if (!end.TrySkip()) break;
                    var record = JsonSerializer.Deserialize(ref reader, Json.AppJsonContext.Default.ServerHealthRecordDto);
                    if (record is null || record.RecordedAt < cutoff || !IsRecordSizeValid(record.Key, record.ProviderKey))
                    {
                        changed = true;
                    }
                    else
                    {
                        map[record.Key] = record;
                        if (map.Count > 2 * MaxRecords)
                            changed |= PruneInPlace(map, now);
                    }
                }
                else if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    pendingRecordsArray = reader.ValueTextEquals("Records"u8) ||
                        string.Equals(reader.GetString(), "Records", StringComparison.OrdinalIgnoreCase);
                }

                state = reader.CurrentState;
                consumed = offset + (int)reader.BytesConsumed;
            }

            if (isFinal)
                return PruneInPlace(map, now) || changed;

            var remaining = bytes - consumed;
            if (remaining == buffer.Length)
                throw new JsonException("Server health record exceeds the buffer.");
            buffer.AsSpan(consumed, remaining).CopyTo(buffer);
            var read = stream.Read(buffer.AsSpan(remaining));
            bytes = remaining + read;
            offset = 0;
            isFinal = read == 0;
        }
    }

    private static void SaveLocked(Dictionary<string, ServerHealthRecordDto> map)
    {
        PruneInPlace(map, DateTimeOffset.UtcNow);

        var path = StorePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var dto = new ServerHealthFileDto { Records = map.Values.ToList() };

        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(
                    stream, dto, Json.AppJsonContext.Default.ServerHealthFileDto);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
                try { File.Delete(tmp); } catch { }
        }
    }

    private static bool PruneInPlace(
        Dictionary<string, ServerHealthRecordDto> map, DateTimeOffset now)
    {
        var changed = false;
        var cutoff = now - FreshTtl;

        foreach (var key in map.Keys.ToList())
        {
            if (map[key].RecordedAt < cutoff)
            {
                map.Remove(key);
                changed = true;
            }
        }

        if (map.Count > MaxRecords)
        {
            var keep = map
                .OrderByDescending(kv => kv.Value.RecordedAt)
                .Take(MaxRecords)
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var key in map.Keys.ToList())
            {
                if (!keep.Contains(key))
                {
                    map.Remove(key);
                    changed = true;
                }
            }
        }

        return changed;
    }

    private static bool RecordsEquivalent(
        ServerHealthRecordDto left, ServerHealthRecordDto right)
        => left.Verdict == right.Verdict
           && left.RecordedAt == right.RecordedAt
           && string.Equals(left.ProviderKey, right.ProviderKey, StringComparison.Ordinal);

    private static bool IsRecordSizeValid(string? key, string? providerKey) =>
        key is { Length: > 0 and <= MaxRecordStringLength }
        && providerKey is not { Length: > MaxRecordStringLength };
}
