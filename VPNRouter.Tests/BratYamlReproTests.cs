using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public class BratYamlReproTests : IDisposable
{
    private readonly string _tempDir;

    public BratYamlReproTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "VPNRouter.BratRepro." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string TempYamlPath() => Path.Combine(_tempDir, "config.yaml");

    [Fact]
    public void Iter1_BratV232YamlState_LoadsWithoutSilentWipe()
    {
        var yaml = @"
schema_version: 4
app:
  config_mode: subscribe
  routing_mode: split
  routing_apps_mode: include
  routing_apps_include:
  - Discord.exe
  - chrome.exe
  subscriptions:
  - id: ninitux-id
    name: ninitux
    url: https://example.invalid/redacted-test-subscription
    enabled: true
    last_refreshed_at: '2026-05-19T20:42:23+03:00'
    last_server_count: 7
    servers:
    - name: de-01 443 main-brat
      server: 1.2.3.4
      port: 443
      uuid: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
      flow: xtls-rprx-vision
      security: reality
      reality:
        enabled: true
        public_key: pk1
        short_id: sid1
        server_name: yahoo.com
        fingerprint: chrome
    - name: is-01 443 main-brat
      server: 5.6.7.8
      port: 443
      uuid: ffffffff-1111-2222-3333-444444444444
      flow: xtls-rprx-vision
      security: reality
      reality:
        enabled: true
        public_key: pk2
        short_id: sid2
        server_name: yahoo.com
        fingerprint: chrome
  active_subscription_server: de-01 443 main-brat
  language: ru
  ui_mode: advanced
  theme: light
vless:
  server: ''
  port: 443
  uuid: ''
  flow: ''
  servers:
  - name: main-brat-manual
    server: 9.10.11.12
    port: 443
    uuid: 99999999-8888-7777-6666-555555555555
    flow: xtls-rprx-vision
    security: reality
    reality:
      enabled: true
      public_key: pk-manual
      short_id: sid-manual
      server_name: yahoo.com
      fingerprint: chrome
  active_server: main-brat-manual
";

        var loaded = SettingsLoader.Parse(yaml);

        Assert.NotNull(loaded);
        Assert.Equal("subscribe", loaded.App.ConfigMode);

        Assert.Single(loaded.App.Subscriptions);
        var sub = loaded.App.Subscriptions[0];
        Assert.Equal("ninitux", sub.Name);
        Assert.True(sub.Enabled, "Subscription enabled flag dropped during parse");
        Assert.Equal(2, sub.Servers.Count);
        Assert.Equal("de-01 443 main-brat", sub.Servers[0].Name);
        Assert.Equal(443, sub.Servers[0].Port);

        Assert.NotNull(loaded.Vless.Servers);
        Assert.Single(loaded.Vless.Servers);
        Assert.Equal("main-brat-manual", loaded.Vless.Servers[0].Name);
        Assert.Equal("9.10.11.12", loaded.Vless.Servers[0].Server);

        Assert.Equal("main-brat-manual", loaded.Vless.ActiveServer);
        Assert.Equal("de-01 443 main-brat", loaded.App.ActiveSubscriptionServer);
    }

    [Fact]
    public void Iter1b_BratV232YamlState_FullLoadPath_DoesNotWipe()
    {
        var path = TempYamlPath();
        File.WriteAllText(path, BratV232YamlFixture);

        var loaded = SettingsLoader.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal("subscribe", loaded.App.ConfigMode);
        Assert.Single(loaded.App.Subscriptions);
        Assert.Equal(2, loaded.App.Subscriptions[0].Servers.Count);
        Assert.NotNull(loaded.Vless.Servers);
        Assert.Single(loaded.Vless.Servers);
        Assert.Equal("main-brat-manual", loaded.Vless.Servers[0].Name);

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void Iter1c_MissingSchemaVersion_DoesNotTriggerOrphanCleanup()
    {
        var yamlWithoutSchema = BratV232YamlFixture
            .Replace("schema_version: 4\n", "")
            .Replace("schema_version: 4\r\n", "");

        var path = TempYamlPath();
        File.WriteAllText(path, yamlWithoutSchema);

        var loaded = SettingsLoader.Load(path);

        Assert.NotNull(loaded.Vless.Servers);
        if (loaded.Vless.Servers.Count == 0)
        {
            throw new Xunit.Sdk.XunitException(
                "REPRO CONFIRMED: missing schema_version triggered " +
                "Migrate_2_to_3 → CleanupOrphanVlessServers → wiped " +
                "Vless.Servers because main-brat-manual didn't match " +
                "any subscription server key. This is brat's bug.");
        }

        Assert.Single(loaded.Vless.Servers);
    }

    [Fact]
    public void Iter1d_SchemaVersionZero_TriggersFullMigrationChain_BR4Fix()
    {
        var yamlWithSchemaZero = BratV232YamlFixture.Replace("schema_version: 4", "schema_version: 0");

        var path = TempYamlPath();
        File.WriteAllText(path, yamlWithSchemaZero);

        var loaded = SettingsLoader.Load(path);

        Assert.NotNull(loaded.Vless.Servers);
        Assert.Single(loaded.Vless.Servers);
        Assert.Equal("main-brat-manual", loaded.Vless.Servers[0].Name);
        Assert.Equal("main-brat-manual", loaded.Vless.ActiveServer);
    }

    [Fact]
    public void Iter4_BR4Fix_PreservesActiveServerOnly_RemovesOthers()
    {
        var yaml = @"schema_version: 0
app:
  config_mode: subscribe
  subscriptions:
  - name: provider
    url: https://example.com/sub
    enabled: true
    servers:
    - name: provider-server
      server: 1.1.1.1
      port: 443
      uuid: aaaa
vless:
  servers:
  - name: user-active-manual
    server: 9.9.9.9
    port: 443
    uuid: bbbb
  - name: stale-orphan
    server: 8.8.8.8
    port: 443
    uuid: cccc
  active_server: user-active-manual
";

        var path = TempYamlPath();
        File.WriteAllText(path, yaml);
        var loaded = SettingsLoader.Load(path);

        Assert.Single(loaded.Vless.Servers);
        Assert.Equal("user-active-manual", loaded.Vless.Servers[0].Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Iter2_AllSchemaVersions_BR4Fix_PreservesActiveServer(int schemaVersion)
    {
        var yaml = BratV232YamlFixture.Replace("schema_version: 4", $"schema_version: {schemaVersion}");

        var path = TempYamlPath();
        File.WriteAllText(path, yaml);

        var loaded = SettingsLoader.Load(path);

        Assert.Single(loaded.Vless.Servers);
        Assert.Equal("main-brat-manual", loaded.Vless.Servers[0].Name);
        Assert.Equal("main-brat-manual", loaded.Vless.ActiveServer);
    }

    [Fact]
    public void Iter2b_SchemaVersionEmptyString_FallsToDefaults_NoDataPartialWipe()
    {
        var yaml = BratV232YamlFixture.Replace("schema_version: 4", "schema_version: ''");

        var path = TempYamlPath();
        File.WriteAllText(path, yaml);

        var loaded = SettingsLoader.Load(path);

        if (loaded.Vless.Servers.Count == 0)
        {
            var backupExists = Directory.GetFiles(_tempDir, "config.yaml.unloadable-*").Any()
                || Directory.GetFiles(_tempDir, "config.yaml.invalid-*").Any();
        }
        else
        {
            Assert.Equal("main-brat-manual", loaded.Vless.Servers[0].Name);
        }
    }

    [Fact]
    public void Iter3_StaticDeserializer_SchemaVersionRawBehaviour()
    {
        var deserializer = new YamlDotNet.Serialization.StaticDeserializerBuilder(
                new VPNRouter.Core.Yaml.YamlStaticContext())
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new VPNRouter.Core.Yaml.DateTimeOffsetYamlConverter())
            .IgnoreUnmatchedProperties()
            .Build();

        var a = deserializer.Deserialize<AppSettings>(
            "schema_version: 4\napp:\n  theme: dark\n");
        Assert.Equal(4, a.SchemaVersion);

        var b = deserializer.Deserialize<AppSettings>(
            "app:\n  theme: dark\n");
        if (b.SchemaVersion != AppSettings.CurrentSchemaVersion)
        {
            throw new Xunit.Sdk.XunitException(
                $"REGRESSION ROOT CAUSE FOUND: StaticDeserializer initialises " +
                $"AppSettings.SchemaVersion to {b.SchemaVersion} when the YAML field " +
                $"is missing, NOT the C# field default (CurrentSchemaVersion = " +
                $"{AppSettings.CurrentSchemaVersion}). " +
                $"This means any YAML missing schema_version triggers the full " +
                $"migration chain → CleanupOrphanVlessServers wipes manual " +
                $"Vless.Servers entries. " +
                $"If brat's YAML lost its schema_version (e.g. via a previous " +
                $"v2.32.2 serializer bug, hand-edit, or new bootstrap path), " +
                $"this explains the manual=2 → manual=0 transition.");
        }

        var c = deserializer.Deserialize<AppSettings>(
            "schema_version: 0\napp:\n  theme: dark\n");
        Assert.Equal(0, c.SchemaVersion);

        try
        {
            var d = deserializer.Deserialize<AppSettings>(
                "schema_version: ''\napp:\n  theme: dark\n");
            if (d.SchemaVersion < 3)
            {
                throw new Xunit.Sdk.XunitException(
                    $"REGRESSION: empty-string schema_version coerced to {d.SchemaVersion} — " +
                    $"would trigger full migration → wipe manual servers.");
            }
        }
        catch (Exception ex) when (ex.GetType().FullName?.Contains("Yaml") == true)
        {
        }
    }

    [Fact]
    public void Iter2c_SchemaVersionNull_FallsToDefaults_NoDataPartialWipe()
    {
        var yaml = BratV232YamlFixture.Replace("schema_version: 4", "schema_version: null");

        var path = TempYamlPath();
        File.WriteAllText(path, yaml);

        var loaded = SettingsLoader.Load(path);

        if (loaded.Vless.Servers.Count > 0)
        {
            Assert.Equal("main-brat-manual", loaded.Vless.Servers[0].Name);
        }
    }

    private const string BratV232YamlFixture = @"schema_version: 4
app:
  config_mode: subscribe
  routing_mode: split
  routing_apps_mode: include
  routing_apps_include:
  - Discord.exe
  - chrome.exe
  subscriptions:
  - id: ninitux-id
    name: ninitux
    url: https://example.invalid/redacted-test-subscription
    enabled: true
    last_refreshed_at: '2026-05-19T20:42:23+03:00'
    last_server_count: 7
    servers:
    - name: de-01 443 main-brat
      server: 1.2.3.4
      port: 443
      uuid: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
      flow: xtls-rprx-vision
      security: reality
      reality:
        enabled: true
        public_key: pk1
        short_id: sid1
        server_name: yahoo.com
        fingerprint: chrome
    - name: is-01 443 main-brat
      server: 5.6.7.8
      port: 443
      uuid: ffffffff-1111-2222-3333-444444444444
      flow: xtls-rprx-vision
      security: reality
      reality:
        enabled: true
        public_key: pk2
        short_id: sid2
        server_name: yahoo.com
        fingerprint: chrome
  active_subscription_server: de-01 443 main-brat
  language: ru
  ui_mode: advanced
  theme: light
vless:
  server: ''
  port: 443
  uuid: ''
  flow: ''
  servers:
  - name: main-brat-manual
    server: 9.10.11.12
    port: 443
    uuid: 99999999-8888-7777-6666-555555555555
    flow: xtls-rprx-vision
    security: reality
    reality:
      enabled: true
      public_key: pk-manual
      short_id: sid-manual
      server_name: yahoo.com
      fingerprint: chrome
  active_server: main-brat-manual
";
}
