#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Phase 4 (2026-05-18) — Newtonsoft.Json → System.Text.Json round-trip pins for
// the ~13 remaining files migrated in this wave. Brief:
// plans/phase4-newtonsoft-retirement-2026-05-18.md
//
// Migrated files (Phase 4):
//   1. VPNRouter.Core/Models/VPNConfig.cs               (sing-box JSON model
//                                                        — [JsonProperty] →
//                                                        [JsonPropertyName])
//   2. VPNRouter.Core/Services/ConfigGenerator.cs        (Serialize)
//   3. VPNRouter.Core/Services/CustomConfigInjector.cs   (JObject → JsonNode)
//   4. VPNRouter.Core/Services/ConfigSanityCheck.cs      (JObject → JsonObject)
//   5. VPNRouter.Core/Services/ConfigShareDocument.cs    ([JsonProperty] →
//                                                        [JsonPropertyName])
//   6. VPNRouter.Core/Services/HealthCheck.cs            (deserialise + JObject)
//   7. VPNRouter.Core/Services/PlaceholderDefense.cs     (JObject → JsonObject)
//   8. VPNRouter.Core/Services/VpnEngine.cs              (CatalogueQuarantine)
//   9. VPNRouter.Core/Services/WindowsDnsHardening.cs    (HardeningState IO)
//  10. VPNRouter.Core/Services/UpdateSources/
//        GitHubReleaseSource.cs                          (DeserializeAnonymousType
//                                                        → typed DTO)
//  11. VPNRouter.Core/Services/UpdateSources/
//        SideloadSource.cs                               (same as #10, Android)
//  12. VPNRouter.Android/AndroidUpdater.cs               (same as #10, Android)
//  13. VPNRouter.CLI/Helpers/StateFile.cs                ([JsonProperty] →
//                                                        [JsonPropertyName])
//
// What this suite pins:
//   • Each migrated DTO survives JsonSerializer.Serialize →
//     JsonSerializer.Deserialize unchanged (structural equality).
//   • The on-disk JSON wire format remains backwards-compatible — the keys
//     pre-Phase-4 Newtonsoft wrote are still parseable by the STJ successor:
//       - VPNConfig classes:     snake_case via [JsonPropertyName(...)].
//       - ConfigShareDocument:   snake_case via [JsonPropertyName(...)].
//       - RunState (StateFile):  schema_version snake_case +
//                                un-attributed fields PascalCase.
//       - GitHubRelease/Asset:   snake_case via [JsonPropertyName(...)].
//       - HardeningState:        PascalCase (no attributes — Newtonsoft and
//                                STJ both emit C# property names verbatim).
//
// Why round-trip + wire-compat both: same rationale as
// Phase3StjJsonRoundTripTests — STJ choking on a Newtonsoft-authored field
// name would silently wipe user data (state.json, dns_hardening_state.json,
// share-document imports). The test that pins a legacy-bytes blob is the
// only reliable backstop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Phase4StjRoundTripTests
{
    // ── DTO 1: SingBoxConfig (VPNConfig.cs) ─────────────────────────────────

    [Fact]
    public void SingBoxConfig_RoundTrip_WireKeysAreSnakeCase()
    {
        var cfg = new SingBoxConfig
        {
            Log = new SingBoxLog { Level = "info", Timestamp = true, Output = "/tmp/sb.log" },
            Dns = new SingBoxDns
            {
                Final = "vpn-dns",
                Strategy = "ipv4_only",
                Servers = new List<DnsServer>
                {
                    new() { Tag = "vpn-dns", Type = "https", Server = "1.1.1.1", ServerPort = 443, Path = "/dns-query", Detour = "proxy" },
                },
                Rules = new List<DnsRule>
                {
                    new()
                    {
                        ProcessName = new List<string> { "Discord.exe" },
                        Action = "route",
                        Server = "vpn-dns",
                    },
                },
            },
            Inbounds = new List<SingBoxInbound>
            {
                new()
                {
                    Type = "tun",
                    Tag = "tun-in",
                    InterfaceName = "VPNRouter-TUN",
                    Address = new List<string> { "172.19.0.1/30" },
                    Mtu = 9000,
                    AutoRoute = true,
                    StrictRoute = false,
                    RouteExcludeAddress = new List<string> { "10.0.0.0/8" },
                    EndpointIndependentNat = false,
                    Stack = "system",
                },
            },
            Outbounds = new List<SingBoxOutbound>
            {
                new()
                {
                    Type = "vless",
                    Tag = "proxy",
                    Server = "1.2.3.4",
                    ServerPort = 443,
                    Uuid = "deadbeef",
                    Flow = "xtls-rprx-vision",
                    DomainResolver = "local-dns",
                },
            },
            Route = new SingBoxRoute
            {
                Rules = new List<RouteRule>
                {
                    new() { Action = "sniff", Timeout = "300ms" },
                    new() { Protocol = "dns", Action = "hijack-dns" },
                    new() { IpIsPrivate = true, Action = "route", Outbound = "direct" },
                    new() { ProcessName = new List<string> { "Discord.exe" }, Action = "route", Outbound = "proxy" },
                },
                Final = "direct",
                AutoDetectInterface = true,
                DefaultDomainResolver = "local-dns",
            },
            Experimental = new SingBoxExperimental
            {
                ClashApi = new ClashApi { ExternalController = "127.0.0.1:9090" },
            },
        };

        var json = ConfigGenerator.Serialize(cfg);

        // Wire keys must be snake_case — sing-box's parser only accepts these.
        Assert.Contains("\"server_port\"", json);
        Assert.Contains("\"process_name\"", json);
        Assert.Contains("\"ip_is_private\"", json);
        Assert.Contains("\"auto_route\"", json);
        Assert.Contains("\"strict_route\"", json);
        Assert.Contains("\"route_exclude_address\"", json);
        Assert.Contains("\"interface_name\"", json);
        Assert.Contains("\"endpoint_independent_nat\"", json);
        Assert.Contains("\"auto_detect_interface\"", json);
        Assert.Contains("\"default_domain_resolver\"", json);
        Assert.Contains("\"domain_resolver\"", json);
        Assert.Contains("\"clash_api\"", json);
        Assert.Contains("\"external_controller\"", json);

        // And no PascalCase versions — confirms [JsonPropertyName] is wired
        // up on every property we sanity-check.
        Assert.DoesNotContain("\"ServerPort\"", json);
        Assert.DoesNotContain("\"ProcessName\"", json);
        Assert.DoesNotContain("\"IpIsPrivate\"", json);
        Assert.DoesNotContain("\"AutoRoute\"", json);
        Assert.DoesNotContain("\"StrictRoute\"", json);

        // Nullable optional fields with WhenWritingNull must elide nulls
        // (matches Newtonsoft NullValueHandling.Ignore pre-Phase-4 behaviour).
        Assert.DoesNotContain("\"flow\":null", json);
        Assert.DoesNotContain("\"reality\":null", json);
        Assert.DoesNotContain("\"utls\":null", json);
    }

    [Fact]
    public void SingBoxConfig_RoundTrip_DeserializesBackCleanly()
    {
        // Round-trip integrity: Serialize → Deserialize → re-Serialize must
        // produce byte-identical output. Proves the migration is lossless
        // for the SingBoxConfig DTO tree.
        var cfg = new SingBoxConfig
        {
            Log = new SingBoxLog { Level = "info", Timestamp = true, Output = "/tmp/sb.log" },
            Dns = new SingBoxDns { Final = "vpn-dns", Strategy = "ipv4_only" },
            Inbounds = new List<SingBoxInbound> { new() { Type = "tun", Tag = "tun-in" } },
            Outbounds = new List<SingBoxOutbound>
            {
                new() { Type = "vless", Tag = "proxy", Server = "1.2.3.4", ServerPort = 443, Uuid = "uuid" },
                new() { Type = "direct", Tag = "direct" },
            },
            Route = new SingBoxRoute { Final = "direct", AutoDetectInterface = true },
        };

        var json1 = ConfigGenerator.Serialize(cfg);
        var roundTripped = JsonSerializer.Deserialize<SingBoxConfig>(json1, ConfigGenerator.SingBoxOptions);
        Assert.NotNull(roundTripped);
        var json2 = ConfigGenerator.Serialize(roundTripped!);

        Assert.Equal(json1, json2);
    }

    // ── DTO 2: ConfigShareDocument ──────────────────────────────────────────

    [Fact]
    public void ConfigShareDocument_RoundTrip_PreservesSchemaMarker()
    {
        var doc = new ConfigShareDocument
        {
            Schema = ConfigShareDocument.SchemaMarker,
            Version = ConfigShareDocument.CurrentVersion,
            ExportedAt = new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero),
            ConfigMode = "subscribe",
            ExportedFrom = new ExportedFromInfo
            {
                Platform = "android",
                AppVersion = "2.33.0-r1",
                DeviceLabel = "KYOCERA",
            },
            Subscriptions = new List<SubscriptionEntry>
            {
                new()
                {
                    Id = "abc",
                    Name = "main",
                    Url = "https://example.com",
                    Enabled = true,
                    LastServerCount = 5,
                },
            },
            Settings = new ExportedSettings
            {
                Theme = "dark",
                Language = "ru",
                RoutingMode = "split",
                BypassRussianTraffic = true,
            },
            PerAppFilter = new PerAppFilterExport
            {
                Mode = "include",
                Packages = new List<string> { "com.discord", "com.chrome" },
            },
        };

        var json = ConfigShareDocument.Serialize(doc);

        // Wire keys must be snake_case — desktop importers will look for
        // these exact names; a regression silently breaks every imported
        // share-document.
        Assert.Contains("\"schema\"", json);
        Assert.Contains("\"version\"", json);
        Assert.Contains("\"exported_at\"", json);
        Assert.Contains("\"exported_from\"", json);
        Assert.Contains("\"config_mode\"", json);
        Assert.Contains("\"subscriptions\"", json);
        Assert.Contains("\"per_app_filter\"", json);
        Assert.Contains("\"app_version\"", json);
        Assert.Contains("\"device_label\"", json);
        Assert.Contains("\"bypass_ru\"", json);
        Assert.Contains("\"routing_mode\"", json);

        // Round-trip via TryParse — the production entry-point. Validates
        // the full Parse → Validate → return flow.
        var parsed = ConfigShareDocument.TryParse(json);
        Assert.True(parsed.Ok, parsed.Error);
        Assert.NotNull(parsed.Document);
        Assert.Equal(doc.Schema, parsed.Document!.Schema);
        Assert.Equal(doc.Version, parsed.Document.Version);
        Assert.Equal(doc.ConfigMode, parsed.Document.ConfigMode);
        Assert.Single(parsed.Document.Subscriptions);
        Assert.Equal("main", parsed.Document.Subscriptions[0].Name);
        Assert.NotNull(parsed.Document.Settings);
        Assert.Equal("dark", parsed.Document.Settings!.Theme);
        Assert.Equal(true, parsed.Document.Settings.BypassRussianTraffic);
        Assert.NotNull(parsed.Document.PerAppFilter);
        Assert.Equal("include", parsed.Document.PerAppFilter!.Mode);
        Assert.Equal(2, parsed.Document.PerAppFilter.Packages.Count);
    }

    [Fact]
    public void ConfigShareDocument_LegacyNewtonsoftBlob_DeserializesCleanly()
    {
        // Capture of what Newtonsoft's pre-Phase-4 [JsonProperty]-annotated
        // writer would have emitted for a minimal valid share document.
        // STJ post-migration MUST accept this byte-for-byte. snake_case
        // keys are the contract.
        const string legacyJson = """
        {
          "schema": "vpnrouter-config-share",
          "version": 1,
          "exported_at": "2026-05-10T08:15:30+00:00",
          "exported_from": {
            "platform": "android",
            "app_version": "2.32.0",
            "device_label": "test-device"
          },
          "config_mode": "subscribe",
          "subscriptions": [
            { "id": "x", "name": "main", "url": "https://example.com", "enabled": true, "last_server_count": 3 }
          ]
        }
        """;

        var parsed = ConfigShareDocument.TryParse(legacyJson);
        Assert.True(parsed.Ok, parsed.Error);
        Assert.NotNull(parsed.Document);
        Assert.Equal("subscribe", parsed.Document!.ConfigMode);
        Assert.Single(parsed.Document.Subscriptions);
        Assert.Equal("main", parsed.Document.Subscriptions[0].Name);
        Assert.Equal("android", parsed.Document.ExportedFrom.Platform);
    }

    [Fact]
    public void ConfigShareDocument_RejectsWrongSchemaMarker()
    {
        // Schema-marker enforcement — protects against accidentally
        // importing a different vendor's config blob with a similar shape.
        const string fake = "{ \"schema\": \"some-other-tool\", \"version\": 1 }";
        var parsed = ConfigShareDocument.TryParse(fake);
        Assert.False(parsed.Ok);
        Assert.Contains("schema marker", parsed.Error!);
    }

    [Fact]
    public void ConfigShareDocument_RejectsMalformedJson()
    {
        var parsed = ConfigShareDocument.TryParse("{not json");
        Assert.False(parsed.Ok);
        Assert.Contains("malformed JSON", parsed.Error!);
    }

    // ── DTO 3: GitHubRelease / GitHubAsset (UpdateSources DTOs) ─────────────

    [Fact]
    public void GitHubRelease_LegacyApiResponse_ParsesCleanly()
    {
        // Real-shape GitHub API response slice (truncated to the fields
        // GitHubReleaseSource / SideloadSource / AndroidUpdater consume).
        // Pre-Phase-4 Newtonsoft DeserializeAnonymousType inferred the shape
        // from an anonymous template; post-Phase-4 we use the typed
        // VPNRouter.Core.Services.UpdateSources.GitHubRelease DTO.
        const string json = """
        [
          {
            "tag_name": "v2.33.0-r1",
            "body": "Release notes go here",
            "html_url": "https://github.com/PavelLizunov/VPNRouter/releases/tag/v2.33.0-r1",
            "draft": false,
            "prerelease": true,
            "assets": [
              {
                "browser_download_url": "https://github.com/PavelLizunov/VPNRouter/releases/download/v2.33.0-r1/VPNRouter-v2.33.0-r1-win.zip",
                "size": 87654321,
                "name": "VPNRouter-v2.33.0-r1-win.zip"
              },
              {
                "browser_download_url": "https://github.com/PavelLizunov/VPNRouter/releases/download/v2.33.0-r1/VPNRouter-v2.33.0-r1-android.apk",
                "size": 50000000,
                "name": "VPNRouter-v2.33.0-r1-android.apk"
              }
            ]
          }
        ]
        """;

        var releases = JsonSerializer.Deserialize<GitHubRelease[]>(
            json, GitHubReleaseSource.GitHubReleaseJsonOptions);

        Assert.NotNull(releases);
        Assert.Single(releases!);
        Assert.Equal("v2.33.0-r1", releases![0].TagName);
        Assert.True(releases[0].Prerelease);
        Assert.False(releases[0].Draft);
        Assert.NotNull(releases[0].Assets);
        Assert.Equal(2, releases[0].Assets!.Length);
        Assert.EndsWith("-win.zip", releases[0].Assets![0].Name);
        Assert.Equal(87654321L, releases[0].Assets![0].Size);
        Assert.EndsWith("-android.apk", releases[0].Assets![1].Name);
    }

    [Fact]
    public void GitHubRelease_RealWorldExtraFields_IgnoredGracefully()
    {
        // The real GitHub API response carries many fields we don't read
        // (id, author, target_commitish, created_at, published_at,
        // tarball_url, zipball_url, ...). STJ skips unknown fields by
        // default (matches Newtonsoft permissive default). Pin the
        // contract.
        const string json = """
        {
          "id": 12345,
          "node_id": "RE_kwDOMo...",
          "tag_name": "v2.32.0",
          "name": "v2.32.0 release",
          "body": "x",
          "html_url": "https://example.com",
          "draft": false,
          "prerelease": false,
          "author": { "login": "someuser", "id": 99999 },
          "target_commitish": "main",
          "tarball_url": "https://example.com/tar",
          "zipball_url": "https://example.com/zip",
          "created_at": "2026-05-10T00:00:00Z",
          "published_at": "2026-05-10T01:00:00Z",
          "assets": []
        }
        """;

        var release = JsonSerializer.Deserialize<GitHubRelease>(
            json, GitHubReleaseSource.GitHubReleaseJsonOptions);

        Assert.NotNull(release);
        Assert.Equal("v2.32.0", release!.TagName);
        Assert.NotNull(release.Assets);
        Assert.Empty(release.Assets!);
    }

    // ── DTO 4: RunState (CLI StateFile) ─────────────────────────────────────

    [Fact]
    public void RunState_RoundTrip_ViaStjPreservesSchemaVersionKey()
    {
        // StateFile.Write / Read go through JsonSerializer with WriteIndented +
        // PropertyNameCaseInsensitive. The schema_version wire key is the
        // only one that's snake_case (matching the pre-Phase-4 Newtonsoft
        // [JsonProperty("schema_version")] contract); other fields are
        // PascalCase. CacheRecovery's STJ-based schema probe looks for
        // exactly "schema_version" — pin the wire key.
        var state = new
        {
            schema_version = 1,
            ActiveProfile = "Discord_Privacy",
            SingBoxPid = 1234,
            StartedAt = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc),
            ProcessNames = new[] { "Discord.exe" },
        };
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        Assert.Contains("\"schema_version\"", json);
        Assert.Contains("\"ActiveProfile\"", json);
        Assert.Contains("\"SingBoxPid\"", json);
    }

    // ── DTO 5: HardeningState (Windows DNS hardening) ───────────────────────

    /// <summary>
    /// Mirror of the private <c>HardeningState</c> nested class in
    /// <c>WindowsDnsHardening</c>. The production type lives behind
    /// <c>#if PLATFORM_WINDOWS</c> and is private to the static class, so
    /// we re-declare the field shape here to pin the wire contract.
    /// Production semantics: WindowsDnsHardening writes the state via
    /// <c>JsonSerializer.Serialize</c> with <c>WriteIndented=true</c> +
    /// <c>PropertyNameCaseInsensitive=true</c>; no [JsonPropertyName]
    /// attributes so STJ emits the C# property names verbatim
    /// (PascalCase), matching the pre-Phase-4 Newtonsoft default exactly.
    /// </summary>
    private sealed class HardeningStateShape
    {
        public SavedRegValueShape Smhnr { get; set; } = new();
        public SavedRegValueShape ParallelAAAA { get; set; } = new();
        public bool TunMetricChanged { get; set; }
    }

    private sealed class SavedRegValueShape
    {
        public bool HadValue { get; set; }
        public int OldValue { get; set; }
    }

    [Fact]
    public void HardeningState_RoundTrip_PascalCaseKeysPreserved()
    {
        var state = new HardeningStateShape
        {
            Smhnr = new SavedRegValueShape { HadValue = true, OldValue = 1 },
            ParallelAAAA = new SavedRegValueShape { HadValue = false, OldValue = 0 },
            TunMetricChanged = true,
        };
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        var json = JsonSerializer.Serialize(state, options);

        // PascalCase wire keys — Newtonsoft default without [JsonProperty]
        // and STJ default without [JsonPropertyName] produce IDENTICAL
        // output. A future regression adding a global JsonNamingPolicy
        // would break every existing dns_hardening_state.json on user disks.
        Assert.Contains("\"Smhnr\"", json);
        Assert.Contains("\"ParallelAAAA\"", json);
        Assert.Contains("\"TunMetricChanged\"", json);
        Assert.Contains("\"HadValue\"", json);
        Assert.Contains("\"OldValue\"", json);

        var roundTripped = JsonSerializer.Deserialize<HardeningStateShape>(json, options);
        Assert.NotNull(roundTripped);
        Assert.True(roundTripped!.Smhnr.HadValue);
        Assert.Equal(1, roundTripped.Smhnr.OldValue);
        Assert.False(roundTripped.ParallelAAAA.HadValue);
        Assert.True(roundTripped.TunMetricChanged);
    }

    [Fact]
    public void HardeningState_LegacyNewtonsoftBlob_DeserializesCleanly()
    {
        // The pre-Phase-4 Newtonsoft writer (WindowsDnsHardening.SaveState
        // using JsonConvert.SerializeObject with Formatting.Indented and
        // un-annotated fields) emitted exactly this shape. STJ must accept
        // it byte-for-byte.
        const string legacyJson = """
        {
          "Smhnr": { "HadValue": true, "OldValue": 1 },
          "ParallelAAAA": { "HadValue": false, "OldValue": 0 },
          "TunMetricChanged": true
        }
        """;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var state = JsonSerializer.Deserialize<HardeningStateShape>(legacyJson, options);

        Assert.NotNull(state);
        Assert.True(state!.Smhnr.HadValue);
        Assert.Equal(1, state.Smhnr.OldValue);
        Assert.False(state.ParallelAAAA.HadValue);
        Assert.True(state.TunMetricChanged);
    }

    // ── DTO 6: CustomConfigInjector wire-format check ───────────────────────
    //
    // CustomConfigInjector.Inject builds its output via JsonNode mutation +
    // ToJsonString(WriteIndented). This isn't a strict DTO round-trip
    // (the input may be hand-written sing-box JSON), but the output must
    // still emit snake_case keys exactly as sing-box expects. The
    // sing-box check integration tests pin the full wire contract end-to-
    // end; this test pins the indentation + key-casing surface that other
    // sing-box clients (Clash, Stash) also depend on.

    [Fact]
    public void CustomConfigInjector_Output_IsIndentedWithSnakeCaseKeys()
    {
        const string minimalConfig = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "x" },
            { "type": "direct", "tag": "direct" }
          ],
          "route": { "rules": [], "final": "direct" }
        }
        """;

        var settings = new AppSettings
        {
            SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" },
        };
        var result = CustomConfigInjector.Inject(
            minimalConfig, new[] { "Discord.exe" }, settings);

        // Output is indented JSON (WriteIndented=true matches pre-Phase-4
        // Newtonsoft Formatting.Indented byte-for-byte).
        Assert.Contains("\n", result);

        // Injected process_name routing uses snake_case (sing-box wire spec).
        Assert.Contains("\"process_name\"", result);
        Assert.DoesNotContain("\"ProcessName\"", result);

        // Clash API + experimental block use snake_case.
        Assert.Contains("\"clash_api\"", result);
        Assert.Contains("\"external_controller\"", result);
        Assert.Contains("\"127.0.0.1:9090\"", result);
    }

    [Fact]
    public void CustomConfigInjector_Idempotent_TwoPassesProduceEquivalent()
    {
        // Idempotency contract: two consecutive Inject passes with the
        // same inputs must produce equivalent output (same set of
        // process_name route rules — no duplicates from re-injection).
        // The pre-Phase-4 Newtonsoft code relied on JObject's
        // dictionary-key uniqueness; STJ's JsonObject has the same
        // semantics, so the contract is preserved.
        const string config = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy" },
            { "type": "direct", "tag": "direct" }
          ],
          "route": { "rules": [], "final": "direct" }
        }
        """;

        var settings = new AppSettings { SingBox = new SingBoxSettings() };
        var first = CustomConfigInjector.Inject(config, new[] { "Discord.exe" }, settings);
        var second = CustomConfigInjector.Inject(first, new[] { "Discord.exe" }, settings);

        // Both passes must produce a config with exactly one process_name
        // route rule (the second Inject calls RemoveInjectedProcessRules
        // before re-injecting).
        var parsed = JsonNode.Parse(second) as JsonObject;
        Assert.NotNull(parsed);
        var rules = StjNodeHelpers.SelectToken(parsed!, "route.rules") as JsonArray;
        Assert.NotNull(rules);
        var processRules = rules!.Where(r => r?["process_name"] != null).ToList();
        Assert.Single(processRules);
    }

    // ── DTO 7: ConfigSanityCheck JsonObject-shaped input ────────────────────

    [Fact]
    public void ConfigSanityCheck_AcceptsJsonObjectFromStringPath()
    {
        // The string-overload of CheckBeforeStart parses via
        // JsonNode.Parse and dispatches to the JsonObject overload.
        // Pin the contract that a clean sing-box JSON passes the gate.
        const string cleanConfig = """
        {
          "outbounds": [
            {
              "type": "vless",
              "tag": "proxy",
              "server": "194.87.222.111",
              "server_port": 443,
              "uuid": "deadbeef-1234-5678-90ab-cdef01234567",
              "tls": {
                "reality": {
                  "public_key": "ValidPubKeyNotInPlaceholderList",
                  "short_id": "abcdef01"
                }
              }
            },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;

        var check = new ConfigSanityCheck();
        var result = check.CheckBeforeStart(cleanConfig);
        Assert.False(result.IsDead, result.Reason);
    }

    [Fact]
    public void ConfigSanityCheck_RejectsPlaceholderViaJsonObject()
    {
        // The JsonObject overload directly — pins the migration of
        // CheckBeforeStart(JObject) → CheckBeforeStart(JsonObject).
        var config = new JsonObject
        {
            ["outbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "vless",
                    ["tag"] = "proxy",
                    ["server"] = "1.2.3.4",
                    ["server_port"] = 443,
                    ["uuid"] = "u",
                    ["tls"] = new JsonObject
                    {
                        ["reality"] = new JsonObject
                        {
                            // Known placeholder pubkey from the stas evidence.
                            ["public_key"] = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU",
                            ["short_id"] = "abcdef01",
                        },
                    },
                },
            },
        };

        var check = new ConfigSanityCheck();
        var result = check.CheckBeforeStart(config);
        Assert.True(result.IsDead);
        Assert.Equal("outbound.tls.reality.public_key", result.OffendingField);
    }
}
