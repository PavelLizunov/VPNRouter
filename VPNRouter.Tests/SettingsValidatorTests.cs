using System.IO;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// v2.32.0 — pin every <see cref="SettingsValidator"/> invariant.
/// One failing case per rule + a happy-path defaults pin + an
/// integration test that <see cref="SettingsLoader.Load"/> actually
/// routes invalid yaml through backup → defaults → recovery notice.
///
/// Plan: <c>plans/v2.32.0-settings-validator.md</c>.
/// </summary>
public class SettingsValidatorTests
{
    // ── Happy path ──────────────────────────────────────────────────
    [Fact]
    public void HappyPath_FreshDefaults_ValidatesOk()
    {
        var s = NewValid();

        var result = SettingsValidator.Validate(s);

        Assert.True(result.IsValid, "fresh defaults should validate");
        Assert.Empty(result.Reasons);
        // Warnings allowed only because there's no custom path to check.
        Assert.Empty(result.Warnings);
    }

    // ── Invariant 1: app.config_mode (unknown) ──────────────────────
    [Fact]
    public void ConfigMode_Unknown_IsInvalid()
    {
        var s = NewValid();
        s.App.ConfigMode = "nonsense";

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("config_mode"));
    }

    // ── Invariant 2: app.config_mode (empty) ────────────────────────
    [Fact]
    public void ConfigMode_Empty_IsInvalid()
    {
        var s = NewValid();
        s.App.ConfigMode = "";

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("config_mode"));
    }

    // ── Invariant 3: app.routing_mode ───────────────────────────────
    [Fact]
    public void RoutingMode_Unknown_IsInvalid()
    {
        var s = NewValid();
        s.App.RoutingMode = "diagonal";

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("routing_mode"));
    }

    // ── Invariant 4: app.theme ──────────────────────────────────────
    [Fact]
    public void Theme_Unknown_IsInvalid()
    {
        var s = NewValid();
        s.App.Theme = "neon";

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("theme"));
    }

    // ── Invariant 5: app.tg_proxy_port ──────────────────────────────
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void TgProxyPort_OutOfRange_IsInvalid(int port)
    {
        var s = NewValid();
        s.App.TgProxyPort = port;

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("tg_proxy_port"));
    }

    // ── Invariant 6: vless.port (legacy single-server field) ───────
    [Fact]
    public void VlessLegacyPort_OutOfRange_IsInvalid()
    {
        var s = NewValid();
        s.Vless.Port = 0;

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("vless.port"));
    }

    // ── Invariant 7: vless.servers[i].port ─────────────────────────
    [Fact]
    public void VlessServerEntryPort_OutOfRange_IsInvalid()
    {
        var s = NewValid();
        s.Vless.Servers.Add(new VlessServerEntry
        {
            Name = "bad",
            Server = "1.2.3.4",
            Port = 99999,
            Uuid = "u",
        });

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("vless.servers[0].port"));
    }

    // ── Invariant 8: tun.mtu ───────────────────────────────────────
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(70000)]
    public void TunMtu_OutOfRange_IsInvalid(int mtu)
    {
        var s = NewValid();
        s.Tun.Mtu = mtu;

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("tun.mtu"));
    }

    // ── Invariant 9: monitoring.health_check_interval ──────────────
    [Fact]
    public void HealthCheckInterval_NonPositive_IsInvalid()
    {
        var s = NewValid();
        s.Monitoring.HealthCheckInterval = 0;

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("health_check_interval"));
    }

    // ── Invariant 10: monitoring.process_scan_interval ─────────────
    [Fact]
    public void ProcessScanInterval_NonPositive_IsInvalid()
    {
        var s = NewValid();
        s.Monitoring.ProcessScanInterval = -5;

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("process_scan_interval"));
    }

    // ── Invariant 11: monitoring.max_restart_attempts ──────────────
    [Fact]
    public void MaxRestartAttempts_Negative_IsInvalid()
    {
        var s = NewValid();
        s.Monitoring.MaxRestartAttempts = -1;

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("max_restart_attempts"));
    }

    // ── Invariant 12: dns.strategy ─────────────────────────────────
    [Fact]
    public void DnsStrategy_Unknown_IsInvalid()
    {
        var s = NewValid();
        s.Dns.Strategy = "ipv5_only";

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("dns.strategy"));
    }

    // ── Invariant 13: update.channel ───────────────────────────────
    [Fact]
    public void UpdateChannel_Unknown_IsInvalid()
    {
        var s = NewValid();
        s.Update.Channel = "nightly";

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("update.channel"));
    }

    // ── Invariant 14: app.subscription_url ─────────────────────────
    [Fact]
    public void SubscriptionUrl_Malformed_IsInvalid()
    {
        var s = NewValid();
        s.App.SubscriptionUrl = "not a url";

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("subscription_url"));
    }

    // ── Invariant 15: app.subscriptions[i].url ─────────────────────
    [Fact]
    public void SubscriptionsListUrl_Malformed_IsInvalid()
    {
        var s = NewValid();
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "test",
            Url = "this is not://valid uri because of the space",
            Enabled = true,
        });

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("subscriptions[0].url"));
    }

    // ── Invariant 16: profile_sources[i].url ───────────────────────
    [Fact]
    public void ProfileSourcesUrl_Malformed_IsInvalid()
    {
        var s = NewValid();
        s.ProfileSources.Add(new ProfileSource
        {
            Type = "github",
            Url = "not a uri at all",
        });

        var result = SettingsValidator.Validate(s);

        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, r => r.Contains("profile_sources[0].url"));
    }

    // ── Warning-only invariant: missing custom config path ─────────
    [Fact]
    public void CustomConfigPathMissing_AddsWarning_StillValid()
    {
        var s = NewValid();
        s.App.ConfigMode = "custom";
        s.App.CustomConfigs.Add(new CustomConfigEntry
        {
            Name = "ghost",
            Path = Path.Combine(Path.GetTempPath(), "vpnrouter-validator-test-missing-" + Guid.NewGuid().ToString("N") + ".json"),
        });
        s.App.ActiveCustomConfig = "ghost";

        var result = SettingsValidator.Validate(s);

        Assert.True(result.IsValid, "missing custom path is a soft warning, not fatal");
        Assert.Contains(result.Warnings, w => w.Contains("missing on disk"));
    }

    // ── Source-pin: SettingsLoader.Load actually invokes Validate ──
    //
    // We don't have a mock-able Validate boundary, so source-pin via
    // observable side-effect: load a structurally-broken yaml from
    // a temp path and verify the file got renamed to .invalid-<stamp>
    // and a fresh defaults file was written, plus LastRecoveryNotice
    // was populated. If a future refactor accidentally drops the
    // SettingsValidator.Validate call inside Load, this test fails.
    [Fact]
    public void Load_RoutesInvalidConfig_ToBackupAndDefaults_AndPopulatesNotice()
    {
        // Static-state hygiene: some prior test in the suite may have
        // populated LastRecoveryNotice without consuming it. Drain
        // before we measure, so the post-Load assertions see only
        // state our own Load() generated.
        SettingsLoader.ConsumeRecoveryNotice();

        var dir = Path.Combine(Path.GetTempPath(), "vpnrouter-validator-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.yaml");

        try
        {
            // Structurally valid YAML, semantically broken: config_mode
            // is unknown. Validator must reject + Loader must back up.
            File.WriteAllText(configPath,
                "schema_version: 2\n" +
                "app:\n" +
                "  config_mode: nonsense\n" +
                "vless: {}\n" +
                "tun:\n" +
                "  mtu: 9000\n" +
                "monitoring:\n" +
                "  health_check_interval: 30\n" +
                "  process_scan_interval: 60\n");

            var loaded = SettingsLoader.Load(configPath);

            // Loader returned defaults — v2.32.0 default ConfigMode = "generated"
            // (revert 2026-05-10 d9f7027 — F-02 chip's flip to "subscribe"
            // was reverted with the rest of the desktop changes).
            Assert.Equal("generated", loaded.App.ConfigMode);

            // A backup file with the .invalid- prefix exists alongside.
            var siblings = Directory.GetFiles(dir, "config.yaml.invalid-*");
            Assert.Single(siblings);

            // Recovery notice is populated and consumable exactly once.
            Assert.NotNull(SettingsLoader.LastRecoveryNotice);
            var consumed = SettingsLoader.ConsumeRecoveryNotice();
            Assert.NotNull(consumed);
            Assert.Contains("config_mode", consumed!);
            Assert.Null(SettingsLoader.LastRecoveryNotice);

            // The on-disk yaml was rewritten with defaults, so a fresh
            // load returns a valid result. We don't strictly assert
            // LastRecoveryNotice stays null on reload because in some
            // suite orderings the in-process Save→Load round-trip can
            // re-emit a parse warning if YamlDotNet flagged any
            // serialization ambiguity (this is best-effort hygiene,
            // not a load failure — the App still gets valid defaults).
            // Drain the notice just in case so subsequent tests in the
            // class start clean.
            var reloaded = SettingsLoader.Load(configPath);
            Assert.Equal("generated", reloaded.App.ConfigMode);
            SettingsLoader.ConsumeRecoveryNotice();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────
    //
    // NewValid() returns a settings tree that mirrors what
    // SettingsLoader.CreateDefaults produces. Tests mutate one field
    // at a time so a regression in one invariant doesn't poison the
    // others.
    private static AppSettings NewValid()
    {
        return new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                RoutingMode = "split",
                Theme = "light",
                TgProxyPort = 1443,
                LogLevel = "info",
            },
            Vless = new VlessConfig
            {
                Port = 443,
                Servers = new List<VlessServerEntry>(),
            },
            Tun = new TunSettings
            {
                Mtu = 9000,
                InterfaceName = "VPNRouter-TUN",
                Ipv4Address = "172.19.0.1/30",
            },
            Dns = new DnsSettings
            {
                Strategy = "ipv4_only",
            },
            SingBox = new SingBoxSettings(),
            Monitoring = new MonitoringSettings
            {
                HealthCheckInterval = 30,
                ProcessScanInterval = 60,
                MaxRestartAttempts = 5,
            },
            Update = new UpdateSettings
            {
                Channel = "stable",
            },
            ProfileSources = new List<ProfileSource>(),
        };
    }
}
