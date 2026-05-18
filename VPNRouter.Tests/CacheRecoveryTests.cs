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

/// <summary>
/// v2.32.0 — pin <see cref="CacheRecovery"/> across all three on-disk JSON
/// caches (free_configs.json, profiles.json, state.json). Goal: a corrupt
/// or schema-mismatched cache file must never silently break a feature —
/// it must always be quarantined (renamed to <c>.corrupt-{ts}</c> for post-
/// mortem) and the in-memory load must surface a typed
/// <see cref="RecoveryReason"/> that callers can branch on.
///
/// <para>Tests use temp directories so each one is hermetic — no
/// %ProgramData% pollution. The <see cref="DirectoryFixture"/> helper
/// auto-cleans on dispose so a failing run leaves the host clean.</para>
/// </summary>
public sealed class CacheRecoveryTests
{
    // ─── direct CacheRecovery API tests ──────────────────────────────────────

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
        // Round-trip must not have written any sibling .corrupt files.
        Assert.Empty(EnumerateCorruptBackups(path));
    }

    [Fact]
    public void LoadOrRecover_LegacyWithoutSchemaVersion_QuarantinesAndReturnsSchemaMissing()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("legacy.json");
        // Pre-v2.32.0 cache shape: no schema_version field at all.
        File.WriteAllText(path, "{\"name\":\"legacy\",\"items\":[\"x\"]}");

        var result = CacheRecovery.LoadOrRecover<DummyCache>(
            path,
            expectedSchemaVersion: 1,
            deserialize: j => StjJson.Deserialize<DummyCache>(j));

        Assert.Equal(RecoveryReason.SchemaMissing, result.Reason);
        Assert.Null(result.Value);
        Assert.True(result.ShouldRebuild);
        // Original file moved out of the way so the next save can take it.
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
        // Mid-byte truncation — real-world disk-full / power-cut shape.
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

        // Caller expects v2 — v1 must be quarantined.
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
        // schema_version OK, JSON OK, but Items is null (mandatory list).
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
        // Forward-compat semantic: a future schema version (>= expected)
        // is accepted on the assumption new fields are additive. The
        // contract from the design doc is "< expected → wipe", not "!=".
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
        // Probe sees schema_version=1 fine, but the caller's deserialiser
        // throws on the typed payload (e.g. a custom converter rejects
        // an enum value). We simulate by handing a deserialiser that
        // always throws after the probe stage.
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
        // If two corrupt loads happen in the same UTC second the
        // second quarantine must not clobber the first — uniquify with
        // a numeric suffix.
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

    // ─── FreeConfigCache integration tests ───────────────────────────────────

    [Fact]
    public void FreeConfigCache_Load_OnLegacyFile_QuarantinesAndReturnsEmpty()
    {
        // Pre-v2.32.0 free_configs.json had no schema_version. On first
        // launch post-upgrade we must wipe + back up, so the next refresh
        // rebuilds from network rather than feeding stale-format data
        // into the verifier.
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
        // Happy path: write fresh file, read it back. v2.31.3 sub-5ms
        // healing must continue to fire so the heal-old regression
        // test from FreeConfigCacheMigrationTests doesn't backslide.
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
                    LatencyMs = 3, // sub-5ms — heal must reset on load
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
        // v2.31.3-r1 heal-old preserved alongside CacheRecovery
        Assert.Equal(0, roundTrip.Configs[0].LatencyMs);
        Assert.Equal(42, roundTrip.Configs[1].LatencyMs);
    }

    [Fact]
    public void FreeConfigCache_Load_OnTruncatedJson_QuarantinesAndReturnsEmpty()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("free_configs.json");
        // Cut mid-byte to simulate disk-full / power-cut on save.
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
        // Explicit schema_version=0 — pre-v1, should wipe.
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

        // Caller forgot to set SchemaVersion — Save() must stamp it.
        var file = new FreeConfigCache.CacheFile { SchemaVersion = 0 };
        cache.Save(file);

        var raw = File.ReadAllText(path);
        Assert.Contains("\"schema_version\":1", raw);
    }

    // ─── ProfileCacheFile integration tests ──────────────────────────────────

    [Fact]
    public void ProfileCache_Load_OnLegacyRawProfileCollection_QuarantinesAndReturnsRebuild()
    {
        // Pre-v2.32.0 cache/profiles.json was the raw upstream
        // ProfileCollection JSON — no schema_version, no wrapper. On
        // first read post-upgrade CacheRecovery must wipe it so we
        // never feed legacy-shape JSON into the typed wrapper deserialiser.
        //
        // Phase 3B (2026-05-18) — migrated from Newtonsoft to STJ via
        // ProfileManager.SafeJsonOptions. The legacy on-disk format that
        // GitHubProfileSource pre-v2.32 wrote was Newtonsoft default
        // PascalCase; we emulate that here by serialising with a non-
        // snake-case STJ option (no PropertyNameCaseInsensitive, no
        // [JsonPropertyName] effect on the legacy-bytes side). The
        // schema-missing branch still trips because the bytes have no
        // "schema_version" key.
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
        // Backup must preserve the legacy bytes verbatim — operator
        // can copy it to a recovery dir if they want the old profile.
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
        // Phase 3B (2026-05-18) — STJ serialization (matches the
        // production GitHubProfileSource.LoadAsync write path which now
        // also uses JsonSerializer.Serialize with WriteIndented=true on
        // ProfileManager.SafeJsonOptions).
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
            // Phase 3B (2026-05-18) — STJ deserialization.
            deserialize: j => StjJson.Deserialize<ProfileCacheFile>(j, ProfileManager.SafeJsonOptions),
            structuralCheck: w => w.Profiles is not null);

        Assert.Equal(RecoveryReason.JsonMalformed, result.Reason);
        Assert.False(File.Exists(path));
        Assert.Single(EnumerateCorruptBackups(path));
    }

    // ─── state.json integration tests ────────────────────────────────────────
    //
    // Note: the production StateFile uses a hard-coded %ProgramData% path so
    // we can't easily redirect it in tests. We pin the schema_version
    // contract by exercising CacheRecovery directly against a temp file
    // shaped like state.json — which is exactly what StateFile.Read does
    // under the hood.

    [Fact]
    public void StateFile_LikeCache_Load_OnLegacy_QuarantinesAndReturnsRebuild()
    {
        using var dir = new DirectoryFixture();
        var path = dir.PathFor("state.json");
        // Pre-v2.32.0 state.json — no schema_version field.
        File.WriteAllText(path,
            "{\"ActiveProfile\":\"Discord_Privacy\",\"SingBoxPid\":1234," +
            "\"StartedAt\":\"2026-05-01T00:00:00\",\"ProcessNames\":[\"Discord.exe\"]}");

        // Use a struct mirroring RunState so we don't pull in CLI namespace.
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

    // ─── helpers ─────────────────────────────────────────────────────────────

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

    /// <summary>Probe DTO used by the API-level tests.</summary>
    private sealed class DummyCache
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("items")]
        public List<string>? Items { get; set; } = new();
    }

    /// <summary>
    /// STJ-shaped mirror of <c>RunState</c>. Phase 4 (2026-05-18) — migrated
    /// from Newtonsoft <c>[JsonProperty]</c> to System.Text.Json
    /// <c>[JsonPropertyName]</c>. The wire field <c>schema_version</c> stays
    /// snake_case (CacheRecovery's probe is unchanged); the other un-attributed
    /// fields keep their PascalCase wire keys (matches the pre-Phase-4
    /// Newtonsoft default).
    /// </summary>
    private sealed class RunStateLike
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        public string ActiveProfile { get; set; } = string.Empty;
        public int SingBoxPid { get; set; }
        public DateTime StartedAt { get; set; }
        public List<string> ProcessNames { get; set; } = new();
    }

    /// <summary>
    /// Hermetic temp-dir helper so each test owns its own filesystem
    /// scratch space. Auto-cleans on Dispose; failures during cleanup
    /// are swallowed (CI may hold transient locks on a logger flush).
    /// </summary>
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
            catch { /* best-effort cleanup */ }
        }
    }
}
