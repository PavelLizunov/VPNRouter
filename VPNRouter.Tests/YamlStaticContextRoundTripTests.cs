using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Phase 6 Wave 31a (2026-05-18) — pin the wire-format contract of the
/// <see cref="SettingsLoader"/> after the YamlDotNet
/// <c>DeserializerBuilder</c>/<c>SerializerBuilder</c> swap to
/// <c>StaticDeserializerBuilder</c> + <c>StaticSerializerBuilder</c>
/// driven by the analyzer-generated <c>YamlStaticContext</c>.
///
/// <para>Behaviour contract under test: a round-trip through Parse →
/// Save → Parse yields equal values across every nested DTO branch of
/// <see cref="AppSettings"/>. The static builders use a different
/// codepath internally (compile-time emit vs runtime reflection) so
/// even with the same naming convention configured, divergence at
/// edge cases (e.g. <see cref="System.DateTimeOffset"/> scalar format,
/// empty collections, dictionary key ordering) would break user-facing
/// config persistence silently. These tests catch that pre-ship.</para>
///
/// <para>Why three cases:</para>
/// <list type="number">
///   <item><b>Defaults round-trip</b> — load → save → load on a
///   freshly-constructed <see cref="AppSettings"/>. Pins the empty/default
///   path used on every first launch.</item>
///   <item><b>Populated round-trip</b> — exercise every nested DTO with
///   non-default values (string, int, bool, nested object, list,
///   dictionary, DateTimeOffset). Pins the data-laden path used on every
///   subsequent launch with user-modified config.</item>
///   <item><b>Wire-format pin</b> — load a hand-crafted YAML fixture
///   exercising the same shape, assert specific field values to catch
///   any property-naming/alias divergence between the reflective and
///   static parsers.</item>
/// </list>
///
/// <para>3G-1 (v3.0 refactor): joined <see cref="SafeModeStateCollection"/>
/// like the sibling <see cref="SettingsLoaderRobustnessTests"/> so the
/// loader's <c>if (SafeMode.Enabled)</c> short-circuit can't fire while
/// a parallel SafeMode-flipping test (StartupPipelineTests) is mid-flight.</para>
/// </summary>
[Collection(SafeModeStateCollection.Name)]
public class YamlStaticContextRoundTripTests : IDisposable
{
    private readonly string _tempDir;

    public YamlStaticContextRoundTripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "VPNRouter.YamlStatic." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string TempYamlPath() => Path.Combine(_tempDir, "config.yaml");

    // ─────────────────────────────────────────────────────────────────────
    // 1. Defaults round-trip — Save() of a fresh AppSettings, then Parse()
    //    of the YAML, yields back the same defaults.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_SaveAndReload_PreservesAllDefaultValues()
    {
        var path = TempYamlPath();
        // Construct AppSettings with the same defaults SettingsLoader.CreateDefaults
        // would produce. We use the same call surface as ResetToDefaults so the
        // round-trip exercises the production CreateDefaults → Save → Parse path.
        SettingsLoader.ResetToDefaults(path);

        // Read back the file written by Save and parse it explicitly. We don't
        // route through Load() here because Load() would short-circuit any
        // recovery branches that don't tell us whether the parse succeeded
        // unmodified — Parse + the raw yaml is the strictest test.
        var yaml = File.ReadAllText(path);
        var roundTripped = SettingsLoader.Parse(yaml);

        // Every reference-typed sub-section must be present and have
        // structurally-default values.
        Assert.NotNull(roundTripped.App);
        Assert.NotNull(roundTripped.Vless);
        Assert.NotNull(roundTripped.Tun);
        Assert.NotNull(roundTripped.Dns);
        Assert.NotNull(roundTripped.SingBox);
        Assert.NotNull(roundTripped.Monitoring);
        Assert.NotNull(roundTripped.Update);
        Assert.NotNull(roundTripped.ProfileSources);
        Assert.NotNull(roundTripped.Vless.Reality);
        Assert.NotNull(roundTripped.Vless.Tls);
        Assert.NotNull(roundTripped.Vless.Transport);
        Assert.NotNull(roundTripped.Vless.Servers);
        Assert.NotNull(roundTripped.Tun.RouteExcludeAddress);
        Assert.NotNull(roundTripped.App.CustomConfigs);
        Assert.NotNull(roundTripped.App.SubscriptionServers);
        Assert.NotNull(roundTripped.App.Subscriptions);
        Assert.NotNull(roundTripped.App.CustomDirectRules);
        Assert.NotNull(roundTripped.App.CustomRules);
        Assert.NotNull(roundTripped.App.UserFreeSources);
        Assert.NotNull(roundTripped.App.RoutingAppsInclude);
        Assert.NotNull(roundTripped.App.RoutingAppsExclude);
        Assert.NotNull(roundTripped.CustomApps);
        Assert.NotNull(roundTripped.CustomGroupApps);
        Assert.NotNull(roundTripped.CustomCategories);
        Assert.NotNull(roundTripped.ExcludedApps);
        Assert.NotNull(roundTripped.EmergencyChannel);
        Assert.NotNull(roundTripped.EmergencyChannel.Configs);

        // Spot-check scalar default values to pin that none of the
        // [YamlMember(Alias="...")] mappings drifted.
        Assert.Equal(AppSettings.CurrentSchemaVersion, roundTripped.SchemaVersion);
        Assert.Equal("info", roundTripped.App.LogLevel);
        Assert.Equal("split", roundTripped.App.RoutingMode);
        Assert.Equal("include", roundTripped.App.RoutingAppsMode);
        Assert.Equal("light", roundTripped.App.Theme);
        Assert.Equal("advanced", roundTripped.App.UiMode);
        Assert.Equal("generated", roundTripped.App.ConfigMode);
        Assert.Equal("VPNRouter-TUN", roundTripped.Tun.InterfaceName);
        Assert.Equal(9000, roundTripped.Tun.Mtu);
        Assert.Equal("ipv4_only", roundTripped.Dns.Strategy);
        Assert.Equal(30, roundTripped.Monitoring.HealthCheckInterval);
        Assert.Equal("PavelLizunov/VPNRouter", roundTripped.Update.GitHubRepo);
        Assert.True(roundTripped.Update.AutoCheck);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 2. Populated round-trip — exercise every supported YAML node kind:
    //    scalar string, scalar int, scalar bool, nested object, list,
    //    dictionary of strings, dictionary of list-of-strings, optional
    //    DateTimeOffset, multiple list entries, mixed-type collections.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Populated_RoundTrip_PreservesEveryNestedFieldKind()
    {
        var original = new AppSettings
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            ActiveProfile = "Test_Profile",
            App = new AppConfig
            {
                LogLevel = "debug",
                Theme = "dark",
                Language = "ru",
                UiMode = "simple",
                ConfigMode = "subscribe",
                RoutingMode = "full",
                RoutingAppsMode = "exclude",
                BypassRussianTraffic = false,
                BlockAds = true,
                StrictMode = true,
                StrictDns = true,
                ZapretEnabled = true,
                ZapretStrategy = "fake+multisplit",
                TgProxyEnabled = true,
                TgProxyPort = 9999,
                TgProxySecret = "deadbeef0000111122223333deadbeef",
                AutostartVpn = true,
                AutostartZapret = true,
                AutostartTgProxy = true,
                AutostartUi = true,
                FlushDnsOnStart = false,
                RoutingAppsInclude = new List<string> { "chrome.exe", "firefox.exe" },
                RoutingAppsExclude = new List<string> { "telegram.exe" },
                CustomConfigs = new List<CustomConfigEntry>
                {
                    new() { Name = "brat-pc", Path = @"C:\custom\brat-pc.json" },
                    new() { Name = "work", Path = @"C:\custom\work.json" }
                },
                ActiveCustomConfig = "brat-pc",
                SubscriptionUrl = "https://example.com/api/v1/subscription",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Id = "sub-1",
                        Name = "Primary",
                        Url = "https://example.com/sub1",
                        Enabled = true,
                        LastServerCount = 12,
                        LastRefreshedAt = new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero),
                        Servers = new List<VlessServerEntry>
                        {
                            new()
                            {
                                Name = "main-tcp",
                                Server = "1.2.3.4",
                                Port = 443,
                                Uuid = "11111111-1111-1111-1111-111111111111",
                                Protocol = "vless",
                                Flow = "xtls-rprx-vision"
                            }
                        }
                    }
                },
                CustomRules = new List<CustomRule>
                {
                    new() { Action = "block", Type = "domain_suffix", Value = ".ads.example.com", Comment = "ad blocker", Enabled = true },
                    new() { Action = "direct", Type = "geosite", Value = "ru", Enabled = false }
                },
                CustomRulesPriority = "custom_first",
                UserFreeSources = new List<UserFreeSource>
                {
                    new() { Name = "private-sub", Url = "https://private.example.com", Enabled = true, AddedAt = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc) }
                },
                PlaceholderPruneCount = 3,
                PlaceholderPruneAtUtc_Str = "2026-05-17T08:30:00Z"
            },
            Vless = new VlessConfig
            {
                Server = "vpn.example.com",
                Port = 8443,
                Uuid = "22222222-2222-2222-2222-222222222222",
                Flow = "xtls-rprx-vision",
                Security = "reality",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    ServerName = "cloudflare.com",
                    Fingerprint = "chrome",
                    PublicKey = "REPLACE_TEST_REALITY_KEY",
                    ShortId = "deadbeef"
                },
                Tls = new VlessTlsConfig
                {
                    Enabled = false,
                    ServerName = "other.example.com",
                    Insecure = true,
                    Fingerprint = "firefox",
                    Alpn = "h2,http/1.1"
                },
                Transport = new VlessTransportConfig
                {
                    Type = "ws",
                    Path = "/proxy",
                    Headers = new Dictionary<string, string>
                    {
                        ["Host"] = "cdn.example.com",
                        ["User-Agent"] = "Mozilla/5.0"
                    }
                },
                Servers = new List<VlessServerEntry>
                {
                    new() { Name = "backup", Server = "5.6.7.8", Port = 443, Uuid = "33333333-3333-3333-3333-333333333333" }
                },
                ActiveServer = "backup"
            },
            Tun = new TunSettings
            {
                InterfaceName = "Test-TUN",
                Ipv4Address = "10.20.30.1/24",
                Ipv6Enabled = true,
                Mtu = 1500,
                AutoRoute = false,
                StrictRoute = true,
                RouteExcludeAddress = new List<string> { "10.9.1.0/24", "192.168.100.0/24" }
            },
            CustomApps = new List<string> { "spotify.exe", "slack.exe" },
            CustomGroupApps = new Dictionary<string, List<string>>
            {
                ["Browsers"] = new() { "chrome.exe", "firefox.exe" },
                ["Messengers"] = new() { "discord.exe", "telegram.exe", "slack.exe" }
            },
            CustomCategories = new List<CustomCategory>
            {
                new() { Name = "Work", Apps = new List<string> { "outlook.exe", "teams.exe" }, Enabled = true }
            },
            ExcludedApps = new List<string> { "firefox.exe" },
            Update = new UpdateSettings { GitHubRepo = "Test/Repo", AutoCheck = false, Channel = "experimental" },
            EmergencyChannel = new EmergencyChannelSettings
            {
                Enabled = true,
                WgturnUrl = "wgturn://example",
                VkLink = "https://vk.com/call/123",
                LastVkLink = "https://vk.com/call/456",
                ActiveConfig = "Operator-A",
                Configs = new List<WgturnEntry>
                {
                    new() { Name = "Operator-A", Url = "wgturn://op-a", AddedAt = new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero) }
                }
            }
        };

        var path = TempYamlPath();
        SettingsLoader.Save(original, path);
        var yaml = File.ReadAllText(path);
        var roundTripped = SettingsLoader.Parse(yaml);

        // ── App (scalar everything) ──
        Assert.Equal(original.SchemaVersion, roundTripped.SchemaVersion);
        Assert.Equal(original.ActiveProfile, roundTripped.ActiveProfile);
        Assert.Equal(original.App.LogLevel, roundTripped.App.LogLevel);
        Assert.Equal(original.App.Theme, roundTripped.App.Theme);
        Assert.Equal(original.App.Language, roundTripped.App.Language);
        Assert.Equal(original.App.UiMode, roundTripped.App.UiMode);
        Assert.Equal(original.App.ConfigMode, roundTripped.App.ConfigMode);
        Assert.Equal(original.App.RoutingMode, roundTripped.App.RoutingMode);
        Assert.Equal(original.App.RoutingAppsMode, roundTripped.App.RoutingAppsMode);
        Assert.Equal(original.App.BypassRussianTraffic, roundTripped.App.BypassRussianTraffic);
        Assert.Equal(original.App.BlockAds, roundTripped.App.BlockAds);
        Assert.Equal(original.App.StrictMode, roundTripped.App.StrictMode);
        Assert.Equal(original.App.StrictDns, roundTripped.App.StrictDns);
        Assert.Equal(original.App.ZapretEnabled, roundTripped.App.ZapretEnabled);
        Assert.Equal(original.App.ZapretStrategy, roundTripped.App.ZapretStrategy);
        Assert.Equal(original.App.TgProxyEnabled, roundTripped.App.TgProxyEnabled);
        Assert.Equal(original.App.TgProxyPort, roundTripped.App.TgProxyPort);
        Assert.Equal(original.App.TgProxySecret, roundTripped.App.TgProxySecret);
        Assert.Equal(original.App.AutostartVpn, roundTripped.App.AutostartVpn);
        Assert.Equal(original.App.FlushDnsOnStart, roundTripped.App.FlushDnsOnStart);
        Assert.Equal(original.App.PlaceholderPruneCount, roundTripped.App.PlaceholderPruneCount);
        Assert.Equal(original.App.PlaceholderPruneAtUtc_Str, roundTripped.App.PlaceholderPruneAtUtc_Str);

        // ── List<string> ──
        Assert.Equal(original.App.RoutingAppsInclude, roundTripped.App.RoutingAppsInclude);
        Assert.Equal(original.App.RoutingAppsExclude, roundTripped.App.RoutingAppsExclude);
        Assert.Equal(original.CustomApps, roundTripped.CustomApps);
        Assert.Equal(original.ExcludedApps, roundTripped.ExcludedApps);

        // ── List<DTO> ──
        Assert.Equal(original.App.CustomConfigs.Count, roundTripped.App.CustomConfigs.Count);
        Assert.Equal(original.App.CustomConfigs[0].Name, roundTripped.App.CustomConfigs[0].Name);
        Assert.Equal(original.App.CustomConfigs[0].Path, roundTripped.App.CustomConfigs[0].Path);
        Assert.Equal(original.App.CustomConfigs[1].Name, roundTripped.App.CustomConfigs[1].Name);

        // ── List<DTO> with nested DateTimeOffset? ──
        Assert.Single(roundTripped.App.Subscriptions);
        var sub = roundTripped.App.Subscriptions[0];
        Assert.Equal("sub-1", sub.Id);
        Assert.Equal("Primary", sub.Name);
        Assert.Equal("https://example.com/sub1", sub.Url);
        Assert.True(sub.Enabled);
        Assert.Equal(12, sub.LastServerCount);
        Assert.NotNull(sub.LastRefreshedAt);
        // DateTimeOffset round-trip — equality may not be exact due to formatting,
        // but year/month/day/hour/minute/second should match.
        Assert.Equal(original.App.Subscriptions[0].LastRefreshedAt!.Value.UtcDateTime,
                     sub.LastRefreshedAt!.Value.UtcDateTime);
        Assert.Single(sub.Servers);
        Assert.Equal("main-tcp", sub.Servers[0].Name);
        Assert.Equal("1.2.3.4", sub.Servers[0].Server);
        Assert.Equal(443, sub.Servers[0].Port);

        // ── CustomRules / multi-rule list ──
        Assert.Equal(2, roundTripped.App.CustomRules.Count);
        Assert.Equal("block", roundTripped.App.CustomRules[0].Action);
        Assert.Equal(".ads.example.com", roundTripped.App.CustomRules[0].Value);
        Assert.True(roundTripped.App.CustomRules[0].Enabled);
        Assert.Equal("direct", roundTripped.App.CustomRules[1].Action);
        Assert.False(roundTripped.App.CustomRules[1].Enabled);
        Assert.Equal("custom_first", roundTripped.App.CustomRulesPriority);

        // ── Vless ──
        Assert.Equal("vpn.example.com", roundTripped.Vless.Server);
        Assert.Equal(8443, roundTripped.Vless.Port);
        Assert.Equal("xtls-rprx-vision", roundTripped.Vless.Flow);
        Assert.Equal("reality", roundTripped.Vless.Security);
        Assert.True(roundTripped.Vless.Reality.Enabled);
        Assert.Equal("cloudflare.com", roundTripped.Vless.Reality.ServerName);
        Assert.Equal("chrome", roundTripped.Vless.Reality.Fingerprint);
        Assert.Equal("REPLACE_TEST_REALITY_KEY", roundTripped.Vless.Reality.PublicKey);
        Assert.Equal("deadbeef", roundTripped.Vless.Reality.ShortId);
        Assert.False(roundTripped.Vless.Tls.Enabled);
        Assert.Equal("other.example.com", roundTripped.Vless.Tls.ServerName);
        Assert.True(roundTripped.Vless.Tls.Insecure);
        Assert.Equal("firefox", roundTripped.Vless.Tls.Fingerprint);
        Assert.Equal("h2,http/1.1", roundTripped.Vless.Tls.Alpn);
        Assert.Equal("ws", roundTripped.Vless.Transport.Type);
        Assert.Equal("/proxy", roundTripped.Vless.Transport.Path);

        // ── Dictionary<string, string> ──
        Assert.Equal(2, roundTripped.Vless.Transport.Headers.Count);
        Assert.Equal("cdn.example.com", roundTripped.Vless.Transport.Headers["Host"]);
        Assert.Equal("Mozilla/5.0", roundTripped.Vless.Transport.Headers["User-Agent"]);

        Assert.Single(roundTripped.Vless.Servers);
        Assert.Equal("backup", roundTripped.Vless.Servers[0].Name);
        Assert.Equal("backup", roundTripped.Vless.ActiveServer);

        // ── Tun + List<string> ──
        Assert.Equal("Test-TUN", roundTripped.Tun.InterfaceName);
        Assert.Equal("10.20.30.1/24", roundTripped.Tun.Ipv4Address);
        Assert.True(roundTripped.Tun.Ipv6Enabled);
        Assert.Equal(1500, roundTripped.Tun.Mtu);
        Assert.False(roundTripped.Tun.AutoRoute);
        Assert.True(roundTripped.Tun.StrictRoute);
        Assert.Equal(2, roundTripped.Tun.RouteExcludeAddress.Count);
        Assert.Contains("10.9.1.0/24", roundTripped.Tun.RouteExcludeAddress);

        // ── Dictionary<string, List<string>> — the analyzer crashes if this
        //    is explicitly registered with [YamlSerializable]; transitive
        //    discovery from the AppSettings.CustomGroupApps property is the
        //    actual path used. This assertion pins that path. ──
        Assert.Equal(2, roundTripped.CustomGroupApps.Count);
        Assert.True(roundTripped.CustomGroupApps.ContainsKey("Browsers"));
        Assert.Equal(2, roundTripped.CustomGroupApps["Browsers"].Count);
        Assert.Contains("chrome.exe", roundTripped.CustomGroupApps["Browsers"]);
        Assert.Equal(3, roundTripped.CustomGroupApps["Messengers"].Count);

        // ── CustomCategories ──
        Assert.Single(roundTripped.CustomCategories);
        Assert.Equal("Work", roundTripped.CustomCategories[0].Name);
        Assert.Equal(2, roundTripped.CustomCategories[0].Apps.Count);
        Assert.True(roundTripped.CustomCategories[0].Enabled);

        // ── Update ──
        Assert.Equal("Test/Repo", roundTripped.Update.GitHubRepo);
        Assert.False(roundTripped.Update.AutoCheck);
        Assert.Equal("experimental", roundTripped.Update.Channel);

        // ── EmergencyChannel ──
        Assert.True(roundTripped.EmergencyChannel.Enabled);
        Assert.Equal("wgturn://example", roundTripped.EmergencyChannel.WgturnUrl);
        Assert.Equal("https://vk.com/call/123", roundTripped.EmergencyChannel.VkLink);
        Assert.Equal("https://vk.com/call/456", roundTripped.EmergencyChannel.LastVkLink);
        Assert.Equal("Operator-A", roundTripped.EmergencyChannel.ActiveConfig);
        Assert.Single(roundTripped.EmergencyChannel.Configs);
        Assert.Equal("Operator-A", roundTripped.EmergencyChannel.Configs[0].Name);
        Assert.Equal("wgturn://op-a", roundTripped.EmergencyChannel.Configs[0].Url);

        // ── UserFreeSources + DateTime ──
        Assert.Single(roundTripped.App.UserFreeSources);
        Assert.Equal("private-sub", roundTripped.App.UserFreeSources[0].Name);
        Assert.Equal("https://private.example.com", roundTripped.App.UserFreeSources[0].Url);
        Assert.True(roundTripped.App.UserFreeSources[0].Enabled);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 3. Wire-format pin — load a hand-crafted YAML fixture, verify that
    //    every [YamlMember(Alias = "snake_case")] mapping is honoured.
    //    Without this test, an analyzer regression that ignored the alias
    //    (e.g. silently fell back to CLR PascalCase) would slip past the
    //    round-trip tests above (which always go through the same
    //    StaticSerializerBuilder on the way in AND out).
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void WireFormat_SnakeCaseAliases_HonoredByStaticDeserializer()
    {
        const string yaml = @"schema_version: 4
app:
  log_level: warning
  routing_mode: full
  routing_apps_mode: exclude
  config_mode: custom
  active_custom_config: brat-pc
  bypass_russian_traffic: false
  block_ads: true
  strict_mode: true
  autostart_vpn: true
  custom_configs:
    - name: brat-pc
      path: ""C:\\custom\\brat-pc.json""
  subscriptions:
    - id: abc123
      name: Test
      url: https://test.example
      enabled: true
      last_server_count: 7
      servers: []
  custom_rules:
    - action: block
      type: domain_suffix
      value: .blocked.example
      comment: pin test
      enabled: true
  custom_rules_priority: custom_first
profile_sources:
  - type: local
    path: ""C:\\test\\profile.json""
    update_interval: 7200
active_profile: Test_Profile
vless:
  server: vpn.test.example
  port: 8443
  uuid: 11111111-1111-1111-1111-111111111111
  flow: xtls-rprx-vision
  security: reality
  reality:
    enabled: true
    server_name: cloudflare.com
    fingerprint: chrome
    public_key: TEST_KEY
    short_id: deadbeef
  transport:
    type: ws
    path: /test
    headers:
      Host: cdn.test.example
tun:
  interface_name: Wire-TUN
  ipv4_address: 10.0.0.1/24
  ipv6_enabled: true
  mtu: 1500
  auto_route: false
  strict_route: true
  route_exclude_address:
    - 192.168.1.0/24
dns:
  strategy: ipv4_only
  vpn_dns: https://9.9.9.9/dns-query
  local_dns: local
singbox:
  executable_path: ""C:\\singbox.exe""
  auto_download: false
monitoring:
  health_check_interval: 5
  restart_on_failure: false
  max_restart_attempts: 2
  process_scan_interval: 15
custom_apps:
  - spotify.exe
custom_group_apps:
  Browsers:
    - chrome.exe
    - firefox.exe
update:
  github_repo: Test/Repo
  auto_check: false
  channel: experimental
";

        var settings = SettingsLoader.Parse(yaml);

        // schema_version alias
        Assert.Equal(4, settings.SchemaVersion);

        // app.* aliases
        Assert.Equal("warning", settings.App.LogLevel);
        Assert.Equal("full", settings.App.RoutingMode);
        Assert.Equal("exclude", settings.App.RoutingAppsMode);
        Assert.Equal("custom", settings.App.ConfigMode);
        Assert.Equal("brat-pc", settings.App.ActiveCustomConfig);
        Assert.False(settings.App.BypassRussianTraffic);
        Assert.True(settings.App.BlockAds);
        Assert.True(settings.App.StrictMode);
        Assert.True(settings.App.AutostartVpn);
        Assert.Equal("custom_first", settings.App.CustomRulesPriority);

        // custom_configs (alias on inner type)
        Assert.Single(settings.App.CustomConfigs);
        Assert.Equal("brat-pc", settings.App.CustomConfigs[0].Name);

        // subscriptions (alias on inner type, last_server_count snake_case)
        Assert.Single(settings.App.Subscriptions);
        var sub = settings.App.Subscriptions[0];
        Assert.Equal("abc123", sub.Id);
        Assert.Equal(7, sub.LastServerCount);

        // custom_rules (alias on inner action/type/value)
        Assert.Single(settings.App.CustomRules);
        Assert.Equal("block", settings.App.CustomRules[0].Action);
        Assert.Equal("domain_suffix", settings.App.CustomRules[0].Type);
        Assert.Equal(".blocked.example", settings.App.CustomRules[0].Value);

        // profile_sources (alias on update_interval int)
        Assert.Single(settings.ProfileSources);
        Assert.Equal("local", settings.ProfileSources[0].Type);
        Assert.Equal(7200, settings.ProfileSources[0].UpdateInterval);

        // active_profile
        Assert.Equal("Test_Profile", settings.ActiveProfile);

        // vless.* (server, uuid, reality.public_key, reality.short_id,
        // reality.server_name, transport.type, transport.path, transport.headers)
        Assert.Equal("vpn.test.example", settings.Vless.Server);
        Assert.Equal("11111111-1111-1111-1111-111111111111", settings.Vless.Uuid);
        Assert.True(settings.Vless.Reality.Enabled);
        Assert.Equal("cloudflare.com", settings.Vless.Reality.ServerName);
        Assert.Equal("TEST_KEY", settings.Vless.Reality.PublicKey);
        Assert.Equal("deadbeef", settings.Vless.Reality.ShortId);
        Assert.Equal("ws", settings.Vless.Transport.Type);
        Assert.Equal("/test", settings.Vless.Transport.Path);
        Assert.Single(settings.Vless.Transport.Headers);
        Assert.Equal("cdn.test.example", settings.Vless.Transport.Headers["Host"]);

        // tun.* (interface_name, ipv4_address, ipv6_enabled, auto_route,
        // strict_route, route_exclude_address)
        Assert.Equal("Wire-TUN", settings.Tun.InterfaceName);
        Assert.Equal("10.0.0.1/24", settings.Tun.Ipv4Address);
        Assert.True(settings.Tun.Ipv6Enabled);
        Assert.Equal(1500, settings.Tun.Mtu);
        Assert.False(settings.Tun.AutoRoute);
        Assert.True(settings.Tun.StrictRoute);
        Assert.Single(settings.Tun.RouteExcludeAddress);
        Assert.Equal("192.168.1.0/24", settings.Tun.RouteExcludeAddress[0]);

        // dns.* (vpn_dns, local_dns)
        Assert.Equal("ipv4_only", settings.Dns.Strategy);
        Assert.Equal("https://9.9.9.9/dns-query", settings.Dns.VpnDns);
        Assert.Equal("local", settings.Dns.LocalDns);

        // singbox.* (executable_path, auto_download)
        Assert.Equal(@"C:\singbox.exe", settings.SingBox.ExecutablePath);
        Assert.False(settings.SingBox.AutoDownload);

        // monitoring.* (health_check_interval, restart_on_failure,
        // max_restart_attempts, process_scan_interval)
        Assert.Equal(5, settings.Monitoring.HealthCheckInterval);
        Assert.False(settings.Monitoring.RestartOnFailure);
        Assert.Equal(2, settings.Monitoring.MaxRestartAttempts);
        Assert.Equal(15, settings.Monitoring.ProcessScanInterval);

        // custom_apps + custom_group_apps (List<string> + Dictionary<string, List<string>>)
        Assert.Single(settings.CustomApps);
        Assert.Equal("spotify.exe", settings.CustomApps[0]);
        Assert.True(settings.CustomGroupApps.ContainsKey("Browsers"));
        Assert.Equal(2, settings.CustomGroupApps["Browsers"].Count);
        Assert.Contains("chrome.exe", settings.CustomGroupApps["Browsers"]);

        // update.* (github_repo, auto_check, channel)
        Assert.Equal("Test/Repo", settings.Update.GitHubRepo);
        Assert.False(settings.Update.AutoCheck);
        Assert.Equal("experimental", settings.Update.Channel);
    }
}
