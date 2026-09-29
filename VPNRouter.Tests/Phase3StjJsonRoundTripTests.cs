#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class Phase3StjJsonRoundTripTests
{
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
        var profile = MakeProfile();
        var json = JsonSerializer.Serialize(profile, ProfileManager.SafeJsonOptions);

        Assert.Contains("\"dns_mode\"", json);
        Assert.Contains("\"block_on_vpn_fail\"", json);
        Assert.Contains("\"include_children\"", json);
        Assert.Contains("\"scan_patterns\"", json);
        Assert.Contains("\"android_packages\"", json);

        Assert.DoesNotContain("\"DnsMode\"", json);
        Assert.DoesNotContain("\"BlockOnVpnFail\"", json);
        Assert.DoesNotContain("\"IncludeChildren\"", json);
    }

    [Fact]
    public void Profile_LegacyWireFormat_DeserializesViaCaseInsensitive()
    {
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

        var json2 = JsonSerializer.Serialize(deserialized, ProfileManager.SafeJsonOptions);
        Assert.Equal(json1, json2);
    }

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
        var json = JsonSerializer.Serialize(
            new ProfileCacheFile { SchemaVersion = 42 },
            ProfileManager.SafeJsonOptions);

        Assert.Contains("\"schema_version\": 42", json.Replace(" ", " "));
    }

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
        Assert.DoesNotContain("\"server\":", json);
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

    [Fact]
    public void ProfileCollection_FullRoundTripUnderDosGuard()
    {
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
