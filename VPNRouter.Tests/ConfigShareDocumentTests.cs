using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// v2.32.0 (Android-led) — pin tests for the Core <see cref="ConfigShareDocument"/>
/// schema. Verifies the round-trip
/// invariant (Build → Serialize → TryParse preserves all fields), schema
/// rejection paths, preview generation, and suggested export filenames.
/// </summary>
public class ConfigShareDocumentTests
{
    private static ConfigShareDocument BuildSampleDocument()
    {
        return new ConfigShareDocument
        {
            ExportedAt = new DateTimeOffset(2026, 5, 7, 18, 30, 0, TimeSpan.Zero),
            ExportedFrom = new ExportedFromInfo
            {
                Platform = "android",
                AppVersion = "2.32.0-r2",
                DeviceLabel = "KYOCERA A101BM",
            },
            ConfigMode = "subscribe",
            Subscriptions = new List<SubscriptionEntry>
            {
                new()
                {
                    Id = "abc123",
                    Name = "Default",
                    Url = "https://example.com/vless.txt",
                    Enabled = true,
                    LastServerCount = 2,
                    Servers = new List<VlessServerEntry>
                    {
                        new()
                        {
                            Name = "de-01",
                            Server = "1.2.3.4",
                            Port = 443,
                            Uuid = "uuid-1",
                            Flow = "xtls-rprx-vision",
                        },
                        new()
                        {
                            Name = "us-01",
                            Server = "5.6.7.8",
                            Port = 443,
                            Uuid = "uuid-2",
                            Flow = "xtls-rprx-vision",
                        },
                    },
                },
            },
        };
    }

    [Fact]
    public void Roundtrip_Subscribe_PreservesFields()
    {
        var doc = BuildSampleDocument();
        var json = ConfigShareDocument.Serialize(doc);

        var result = ConfigShareDocument.TryParse(json);
        Assert.True(result.Ok, $"parse failed: {result.Error}");
        var parsed = result.Document!;

        Assert.Equal(ConfigShareDocument.SchemaMarker, parsed.Schema);
        Assert.Equal(ConfigShareDocument.CurrentVersion, parsed.Version);
        Assert.Equal("subscribe", parsed.ConfigMode);
        Assert.Single(parsed.Subscriptions);
        Assert.Equal("Default", parsed.Subscriptions[0].Name);
        Assert.Equal(2, parsed.Subscriptions[0].Servers.Count);
        Assert.Equal("de-01", parsed.Subscriptions[0].Servers[0].Name);
        Assert.Equal("android", parsed.ExportedFrom.Platform);
        Assert.Null(parsed.ManualVlessUri);
        Assert.Null(parsed.CustomConfig);
        Assert.Null(parsed.Settings);
        Assert.Null(parsed.PerAppFilter);
    }

    [Fact]
    public void Roundtrip_ManualUriMode_Preserved()
    {
        var doc = new ConfigShareDocument
        {
            ConfigMode = "manual",
            ManualVlessUri = "vless://uuid@host:443?security=reality&pbk=key#name",
            ExportedAt = DateTimeOffset.UtcNow,
        };
        var json = ConfigShareDocument.Serialize(doc);
        var result = ConfigShareDocument.TryParse(json);
        Assert.True(result.Ok);
        Assert.Equal("manual", result.Document!.ConfigMode);
        Assert.StartsWith("vless://", result.Document.ManualVlessUri);
    }

    [Fact]
    public void Roundtrip_CustomConfigMode_Preserved()
    {
        var rawJson = "{\"log\":{\"level\":\"info\"},\"outbounds\":[{\"type\":\"direct\"}]}";
        var doc = new ConfigShareDocument
        {
            ConfigMode = "custom",
            CustomConfig = new CustomConfigPayload
            {
                Name = "my-tuic",
                SingBoxJson = rawJson,
            },
            ExportedAt = DateTimeOffset.UtcNow,
        };
        var json = ConfigShareDocument.Serialize(doc);
        var result = ConfigShareDocument.TryParse(json);
        Assert.True(result.Ok);
        Assert.NotNull(result.Document!.CustomConfig);
        Assert.Equal("my-tuic", result.Document.CustomConfig!.Name);
        Assert.Equal(rawJson, result.Document.CustomConfig.SingBoxJson);
    }

    [Fact]
    public void Roundtrip_OptInSettings_Preserved()
    {
        var doc = BuildSampleDocument();
        doc.Settings = new ExportedSettings
        {
            Theme = "dark",
            Language = "ru",
            RoutingMode = "split",
            BypassRussianTraffic = true,
            BlockOnVpnFail = false,
            DnsStrategy = "ipv4_only",
            UpdateChannel = "experimental",
            AutostartVpn = true,
            AutostartZapret = false,
            AutostartTgProxy = false,
        };

        var json = ConfigShareDocument.Serialize(doc);
        var result = ConfigShareDocument.TryParse(json);
        Assert.True(result.Ok);

        var s = result.Document!.Settings!;
        Assert.Equal("dark", s.Theme);
        Assert.Equal("ru", s.Language);
        Assert.Equal("split", s.RoutingMode);
        Assert.Equal(true, s.BypassRussianTraffic);
        Assert.Equal("ipv4_only", s.DnsStrategy);
        Assert.Equal("experimental", s.UpdateChannel);
        Assert.Equal(true, s.AutostartVpn);
    }

    [Fact]
    public void Roundtrip_PerAppFilter_Preserved()
    {
        var doc = BuildSampleDocument();
        doc.PerAppFilter = new PerAppFilterExport
        {
            Mode = "include",
            Packages = new List<string> { "com.discord", "com.spotify.music" },
        };

        var json = ConfigShareDocument.Serialize(doc);
        var result = ConfigShareDocument.TryParse(json);
        Assert.True(result.Ok);

        var f = result.Document!.PerAppFilter!;
        Assert.Equal("include", f.Mode);
        Assert.Equal(2, f.Packages.Count);
        Assert.Contains("com.discord", f.Packages);
    }

    [Fact]
    public void Parse_EmptyInput_Fails()
    {
        var r1 = ConfigShareDocument.TryParse(null);
        Assert.False(r1.Ok);
        Assert.Contains("empty", r1.Error!);

        var r2 = ConfigShareDocument.TryParse("");
        Assert.False(r2.Ok);

        var r3 = ConfigShareDocument.TryParse("   ");
        Assert.False(r3.Ok);
    }

    [Fact]
    public void Parse_MalformedJson_Fails()
    {
        var result = ConfigShareDocument.TryParse("{not valid json");
        Assert.False(result.Ok);
        Assert.Contains("malformed", result.Error!.ToLowerInvariant());
    }

    [Fact]
    public void Parse_WrongSchemaMarker_Fails()
    {
        var json = "{\"schema\":\"some-other-app\",\"version\":1}";
        var result = ConfigShareDocument.TryParse(json);
        Assert.False(result.Ok);
        Assert.Contains("schema marker", result.Error!);
    }

    [Fact]
    public void Parse_FutureVersion_Fails()
    {
        var json = $"{{\"schema\":\"{ConfigShareDocument.SchemaMarker}\",\"version\":99}}";
        var result = ConfigShareDocument.TryParse(json);
        Assert.False(result.Ok);
        Assert.Contains("newer than supported", result.Error!);
    }

    [Fact]
    public void Parse_UnknownConfigMode_Fails()
    {
        var json = $"{{\"schema\":\"{ConfigShareDocument.SchemaMarker}\",\"version\":1,\"config_mode\":\"made-up\"}}";
        var result = ConfigShareDocument.TryParse(json);
        Assert.False(result.Ok);
        Assert.Contains("unknown config_mode", result.Error!);
    }

    [Fact]
    public void Parse_CustomModeWithoutPayload_Fails()
    {
        var json = $"{{\"schema\":\"{ConfigShareDocument.SchemaMarker}\",\"version\":1,\"config_mode\":\"custom\"}}";
        var result = ConfigShareDocument.TryParse(json);
        Assert.False(result.Ok);
        Assert.Contains("custom_config", result.Error!);
    }

    [Fact]
    public void BuildPreview_Russian_ShowsCounts()
    {
        var doc = BuildSampleDocument();
        var preview = doc.BuildPreview(ru: true);
        Assert.Contains("Подписки: 1", preview);
        Assert.Contains("Серверы: 2", preview);
    }

    [Fact]
    public void BuildPreview_English_ShowsCounts()
    {
        var doc = BuildSampleDocument();
        doc.Settings = new ExportedSettings { Theme = "light" };
        doc.PerAppFilter = new PerAppFilterExport { Mode = "include", Packages = new List<string> { "a", "b" } };

        var preview = doc.BuildPreview(ru: false);
        Assert.Contains("Subscriptions: 1", preview);
        Assert.Contains("Settings: included", preview);
        Assert.Contains("2 apps", preview);
    }

    [Fact]
    public void SuggestFilename_ProducesSortableTimestamp()
    {
        var when = new DateTimeOffset(2026, 5, 7, 18, 30, 0, TimeSpan.Zero).ToLocalTime();
        var name = ConfigShareDocument.SuggestFilename(when);
        Assert.StartsWith("vpnrouter-config-", name);
        Assert.EndsWith(".json", name);
        Assert.Contains("2026", name);
    }

    [Fact]
    public void Parse_DropsExportedFromNullToDefault()
    {
        // Producer omitted exported_from entirely — TryParse should not
        // crash with a NullReferenceException downstream.
        var json = $"{{\"schema\":\"{ConfigShareDocument.SchemaMarker}\",\"version\":1,\"config_mode\":\"subscribe\"}}";
        var result = ConfigShareDocument.TryParse(json);
        Assert.True(result.Ok, $"parse failed: {result.Error}");
        Assert.NotNull(result.Document!.ExportedFrom);
        Assert.NotNull(result.Document.Subscriptions);
        Assert.Empty(result.Document.Subscriptions);
    }
}
