using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public class SettingsLoaderRobustnessTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsLoaderRobustnessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "VPNRouter.SR4." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string PathFor(string filename) => Path.Combine(_tempDir, filename);

    private static void AssertSane(AppSettings s)
    {
        Assert.NotNull(s);
        Assert.NotNull(s.App);
        Assert.NotNull(s.Vless);
        Assert.NotNull(s.Tun);
        Assert.NotNull(s.Dns);
        Assert.NotNull(s.SingBox);
        Assert.NotNull(s.Monitoring);
        Assert.NotNull(s.Update);
        Assert.NotNull(s.ProfileSources);
        Assert.NotNull(s.CustomApps);
        Assert.NotNull(s.CustomGroupApps);
        Assert.NotNull(s.CustomCategories);
        Assert.NotNull(s.ExcludedApps);
        Assert.NotNull(s.Vless.Reality);
        Assert.NotNull(s.Vless.Tls);
        Assert.NotNull(s.Vless.Transport);
        Assert.NotNull(s.Vless.Servers);
        Assert.NotNull(s.Vless.Transport.Headers);
        Assert.NotNull(s.Tun.RouteExcludeAddress);
        Assert.NotNull(s.App.CustomConfigs);
        Assert.NotNull(s.App.SubscriptionServers);
        Assert.NotNull(s.App.Subscriptions);
        Assert.NotNull(s.App.CustomDirectRules);
        Assert.NotNull(s.App.CustomRules);
        Assert.NotNull(s.App.UserFreeSources);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var path = PathFor("missing.yaml");
        Assert.False(File.Exists(path));

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
        Assert.True(File.Exists(path),
            "Load() with missing file should write an example config side-effect.");
    }

    [Fact]
    public void Load_EmptyFile_ReturnsDefaults_NoBackup()
    {
        var path = PathFor("empty.yaml");
        File.WriteAllText(path, string.Empty);

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        var unloadable = Directory.GetFiles(_tempDir, "*.unloadable-*");
        Assert.Empty(unloadable);
    }

    [Fact]
    public void Load_MalformedYaml_ReturnsDefaults_BackupsCorruptFile()
    {
        var path = PathFor("malformed.yaml");
        File.WriteAllText(path,
            "app:\n" +
            "  routing_mode: split\n" +
            "    bad: indentation\n" +
            "vless:\n" +
            "  servers: [unterminated\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.False(File.Exists(path),
            "Unloadable file should be renamed to .unloadable-{ts}");
        var backups = Directory.GetFiles(_tempDir, "malformed.yaml.unloadable-*");
        Assert.Single(backups);
    }

    [Fact]
    public void Load_MissingTopLevelSection_FillsInDefaults()
    {
        var path = PathFor("partial.yaml");
        File.WriteAllText(path,
            "vless:\n" +
            "  server: example.com\n" +
            "  port: 443\n" +
            "  uuid: aaaa-bbbb-cccc-dddd\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.Equal("example.com", s.Vless.Server);
        Assert.Equal(443, s.Vless.Port);
        Assert.Equal("aaaa-bbbb-cccc-dddd", s.Vless.Uuid);
        Assert.Equal("split", s.App.RoutingMode);
        Assert.Equal("system", s.App.Theme);
    }

    [Fact]
    public void Load_PartialSubSection_PreservesOtherFields_FillsMissing()
    {
        var path = PathFor("partial-vless.yaml");
        File.WriteAllText(path,
            "vless:\n" +
            "  server: real.example.com\n" +
            "  reality: ~\n" +
            "  tls: ~\n" +
            "  transport: ~\n" +
            "  servers: ~\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.Equal("real.example.com", s.Vless.Server);
        Assert.NotNull(s.Vless.Reality);
        Assert.NotNull(s.Vless.Tls);
        Assert.NotNull(s.Vless.Transport);
        Assert.NotNull(s.Vless.Servers);
        Assert.Empty(s.Vless.Servers);
    }

    [Fact]
    public void Load_TypeCoercionFailure_ReturnsDefaults_BackupsFile()
    {
        var path = PathFor("typemismatch.yaml");
        File.WriteAllText(path,
            "app:\n" +
            "  tg_proxy_port: \"not-a-number\"\n" +
            "  tg_proxy_enabled: true\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.False(File.Exists(path));
        var backups = Directory.GetFiles(_tempDir, "typemismatch.yaml.unloadable-*");
        Assert.Single(backups);
        Assert.Equal(1443, s.App.TgProxyPort);
        Assert.False(s.App.TgProxyEnabled);
    }

    [Fact]
    public void Load_FileLocked_ReturnsDefaults_OriginalUntouched()
    {
        var path = PathFor("locked.yaml");
        File.WriteAllText(path, "app:\n  theme: dark\n");

        using (var holder = new FileStream(path, FileMode.Open,
                   FileAccess.Read, FileShare.None))
        {
            var s = SettingsLoader.Load(path);

            AssertSane(s);
            Assert.True(File.Exists(path));
            var backups = Directory.GetFiles(_tempDir, "locked.yaml.unloadable-*");
            Assert.Empty(backups);
            Assert.Equal("system", s.App.Theme);
            GC.KeepAlive(holder);
        }
    }

    [Fact]
    public void Load_UnknownFutureFields_AreIgnored_KnownFieldsPreserved()
    {
        var path = PathFor("future.yaml");
        File.WriteAllText(path,
            "schema_version: 2\n" +
            "future_field_we_have_not_invented_yet: 42\n" +
            "app:\n" +
            "  theme: dark\n" +
            "  some_field_from_v2_99: enabled\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.Equal("dark", s.App.Theme);
        Assert.True(File.Exists(path));
        var backups = Directory.GetFiles(_tempDir, "future.yaml.unloadable-*");
        Assert.Empty(backups);
    }

    [Fact]
    public void Load_FileWithUtf8Bom_LoadsSuccessfully()
    {
        var path = PathFor("bom.yaml");
        var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
        bytes.AddRange(Encoding.UTF8.GetBytes(
            "app:\n  theme: dark\n  language: ru\n"));
        File.WriteAllBytes(path, bytes.ToArray());

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.Equal("dark", s.App.Theme);
        Assert.Equal("ru", s.App.Language);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_NonMappingRoot_ReturnsDefaults_BackupsFile()
    {
        var path = PathFor("scalar-root.yaml");
        File.WriteAllText(path, "this is just a string");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.False(File.Exists(path));
        var backups = Directory.GetFiles(_tempDir, "scalar-root.yaml.unloadable-*");
        Assert.Single(backups);
    }

    [Fact]
    public void Load_NoRecognizedKeys_ReturnsDefaults_BackupsFile()
    {
        var path = PathFor("alien.yaml");
        File.WriteAllText(path,
            "completely:\n  unrelated:\n    config: file\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.False(File.Exists(path));
        var backups = Directory.GetFiles(_tempDir, "alien.yaml.unloadable-*");
        Assert.Single(backups);
    }

    [Fact]
    public void EnsureSane_IsIdempotent()
    {
        var s = new AppSettings();
        s.Vless.Servers.Add(new VlessServerEntry { Server = "a.example.com" });
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Url = "https://example.com/sub",
            Servers = { new VlessServerEntry { Server = "b.example.com" } }
        });
        s.CustomGroupApps["Discord"] = new List<string> { "Discord.exe" };

        var firstPass = s.EnsureSane();
        var firstServersCount = firstPass.Vless.Servers.Count;
        var firstSubCount = firstPass.App.Subscriptions.Count;

        var secondPass = firstPass.EnsureSane();

        Assert.Same(firstPass, secondPass);
        Assert.Equal(firstServersCount, secondPass.Vless.Servers.Count);
        Assert.Equal(firstSubCount, secondPass.App.Subscriptions.Count);
        Assert.Equal("a.example.com", secondPass.Vless.Servers[0].Server);
        Assert.Equal("b.example.com",
            secondPass.App.Subscriptions[0].Servers[0].Server);
    }

    [Fact]
    public void EnsureSane_OnNullReceiver_ReturnsFreshDefaults()
    {
        AppSettings? nothing = null;
        var s = nothing.EnsureSane();
        AssertSane(s);
        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
    }

    [Fact]
    public void Load_TriggersSchemaMigration_FromV1ToCurrent()
    {
        var path = PathFor("legacy.yaml");
        File.WriteAllText(path,
            "schema_version: 1\n" +
            "app:\n" +
            "  custom_direct_rules:\n" +
            "    - type: ip_cidr\n" +
            "      value: 10.0.0.0/8\n" +
            "      comment: LAN\n" +
            "      enabled: true\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
        Assert.Single(s.App.CustomRules);
        Assert.Equal("direct", s.App.CustomRules[0].Action);
        Assert.Equal("ip_cidr", s.App.CustomRules[0].Type);
        Assert.Equal("LAN", s.App.CustomRules[0].Comment);
    }

    [Fact]
    public void Save_ThenLoad_PersistsExcludedApps()
    {
        var path = PathFor("excluded-apps-roundtrip.yaml");
        var s = new AppSettings
        {
            ExcludedApps = new List<string> { "firefox.exe", "msedge.exe" }
        };
        SettingsLoader.Save(s, path);

        var yaml = File.ReadAllText(path);
        Assert.Contains("excluded_apps:", yaml);
        Assert.Contains("firefox.exe", yaml);

        var reloaded = SettingsLoader.Load(path);
        AssertSane(reloaded);
        Assert.Equal(2, reloaded.ExcludedApps.Count);
        Assert.Contains("firefox.exe", reloaded.ExcludedApps);
        Assert.Contains("msedge.exe", reloaded.ExcludedApps);
    }

    [Fact]
    public void Load_PreV9IConfigWithoutExcludedApps_DefaultsToEmptyList()
    {
        var path = PathFor("legacy-no-excluded.yaml");
        File.WriteAllText(path,
            "schema_version: 2\n" +
            "app:\n" +
            "  routing_mode: split\n" +
            "  theme: dark\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.NotNull(s.ExcludedApps);
        Assert.Empty(s.ExcludedApps);
    }
}
