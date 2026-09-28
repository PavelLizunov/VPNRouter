using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using StjJson = System.Text.Json.JsonSerializer;

namespace VPNRouter.Tests;

public sealed class CacheRecoveryTests
{
    [Fact]
    public void LoadOrRecover_FileMissing_ReturnsNotFound()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("missing.json");

        var result = CacheRecovery.LoadOrRecover<DummyCache>(
            path,
            expectedSchemaVersion: 1,
            deserialize: j => StjJson.Deserialize<DummyCache>(j));

        Assert.Equal(RecoveryReason.NotFound, result.Reason);
        Assert.Null(result.Value);
        Assert.False(result.Loaded);
        Assert.False(result.ShouldRebuild,
            "NotFound is a clean first-launch state, not corruption.");
    }

    [Fact]
    public void LoadOrRecover_ValidV1_ReturnsLoadedAndPreservesContent()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("ok.json");
        File.WriteAllText(path,
            "{\"schema_version\":1,\"name\":\"alpha\",\"items\":[\"a\",\"b\"]}");

        var result = CacheRecovery.LoadOrRecover<DummyCache>(
            path,
            expectedSchemaVersion: 1,
            deserialize: j => StjJson.Deserialize<DummyCache>(j));

        Assert.Equal(RecoveryReason.Success, result.Reason);
        Assert.True(result.Loaded);
        Assert.NotNull(result.Value);
        Assert.Equal("alpha", result.Value!.Name);
        Assert.Equal(2, result.Value.Items.Count);
        Assert.Empty(EnumerateCorruptBackups(path));
    }

    [Fact]
    public void LoadOrRecover_LegacyWithoutSchemaVersion_QuarantinesAndReturnsSchemaMissing()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("legacy.json");
        File.WriteAllText(path, "{\"name\":\"legacy\",\"items\":[\"x\"]}");

        var result = CacheRecovery.LoadOrRecover<DummyCache>(
            path,
            expectedSchemaVersion: 1,
            deserialize: j => StjJson.Deserialize<DummyCache>(j));

        Assert.Equal(RecoveryReason.SchemaMissing, result.Reason);
        Assert.Null(result.Value);
        Assert.True(result.ShouldRebuild);
        Assert.False(File.Exists(path));
        var backups = EnumerateCorruptBackups(path);
        Assert.Single(backups);
        Assert.Contains("legacy",
            File.ReadAllText(backups.Single()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void LoadOrRecover_TruncatedJson_QuarantinesAndReturnsJsonMalformed()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("truncated.json");
        File.WriteAllText(path, "{\"schema_version\":1,\"items\":[\"a\",\"b");

        var result = CacheRecovery.LoadOrRecover<DummyCache>(
            path,
            expectedSchemaVersion: 1,
            deserialize: j => StjJson.Deserialize<DummyCache>(j));

        Assert.Equal(RecoveryReason.JsonMalformed, result.Reason);
        Assert.Null(result.Value);
        Assert.True(result.ShouldRebuild);
        Assert.False(File.Exists(path));
        Assert.Single(EnumerateCorruptBackups(path));
    }

    [Fact]
    public void LoadOrRecover_OlderSchema_QuarantinesAndReturnsSchemaMismatch()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("old.json");
        File.WriteAllText(path,
            "{\"schema_version\":1,\"name\":\"old\",\"items\":[]}");

        var result = CacheRecovery.LoadOrRecover<DummyCache>(
            path,
            expectedSchemaVersion: 2,
            deserialize: j => StjJson.Deserialize<DummyCache>(j));

        Assert.Equal(RecoveryReason.SchemaMismatch, result.Reason);
        Assert.True(result.ShouldRebuild);
        Assert.False(File.Exists(path));
        Assert.Single(EnumerateCorruptBackups(path));
    }

    [Fact]
    public void LoadOrRecover_StructurallyInvalid_QuarantinesAndReturnsStructurallyInvalid()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("structural.json");
        File.WriteAllText(path,
            "{\"schema_version\":1,\"name\":\"alpha\",\"items\":null}");

        var result = CacheRecovery.LoadOrRecover<DummyCache>(
            path,
            expectedSchemaVersion: 1,
            deserialize: j => StjJson.Deserialize<DummyCache>(j),
            structuralCheck: c => c.Items is not null);

        Assert.Equal(RecoveryReason.StructurallyInvalid, result.Reason);
        Assert.True(result.ShouldRebuild);
        Assert.False(File.Exists(path));
        Assert.Single(EnumerateCorruptBackups(path));
    }

    [Fact]
    public void LoadOrRecover_FutureSchemaForwardCompat_PassesThrough()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("future.json");
        File.WriteAllText(path,
            "{\"schema_version\":99,\"name\":\"future\",\"items\":[\"x\"]}");

        var result = CacheRecovery.LoadOrRecover<DummyCache>(
            path,
            expectedSchemaVersion: 1,
            deserialize: j => StjJson.Deserialize<DummyCache>(j));

        Assert.Equal(RecoveryReason.Success, result.Reason);
        Assert.True(result.Loaded);
        Assert.True(File.Exists(path),
            "Future-schema files must NOT be quarantined (they may parse cleanly).");
    }

    [Fact]
    public void LoadOrRecover_DeserializerThrows_QuarantinesAndReturnsJsonMalformed()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("strict.json");
        File.WriteAllText(path,
            "{\"schema_version\":1,\"name\":\"x\",\"items\":[]}");

        var result = CacheRecovery.LoadOrRecover<DummyCache>(
            path,
            expectedSchemaVersion: 1,
            deserialize: _ => throw new InvalidOperationException("strict reject"));

        Assert.Equal(RecoveryReason.JsonMalformed, result.Reason);
        Assert.False(File.Exists(path));
        Assert.Single(EnumerateCorruptBackups(path));
    }

    [Fact]
    public void LoadOrRecover_DeserializerReturnsNull_QuarantinesAndReturnsJsonMalformed()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("nullret.json");
        File.WriteAllText(path,
            "{\"schema_version\":1,\"name\":\"x\",\"items\":[]}");

        var result = CacheRecovery.LoadOrRecover<DummyCache>(
            path,
            expectedSchemaVersion: 1,
            deserialize: _ => null);

        Assert.Equal(RecoveryReason.JsonMalformed, result.Reason);
        Assert.False(File.Exists(path));
        Assert.Single(EnumerateCorruptBackups(path));
    }

    [Fact]
    public void LoadOrRecover_TwoCorruptionsSameSecond_BothBackupsPreserved()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("burst.json");

        File.WriteAllText(path, "{not json");
        var first = CacheRecovery.LoadOrRecover<DummyCache>(
            path, 1, j => StjJson.Deserialize<DummyCache>(j));
        Assert.Equal(RecoveryReason.JsonMalformed, first.Reason);

        File.WriteAllText(path, "{still not json");
        var second = CacheRecovery.LoadOrRecover<DummyCache>(
            path, 1, j => StjJson.Deserialize<DummyCache>(j));
        Assert.Equal(RecoveryReason.JsonMalformed, second.Reason);

        Assert.Equal(2, EnumerateCorruptBackups(path).Count);
    }

    [Fact]
    public void FreeConfigCache_Load_OnLegacyFile_QuarantinesAndReturnsEmpty()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("free_configs.json");
        File.WriteAllText(path,
            "{\"LastAggregatedAt\":\"0001-01-01T00:00:00\",\"Configs\":[]}");

        var cache = new FreeConfigCache(NullLogger(), path);
        var loaded = cache.Load();

        Assert.NotNull(loaded);
        Assert.Empty(loaded.Configs);
        Assert.False(File.Exists(path),
            "Legacy free_configs.json must be quarantined on first read.");
        Assert.Single(EnumerateCorruptBackups(path));
    }

    [Fact]
    public void FreeConfigCache_SaveLoadRoundTrip_PreservesEntries()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("free_configs.json");
        var cache = new FreeConfigCache(NullLogger(), path);

        var file = new FreeConfigCache.CacheFile
        {
            LastAggregatedAt = new DateTime(2026, 5, 7, 0, 0, 0, DateTimeKind.Utc),
            Configs =
            {
                new FreeConfigEntry
                {
                    Host = "1.2.3.4",
                    Port = 443,
                    LatencyMs = 3,
                    Status = FreeConfigStatus.Verified,
                },
                new FreeConfigEntry
                {
                    Host = "5.6.7.8",
                    Port = 443,
                    LatencyMs = 42,
                    Status = FreeConfigStatus.Verified,
                },
            },
        };
        cache.Save(file);

        var roundTrip = cache.Load();
        Assert.Equal(2, roundTrip.Configs.Count);
        Assert.Equal(0, roundTrip.Configs[0].LatencyMs);
        Assert.Equal(42, roundTrip.Configs[1].LatencyMs);
    }

    [Fact]
    public void FreeConfigCache_Load_OnTruncatedJson_QuarantinesAndReturnsEmpty()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("free_configs.json");
        File.WriteAllText(path,
            "{\"schema_version\":1,\"Configs\":[{\"Host\":\"1.2.3.4\",\"Po");

        var cache = new FreeConfigCache(NullLogger(), path);
        var loaded = cache.Load();

        Assert.NotNull(loaded);
        Assert.Empty(loaded.Configs);
        Assert.False(File.Exists(path));
        Assert.Single(EnumerateCorruptBackups(path));
    }

    [Fact]
    public void FreeConfigCache_Load_OnSchemaTooOld_QuarantinesAndReturnsEmpty()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("free_configs.json");
        File.WriteAllText(path,
            "{\"schema_version\":0,\"Configs\":[]}");

        var cache = new FreeConfigCache(NullLogger(), path);
        var loaded = cache.Load();

        Assert.NotNull(loaded);
        Assert.Empty(loaded.Configs);
        Assert.False(File.Exists(path));
        Assert.Single(EnumerateCorruptBackups(path));
    }

    [Fact]
    public void FreeConfigCache_Save_StampsCurrentSchemaVersion()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("free_configs.json");
        var cache = new FreeConfigCache(NullLogger(), path);

        var file = new FreeConfigCache.CacheFile { SchemaVersion = 0 };
        cache.Save(file);

        var raw = File.ReadAllText(path);
        Assert.Matches("\"schema_version\"\\s*:\\s*1", raw);
    }

    [Fact]
    public void ProfileCache_Load_OnLegacyRawProfileCollection_QuarantinesAndReturnsRebuild()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("profiles.json");
        var legacy = StjJson.Serialize(new ProfileCollection
        {
            Profiles = new List<Profile>
            {
                new() { Name = "Legacy", Description = "from before v2.32" },
            },
        });
        File.WriteAllText(path, legacy);

        var result = CacheRecovery.LoadOrRecover<ProfileCacheFile>(
            path,
            expectedSchemaVersion: GitHubProfileSource.CurrentSchemaVersion,
            deserialize: j => StjJson.Deserialize<ProfileCacheFile>(j, ProfileManager.SafeJsonOptions),
            structuralCheck: w => w.Profiles is not null);

        Assert.Equal(RecoveryReason.SchemaMissing, result.Reason);
        Assert.True(result.ShouldRebuild);
        Assert.False(File.Exists(path));
        var backups = EnumerateCorruptBackups(path);
        Assert.Single(backups);
        Assert.Contains("Legacy", File.ReadAllText(backups.Single()));
    }

    [Fact]
    public void ProfileCache_Load_OnValidV1Wrapper_ReturnsLoaded()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("profiles.json");
        var wrapper = new ProfileCacheFile
        {
            SchemaVersion = 1,
            CachedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            UpstreamUrl = "https://example.com/profiles.json",
            Profiles = new ProfileCollection
            {
                Profiles = new List<Profile>
                {
                    new() { Name = "Alpha", Description = "wrapped" },
                },
            },
        };
        File.WriteAllText(path,
            StjJson.Serialize(wrapper, ProfileManager.SafeJsonOptions));

        var result = CacheRecovery.LoadOrRecover<ProfileCacheFile>(
            path,
            expectedSchemaVersion: 1,
            deserialize: j => StjJson.Deserialize<ProfileCacheFile>(j, ProfileManager.SafeJsonOptions),
            structuralCheck: w => w.Profiles is not null);

        Assert.True(result.Loaded);
        Assert.Equal("Alpha", result.Value!.Profiles.Profiles[0].Name);
        Assert.True(File.Exists(path),
            "Valid wrapper must be left in place for the next offline load.");
    }

    [Fact]
    public void ProfileCache_Load_OnTruncatedJson_QuarantinesAndReturnsRebuild()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("profiles.json");
        File.WriteAllText(path,
            "{\"schema_version\":1,\"profiles\":{\"profiles\":[{\"name\":\"trunc");

        var result = CacheRecovery.LoadOrRecover<ProfileCacheFile>(
            path,
            expectedSchemaVersion: 1,
            deserialize: j => StjJson.Deserialize<ProfileCacheFile>(j, ProfileManager.SafeJsonOptions),
            structuralCheck: w => w.Profiles is not null);

        Assert.Equal(RecoveryReason.JsonMalformed, result.Reason);
        Assert.False(File.Exists(path));
        Assert.Single(EnumerateCorruptBackups(path));
    }

    [Fact]
    public void StateFile_LikeCache_Load_OnLegacy_QuarantinesAndReturnsRebuild()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("state.json");
        File.WriteAllText(path,
            "{\"ActiveProfile\":\"Discord_Privacy\",\"SingBoxPid\":1234," +
            "\"StartedAt\":\"2026-05-01T00:00:00\",\"ProcessNames\":[\"Discord.exe\"]}");

        var result = CacheRecovery.LoadOrRecover<RunStateLike>(
            path,
            expectedSchemaVersion: 1,
            deserialize: j => StjJson.Deserialize<RunStateLike>(j, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));

        Assert.Equal(RecoveryReason.SchemaMissing, result.Reason);
        Assert.True(result.ShouldRebuild);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void StateFile_LikeCache_Load_OnValidV1_ReturnsLoaded()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("state.json");
        File.WriteAllText(path,
            "{\"schema_version\":1,\"ActiveProfile\":\"Discord_Privacy\"," +
            "\"SingBoxPid\":1234,\"StartedAt\":\"2026-05-01T00:00:00\"," +
            "\"ProcessNames\":[\"Discord.exe\"]}");

        var result = CacheRecovery.LoadOrRecover<RunStateLike>(
            path,
            expectedSchemaVersion: 1,
            deserialize: j => StjJson.Deserialize<RunStateLike>(j, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));

        Assert.True(result.Loaded);
        Assert.Equal(1234, result.Value!.SingBoxPid);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void StateFile_LikeCache_Load_OnTruncatedJson_QuarantinesAndReturnsRebuild()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("state.json");
        File.WriteAllText(path,
            "{\"schema_version\":1,\"ActiveProfile\":\"Disc");

        var result = CacheRecovery.LoadOrRecover<RunStateLike>(
            path,
            expectedSchemaVersion: 1,
            deserialize: j => StjJson.Deserialize<RunStateLike>(j, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));

        Assert.Equal(RecoveryReason.JsonMalformed, result.Reason);
        Assert.False(File.Exists(path));
    }

    private static Serilog.ILogger NullLogger() =>
        new Serilog.LoggerConfiguration()
            .MinimumLevel.Fatal()
            .CreateLogger();

    private static List<string> EnumerateCorruptBackups(string baseFile)
    {
        var dir = Path.GetDirectoryName(baseFile);
        var name = Path.GetFileName(baseFile);
        return Directory
            .GetFiles(dir!, name + ".corrupt-*")
            .ToList();
    }

    private sealed class DummyCache
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("items")]
        public List<string>? Items { get; set; } = new();
    }

    private sealed class RunStateLike
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        public string ActiveProfile { get; set; } = string.Empty;
        public int SingBoxPid { get; set; }
        public DateTime StartedAt { get; set; }
        public List<string> ProcessNames { get; set; } = new();
    }

    private sealed class DirectoryFixture : IDisposable
    {
        public string Root { get; }

        public DirectoryFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "VPNRouter.CacheRecoveryTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string PathFor(string name) => Path.Combine(Root, name);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch {  }
        }
    }
}
