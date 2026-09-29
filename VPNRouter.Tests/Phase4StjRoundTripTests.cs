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

public sealed class Phase4StjRoundTripTests
{
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

        Assert.DoesNotContain("\"ServerPort\"", json);
        Assert.DoesNotContain("\"ProcessName\"", json);
        Assert.DoesNotContain("\"IpIsPrivate\"", json);
        Assert.DoesNotContain("\"AutoRoute\"", json);
        Assert.DoesNotContain("\"StrictRoute\"", json);

        Assert.DoesNotContain("\"flow\":null", json);
        Assert.DoesNotContain("\"reality\":null", json);
        Assert.DoesNotContain("\"utls\":null", json);
    }

    [Fact]
    public void SingBoxConfig_RoundTrip_DeserializesBackCleanly()
    {
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

    [Fact]
    public void GitHubRelease_LegacyApiResponse_ParsesCleanly()
    {
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

    [Fact]
    public void RunState_RoundTrip_ViaStjPreservesSchemaVersionKey()
    {
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

        Assert.Contains("\n", result);

        Assert.Contains("\"process_name\"", result);
        Assert.DoesNotContain("\"ProcessName\"", result);

        Assert.Contains("\"clash_api\"", result);
        Assert.Contains("\"external_controller\"", result);
        Assert.Contains("\"127.0.0.1:9090\"", result);
    }

    [Fact]
    public void CustomConfigInjector_Idempotent_TwoPassesProduceEquivalent()
    {
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

        var parsed = JsonNode.Parse(second) as JsonObject;
        Assert.NotNull(parsed);
        var rules = StjNodeHelpers.SelectToken(parsed!, "route.rules") as JsonArray;
        Assert.NotNull(rules);
        var processRules = rules!.Where(r => r?["process_name"] != null).ToList();
        Assert.Single(processRules);
    }

    [Fact]
    public void ConfigSanityCheck_AcceptsJsonObjectFromStringPath()
    {
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
