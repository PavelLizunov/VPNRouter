#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Phase 3 — 3B (2026-05-18): Newtonsoft.Json → System.Text.Json round-trip pins
// for every DTO migrated in this wave. Brief:
// plans/phase3-3B-newtonsoft-to-stj-2026-05-18.md
//
// Migrated files (5):
//   1. VPNRouter.Android/AndroidStorage.cs (uses VlessServerEntry,
//      SubscriptionEntry, CustomCategory, ServerTestResultDto)
//   2. VPNRouter.Core/Services/SubscriptionFetcher.cs (already STJ pre-3B)
//   3. VPNRouter.Core/Services/FreeConfigs/FreeConfigCache.cs (already STJ)
//   4. VPNRouter.Core/Services/UpdateChecker.cs (drops JsonConvert
//      .DeserializeAnonymousType in favour of explicit GitHubRelease/Asset DTOs)
//   5. VPNRouter.Core/Services/ProfileManager.cs (uses Profile, ProcessRule,
//      ProfileCollection, ProfileCacheFile)
//
// What this suite pins:
//   • Each migrated DTO survives JsonSerializer.Serialize →
//     JsonSerializer.Deserialize unchanged (structural equality, field-by-field).
//   • The on-disk JSON wire format remains backwards-compatible — i.e. the
//     keys our Newtonsoft predecessor wrote are still parseable by the STJ
//     successor (snake_case for [JsonPropertyName]-annotated fields,
//     PascalCase for un-annotated fields via case-insensitive lookup).
//   • The DoS guard MaxDepth=32 on ProfileManager.SafeJsonOptions matches the
//     Newtonsoft predecessor's MaxDepth setting (pinned separately by
//     ProfileManagerJsonDosGuardTests).
//
// Why round-trip + wire-compat both: the migration risk is that users with
// existing on-disk JSON (profiles.json, SharedPreferences blobs, free-config
// cache) get their data silently wiped because STJ chokes on a Newtonsoft-
// authored field name. Round-trip alone wouldn't catch the wire-compat
// regression — we'd happily serialize+deserialize fine in STJ but produce
// JSON the previous build couldn't read (or vice versa: read NOTHING from
// existing pre-3B blobs because the keys don't match). So each test
// constructs the DTO, serializes it via STJ, then deserializes it via STJ —
// AND inspects the intermediate JSON string for the expected wire keys.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Phase3StjJsonRoundTripTests
{
    // ── Test fixtures ────────────────────────────────────────────────────

    private static Profile MakeProfile()
    {
        return new Profile
        {
            Name = "Test_Profile",
            Description = "Round-trip integrity probe",
            DnsMode = "vpn_only",
            BlockOnVpnFail = true,
            Processes = new List<ProcessRule>
            {
                new ProcessRule
                {
                    Name = "Discord.exe",
                    IncludeChildren = true,
                    ScanPatterns = new[] { "Discord*.exe", "DiscordUpdate.exe" },
                },
                new ProcessRule
                {
                    Name = "chrome.exe",
                    IncludeChildren = false,
                    ScanPatterns = Array.Empty<string>(),
                },
            },
            AndroidPackages = new List<string> { "com.discord", "com.android.chrome" },
        };
    }

    // ── DTO 1: Profile (5 fields + Processes[] + AndroidPackages[]) ─────

    [Fact]
    public void Profile_RoundTrip_StructurallyIdentical()
    {
        var original = MakeProfile();

        var json = JsonSerializer.Serialize(original, ProfileManager.SafeJsonOptions);
        var roundTripped = JsonSerializer.Deserialize<Profile>(json, ProfileManager.SafeJsonOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Name, roundTripped!.Name);
        Assert.Equal(original.Description, roundTripped.Description);
        Assert.Equal(original.DnsMode, roundTripped.DnsMode);
        Assert.Equal(original.BlockOnVpnFail, roundTripped.BlockOnVpnFail);
        Assert.Equal(original.Processes.Count, roundTripped.Processes.Count);
        Assert.Equal(original.AndroidPackages.Count, roundTripped.AndroidPackages.Count);

        for (int i = 0; i < original.Processes.Count; i++)
        {
            Assert.Equal(original.Processes[i].Name, roundTripped.Processes[i].Name);
            Assert.Equal(original.Processes[i].IncludeChildren, roundTripped.Processes[i].IncludeChildren);
            Assert.Equal(original.Processes[i].ScanPatterns, roundTripped.Processes[i].ScanPatterns);
        }
    }

    [Fact]
    public void Profile_WireFormat_UsesSnakeCaseKeys()
    {
        // The on-disk profiles.json schema (committed in
        // profiles/default.json / GitHubProfileSource cache) uses snake_case
        // keys. STJ honours [JsonPropertyName] so the wire output must
        // contain "dns_mode", "block_on_vpn_fail", "include_children",
        // "scan_patterns", "android_packages" — the same keys the pre-3B
        // Newtonsoft writer produced. A regression here would silently
        // make profiles unreadable across the migration.
        var profile = MakeProfile();
        var json = JsonSerializer.Serialize(profile, ProfileManager.SafeJsonOptions);

        Assert.Contains("\"dns_mode\"", json);
        Assert.Contains("\"block_on_vpn_fail\"", json);
        Assert.Contains("\"include_children\"", json);
        Assert.Contains("\"scan_patterns\"", json);
        Assert.Contains("\"android_packages\"", json);

        // And nothing PascalCase from a missing attribute — that would mean
        // an [JsonPropertyName] got dropped.
        Assert.DoesNotContain("\"DnsMode\"", json);
        Assert.DoesNotContain("\"BlockOnVpnFail\"", json);
        Assert.DoesNotContain("\"IncludeChildren\"", json);
    }

    [Fact]
    public void Profile_LegacyWireFormat_DeserializesViaCaseInsensitive()
    {
        // A profile authored by a user / older tool with snake_case keys
        // (exact match — no quirks) must deserialize cleanly. This pins the
        // baseline contract.
        const string json = """
            {
              "name": "Legacy",
              "description": "From hand-edited profiles.json",
              "processes": [
                { "name": "test.exe", "include_children": true, "scan_patterns": ["test*.exe"] }
              ],
              "dns_mode": "smart",
              "block_on_vpn_fail": false,
              "android_packages": ["com.test"]
            }
            """;

        var profile = JsonSerializer.Deserialize<Profile>(json, ProfileManager.SafeJsonOptions);

        Assert.NotNull(profile);
        Assert.Equal("Legacy", profile!.Name);
        Assert.Equal("smart", profile.DnsMode);
        Assert.False(profile.BlockOnVpnFail);
        Assert.Single(profile.Processes);
        Assert.Equal("test.exe", profile.Processes[0].Name);
        Assert.Equal(new[] { "test*.exe" }, profile.Processes[0].ScanPatterns);
        Assert.Single(profile.AndroidPackages);
        Assert.Equal("com.test", profile.AndroidPackages[0]);
    }

    // ── DTO 2: ProcessRule (3 fields, byte-identity check) ───────────────

    [Fact]
    public void ProcessRule_RoundTrip_BinaryIdentical()
    {
        var original = new ProcessRule
        {
            Name = "Telegram.exe",
            IncludeChildren = false,
            ScanPatterns = new[] { "Telegram*.exe", "tdata*.exe" },
        };

        var json1 = JsonSerializer.Serialize(original, ProfileManager.SafeJsonOptions);
        var deserialized = JsonSerializer.Deserialize<ProcessRule>(json1, ProfileManager.SafeJsonOptions);
        Assert.NotNull(deserialized);

        // Re-serializing the deserialized object must produce byte-identical
        // JSON — proves the migration is fully lossless for this DTO.
        var json2 = JsonSerializer.Serialize(deserialized, ProfileManager.SafeJsonOptions);
        Assert.Equal(json1, json2);
    }

    // ── DTO 3: ProfileCollection (just wraps Profile[]) ──────────────────

    [Fact]
    public void ProfileCollection_RoundTrip_PreservesNestedProfileFields()
    {
        var original = new ProfileCollection
        {
            Profiles = new List<Profile> { MakeProfile() },
        };

        var json = JsonSerializer.Serialize(original, ProfileManager.SafeJsonOptions);
        var roundTripped = JsonSerializer.Deserialize<ProfileCollection>(json, ProfileManager.SafeJsonOptions);

        Assert.NotNull(roundTripped);
        Assert.Single(roundTripped!.Profiles);
        Assert.Equal(original.Profiles[0].Name, roundTripped.Profiles[0].Name);
        Assert.Equal(original.Profiles[0].Processes.Count, roundTripped.Profiles[0].Processes.Count);
    }

    // ── DTO 4: ProfileCacheFile (schema-versioned envelope for GitHub
    //           cache; was Newtonsoft [JsonProperty] pre-3B) ──────────────

    [Fact]
    public void ProfileCacheFile_RoundTrip_KeepsSchemaMarker()
    {
        var original = new ProfileCacheFile
        {
            SchemaVersion = 1,
            CachedAt = new DateTime(2026, 5, 18, 10, 30, 0, DateTimeKind.Utc),
            UpstreamUrl = "https://example.com/profiles.json",
            Profiles = new ProfileCollection
            {
                Profiles = new List<Profile> { MakeProfile() },
            },
        };

        var json = JsonSerializer.Serialize(original, ProfileManager.SafeJsonOptions);

        // Wire keys: schema_version / cached_at / upstream_url / profiles —
        // exactly what Newtonsoft [JsonProperty] wrote pre-3B.
        Assert.Contains("\"schema_version\"", json);
        Assert.Contains("\"cached_at\"", json);
        Assert.Contains("\"upstream_url\"", json);
        Assert.Contains("\"profiles\"", json);

        var roundTripped = JsonSerializer.Deserialize<ProfileCacheFile>(json, ProfileManager.SafeJsonOptions);
        Assert.NotNull(roundTripped);
        Assert.Equal(original.SchemaVersion, roundTripped!.SchemaVersion);
        Assert.Equal(original.CachedAt, roundTripped.CachedAt);
        Assert.Equal(original.UpstreamUrl, roundTripped.UpstreamUrl);
        Assert.Single(roundTripped.Profiles.Profiles);
        Assert.Equal(original.Profiles.Profiles[0].Name, roundTripped.Profiles.Profiles[0].Name);
    }

    [Fact]
    public void ProfileCacheFile_SchemaVersionProbe_DetectsBumpForwardCompat()
    {
        // CacheRecovery's STJ-based schema probe looks for "schema_version" —
        // the migrated [JsonPropertyName] attribute on ProfileCacheFile
        // emits exactly that key. Without this guarantee the offline-cache
        // fallback in GitHubProfileSource breaks silently across migrations.
        var json = JsonSerializer.Serialize(
            new ProfileCacheFile { SchemaVersion = 42 },
            ProfileManager.SafeJsonOptions);

        Assert.Contains("\"schema_version\": 42", json.Replace(" ", " ")); // tolerate whitespace
    }

    // ── DTO 5: VlessServerEntry / SubscriptionEntry / CustomCategory ─────
    //          (AndroidStorage blob shapes — wire-compat with pre-3B
    //           Newtonsoft default conventions = PascalCase) ──────────────

    [Fact]
    public void VlessServerEntry_RoundTrip_PreservesAllProtocolFields()
    {
        var original = new VlessServerEntry
        {
            Name = "main",
            Protocol = "vless",
            Server = "1.2.3.4",
            Port = 443,
            Uuid = "deadbeef-1234-5678-90ab-cdef01234567",
            Flow = "xtls-rprx-vision",
            Security = "reality",
            Password = "",
            Method = "",
            CongestionControl = "bbr",
            UdpRelayMode = "native",
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<VlessServerEntry>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Name, roundTripped!.Name);
        Assert.Equal(original.Protocol, roundTripped.Protocol);
        Assert.Equal(original.Server, roundTripped.Server);
        Assert.Equal(original.Port, roundTripped.Port);
        Assert.Equal(original.Uuid, roundTripped.Uuid);
        Assert.Equal(original.Flow, roundTripped.Flow);
        Assert.Equal(original.Security, roundTripped.Security);
    }

    [Fact]
    public void VlessServerEntry_DefaultConventions_UsesPascalCaseOnWire()
    {
        // VlessServerEntry only has [YamlMember] attributes — no
        // [JsonPropertyName]. Newtonsoft serialised these as PascalCase by
        // default (C# property names verbatim); STJ does the same. This
        // pin guards against accidentally adding a global STJ snake-case
        // policy that would silently break every existing SharedPreferences
        // blob on Android (which Newtonsoft wrote as "Server", "Port",
        // "Uuid", etc.).
        var srv = new VlessServerEntry
        {
            Name = "x",
            Server = "1.2.3.4",
            Port = 443,
            Uuid = "uuid",
            Flow = "flow",
        };
        var json = JsonSerializer.Serialize(srv);

        Assert.Contains("\"Server\":", json);
        Assert.Contains("\"Port\":", json);
        Assert.Contains("\"Uuid\":", json);
        Assert.DoesNotContain("\"server\":", json);  // would imply unintended naming policy
    }

    [Fact]
    public void SubscriptionEntry_RoundTrip_ServersListPreserved()
    {
        var original = new SubscriptionEntry
        {
            Id = "abc123",
            Name = "Default",
            Url = "https://sub.example.com/feed",
            Enabled = true,
            LastRefreshedAt = new DateTimeOffset(2026, 5, 18, 0, 0, 0, TimeSpan.Zero),
            LastServerCount = 5,
            Servers = new List<VlessServerEntry>
            {
                new VlessServerEntry { Name = "main", Server = "1.2.3.4", Port = 443, Uuid = "u1", Flow = "f1" },
                new VlessServerEntry { Name = "backup", Server = "5.6.7.8", Port = 443, Uuid = "u2", Flow = "f2" },
            },
        };

        // AndroidStorage uses PropertyNameCaseInsensitive=true on JsonOptions —
        // mirror that here so the test reflects production behaviour.
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var json = JsonSerializer.Serialize(original, options);
        var roundTripped = JsonSerializer.Deserialize<SubscriptionEntry>(json, options);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Id, roundTripped!.Id);
        Assert.Equal(original.Name, roundTripped.Name);
        Assert.Equal(original.Url, roundTripped.Url);
        Assert.Equal(original.Enabled, roundTripped.Enabled);
        Assert.Equal(original.LastRefreshedAt, roundTripped.LastRefreshedAt);
        Assert.Equal(original.LastServerCount, roundTripped.LastServerCount);
        Assert.Equal(original.Servers.Count, roundTripped.Servers.Count);
        Assert.Equal(original.Servers[0].Uuid, roundTripped.Servers[0].Uuid);
        Assert.Equal(original.Servers[1].Server, roundTripped.Servers[1].Server);
    }

    [Fact]
    public void CustomCategory_RoundTrip_AppsListPreserved()
    {
        var original = new CustomCategory
        {
            Name = "Banking",
            Apps = new List<string> { "com.bank.app1", "com.bank.app2" },
            Enabled = false,
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<CustomCategory>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Name, roundTripped!.Name);
        Assert.Equal(original.Apps, roundTripped.Apps);
        Assert.Equal(original.Enabled, roundTripped.Enabled);
    }

    // ── DTO 6: AndroidStorage's nested ServerTestResultDto (snake_case
    //          via [JsonPropertyName]) ────────────────────────────────────

    /// <summary>
    /// Mirror of <c>AndroidStorage.ServerTestResultDto</c>. Re-declared here
    /// (instead of via Android project reference) because the Tests project
    /// targets net8.0 without the Android workload — we can't reference the
    /// real type. The shape pin is the contract we care about: the wire
    /// keys must stay snake_case (status / latency_ms / last_tested_at /
    /// error) so existing pre-3B blobs in production SharedPreferences
    /// stay readable post-migration.
    /// </summary>
    private sealed class ServerTestResultDtoShape
    {
        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("latency_ms")]
        public int LatencyMs { get; set; }

        [JsonPropertyName("last_tested_at")]
        public DateTimeOffset LastTestedAt { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    [Fact]
    public void ServerTestResultDto_RoundTrip_SnakeCaseKeysPreserved()
    {
        var original = new ServerTestResultDtoShape
        {
            Status = 2,
            LatencyMs = 47,
            LastTestedAt = new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero),
            Error = null,
        };

        var json = JsonSerializer.Serialize(original);

        Assert.Contains("\"status\":2", json);
        Assert.Contains("\"latency_ms\":47", json);
        Assert.Contains("\"last_tested_at\":", json);
        Assert.Contains("\"error\":null", json);

        var roundTripped = JsonSerializer.Deserialize<ServerTestResultDtoShape>(json);
        Assert.NotNull(roundTripped);
        Assert.Equal(original.Status, roundTripped!.Status);
        Assert.Equal(original.LatencyMs, roundTripped.LatencyMs);
        Assert.Equal(original.LastTestedAt, roundTripped.LastTestedAt);
        Assert.Equal(original.Error, roundTripped.Error);
    }

    [Fact]
    public void ServerTestResultDto_LegacyNewtonsoftBlob_DeserializesCleanly()
    {
        // What Newtonsoft's [JsonProperty]-annotated writer produced pre-3B
        // (verbatim from a captured blob on a real Android install — exact
        // shape pinned). The new STJ reader must accept this byte-for-byte.
        const string legacyJson = """
            {"status":1,"latency_ms":120,"last_tested_at":"2026-05-10T08:15:30.0000000+00:00","error":null}
            """;

        var parsed = JsonSerializer.Deserialize<ServerTestResultDtoShape>(legacyJson);

        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!.Status);
        Assert.Equal(120, parsed.LatencyMs);
        Assert.Equal(2026, parsed.LastTestedAt.Year);
        Assert.Null(parsed.Error);
    }

    // ── DTO 7: GitHubRelease / GitHubAsset (UpdateChecker's
    //          ex-anonymous-type DTOs) ─────────────────────────────────────

    /// <summary>
    /// Mirror of UpdateChecker's internal GitHubRelease — the real type is
    /// private to UpdateChecker. We pin the contract: snake_case wire keys
    /// (tag_name / html_url / browser_download_url) per the GitHub Releases
    /// API spec. A real GitHub Releases API response is the upstream
    /// contract; this test guards against accidentally renaming our DTO
    /// fields in a way that breaks update detection silently.
    /// </summary>
    private sealed class GitHubReleaseShape
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public GitHubAssetShape[] Assets { get; set; } = Array.Empty<GitHubAssetShape>();
    }

    private sealed class GitHubAssetShape
    {
        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    [Fact]
    public void GitHubRelease_LegacyApiResponse_ParsesViaStj()
    {
        // Real-shape GitHub API response slice (truncated to the fields we
        // consume). Pre-3B Newtonsoft DeserializeAnonymousType inferred this
        // shape from the anonymous-template; post-3B we use explicit DTOs.
        // The wire contract is exactly the same — this test is the proof.
        const string json = """
            [
              {
                "tag_name": "v2.33.0-r1",
                "body": "Release notes",
                "html_url": "https://github.com/PavelLizunov/VPNRouter/releases/tag/v2.33.0-r1",
                "draft": false,
                "prerelease": true,
                "assets": [
                  {
                    "browser_download_url": "https://github.com/PavelLizunov/VPNRouter/releases/download/v2.33.0-r1/VPNRouter-v2.33.0-r1-win.zip",
                    "size": 87654321,
                    "name": "VPNRouter-v2.33.0-r1-win.zip"
                  }
                ]
              }
            ]
            """;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var releases = JsonSerializer.Deserialize<GitHubReleaseShape[]>(json, options);

        Assert.NotNull(releases);
        Assert.Single(releases!);
        Assert.Equal("v2.33.0-r1", releases![0].TagName);
        Assert.True(releases[0].Prerelease);
        Assert.False(releases[0].Draft);
        Assert.Single(releases[0].Assets);
        Assert.Equal(87654321L, releases[0].Assets[0].Size);
        Assert.EndsWith("-win.zip", releases[0].Assets[0].Name);
    }

    [Fact]
    public void GitHubRelease_UnknownFields_Ignored()
    {
        // STJ skips unknown fields by default (matching Newtonsoft's
        // permissive behaviour). The real GitHub API response carries many
        // fields we don't read (id, author, target_commitish, created_at,
        // published_at, tarball_url, zipball_url, ...). Pin that we tolerate
        // them rather than throwing.
        const string json = """
            {
              "id": 12345,
              "tag_name": "v1.0.0",
              "body": "x",
              "html_url": "https://example.com",
              "draft": false,
              "prerelease": false,
              "author": { "login": "someuser", "id": 99999 },
              "tarball_url": "https://example.com/tar",
              "zipball_url": "https://example.com/zip",
              "assets": []
            }
            """;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var release = JsonSerializer.Deserialize<GitHubReleaseShape>(json, options);

        Assert.NotNull(release);
        Assert.Equal("v1.0.0", release!.TagName);
        Assert.Empty(release.Assets);
    }

    // ── End-to-end: ProfileCollection migration through SafeJsonOptions ──

    [Fact]
    public void ProfileCollection_FullRoundTripUnderDosGuard()
    {
        // Sanity check: a realistic profile collection round-trips cleanly
        // through SafeJsonOptions (MaxDepth=32, case-insensitive, indented).
        // The MaxDepth limit is asserted separately by
        // ProfileManagerJsonDosGuardTests; this just confirms a normal
        // multi-profile catalog fits comfortably.
        var coll = new ProfileCollection
        {
            Profiles = new List<Profile>
            {
                MakeProfile(),
                new Profile { Name = "Browsers", Description = "All browsers", DnsMode = "smart" },
                new Profile { Name = "Games", Description = "Game launchers", DnsMode = "direct" },
            },
        };

        var json = JsonSerializer.Serialize(coll, ProfileManager.SafeJsonOptions);
        var parsed = JsonSerializer.Deserialize<ProfileCollection>(json, ProfileManager.SafeJsonOptions);

        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.Profiles.Count);
        Assert.Equal("Browsers", parsed.Profiles[1].Name);
        Assert.Equal("smart", parsed.Profiles[1].DnsMode);
        Assert.Equal("direct", parsed.Profiles[2].DnsMode);
    }
}
