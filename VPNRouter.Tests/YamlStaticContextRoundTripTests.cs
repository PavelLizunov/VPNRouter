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

    [Fact]
    public void LegacyYaml_WithoutConnectionIntent_LoadsGeneralIntent()
    {
        var settings = SettingsLoader.Parse("""
schema_version: 8
app:
  log_level: info
  routing_mode: split
""");

        Assert.Equal(ConnectionIntent.General, settings.App.ConnectionIntent);
    }

    [Fact]
    public void WireFormat_SnakeCaseAliases_HonoredByStaticDeserializer()
    {
        const string yaml = @"schema_version: 5
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

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);

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

        Assert.Single(settings.App.CustomConfigs);
        Assert.Equal("brat-pc", settings.App.CustomConfigs[0].Name);

        Assert.Single(settings.App.Subscriptions);
        var sub = settings.App.Subscriptions[0];
        Assert.Equal("abc123", sub.Id);
        Assert.Equal(7, sub.LastServerCount);

        Assert.Single(settings.App.CustomRules);
        Assert.Equal("block", settings.App.CustomRules[0].Action);
        Assert.Equal("domain_suffix", settings.App.CustomRules[0].Type);
        Assert.Equal(".blocked.example", settings.App.CustomRules[0].Value);

        Assert.Single(settings.ProfileSources);
        Assert.Equal("local", settings.ProfileSources[0].Type);
        Assert.Equal(7200, settings.ProfileSources[0].UpdateInterval);

        Assert.Equal("Test_Profile", settings.ActiveProfile);

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

        Assert.Equal("Wire-TUN", settings.Tun.InterfaceName);
        Assert.Equal("10.0.0.1/24", settings.Tun.Ipv4Address);
        Assert.True(settings.Tun.Ipv6Enabled);
        Assert.Equal(1280, settings.Tun.Mtu);
        Assert.False(settings.Tun.AutoRoute);
        Assert.True(settings.Tun.StrictRoute);
        Assert.Single(settings.Tun.RouteExcludeAddress);
        Assert.Equal("192.168.1.0/24", settings.Tun.RouteExcludeAddress[0]);

        Assert.Equal("ipv4_only", settings.Dns.Strategy);
        Assert.Equal("https://9.9.9.9/dns-query", settings.Dns.VpnDns);
        Assert.Equal("local", settings.Dns.LocalDns);

        Assert.Equal(@"C:\singbox.exe", settings.SingBox.ExecutablePath);
        Assert.False(settings.SingBox.AutoDownload);

        Assert.Equal(5, settings.Monitoring.HealthCheckInterval);
        Assert.False(settings.Monitoring.RestartOnFailure);
        Assert.Equal(2, settings.Monitoring.MaxRestartAttempts);
        Assert.Equal(15, settings.Monitoring.ProcessScanInterval);

        Assert.Single(settings.CustomApps);
        Assert.Equal("spotify.exe", settings.CustomApps[0]);
        Assert.True(settings.CustomGroupApps.ContainsKey("Browsers"));
        Assert.Equal(2, settings.CustomGroupApps["Browsers"].Count);
        Assert.Contains("chrome.exe", settings.CustomGroupApps["Browsers"]);

        Assert.Equal("Test/Repo", settings.Update.GitHubRepo);
        Assert.False(settings.Update.AutoCheck);
        Assert.Equal("experimental", settings.Update.Channel);
    }

    [Fact]
    public void WireFormat_RemovedRouteGamesDirectKey_IsIgnored()
    {
        const string yaml = @"schema_version: 5
app:
  routing_mode: full
  route_games_direct: true
vless:
  server: vpn.test.example
  port: 443
  uuid: 11111111-1111-1111-1111-111111111111
";

        var settings = SettingsLoader.Parse(yaml);

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal("full", settings.App.RoutingMode);
    }
}
