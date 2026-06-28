using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// LeakProtection.ValidateAppSettings (F-12 / parity audit P0, 2026-05-09)
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// F-12 (parity audit P0, 2026-05-09): defense-in-depth backstop for silent
/// ConfigMode flips. <see cref="LeakProtection.ValidateAppSettings"/> sits
/// at the model level and is run by <see cref="VpnEngine.StartAsync"/> +
/// <see cref="VpnEngine.ApplyAsync"/> before any sing-box config generation.
///
/// <para>If a future UI change re-introduces a silent <c>ConfigMode</c>
/// flip without populating <c>Subscriptions</c> / <c>Vless.Servers</c>,
/// the engine throws here instead of generating a leaky sing-box config
/// with empty proxy outbounds. Same failure class as v2.28.2 silent leak
/// — see <c>plans/session-night-shift-2026-04-25.md</c>.</para>
/// </summary>
public class LeakProtectionAppSettingsTests
{
    [Fact]
    public void Subscribe_WithEnabledSubAndServers_Passes()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "primary", Url = "https://example.com/sub", Enabled = true,
                        Servers = new List<VlessServerEntry>
                        {
                            new() { Server = "1.2.3.4", Port = 443, Uuid = "u" }
                        }
                    }
                }
            }
        };

        var result = LeakProtection.ValidateAppSettings(settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Generated_AwgActiveServer_EmptyServerSnapshot_Passes()
    {
        // v2.45.0-r2 (AWG live-test regression): a generated-mode config whose
        // active server is AmneziaWG — with Vless.Servers empty at this pre-resolve
        // guard — must NOT be rejected "no VLESS server configured". The
        // VlessServersResolver populates the pool one step later in the pipeline.
        var settings = new AppSettings
        {
            App = new AppConfig { ConfigMode = "generated" },
            Vless = new VlessConfig
            {
                ActiveServer = "main-brat",
                Servers = new List<VlessServerEntry>(),
            },
        };

        var result = LeakProtection.ValidateAppSettings(settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Generated_NothingConfigured_NoActiveServer_StillFails()
    {
        // The genuine empty case stays caught: no servers, no legacy scalar, no
        // enabled sub with servers, AND no active-server selection.
        var settings = new AppSettings
        {
            App = new AppConfig { ConfigMode = "generated", ActiveSubscriptionServer = "" },
            Vless = new VlessConfig { ActiveServer = "", Servers = new List<VlessServerEntry>() },
        };

        var result = LeakProtection.ValidateAppSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no VLESS server", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Subscribe_WithoutAnySubscriptions_Fails()
    {
        // The F-12 silent-flip scenario: ConfigMode says "subscribe" but
        // there are no subscription entries at all. Pre-fix this would
        // generate a config with empty proxy outbounds and the engine
        // would silently fall through to direct.
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                Subscriptions = new List<SubscriptionEntry>()
            }
        };

        var result = LeakProtection.ValidateAppSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("subscribe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Subscribe_AllSubsDisabled_Fails()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new() { Name = "off", Url = "https://example.com/sub", Enabled = false }
                }
            }
        };

        var result = LeakProtection.ValidateAppSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("disabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Subscribe_EnabledSubButNoServers_DefersToResolverFallback()
    {
        // BR-1 (brat 2026-05-19) — softened F-12: ConfigMode=subscribe
        // with an enabled sub that has no servers yet is no longer a
        // pre-generation invariant violation. VlessServersResolver runs
        // RIGHT AFTER this validation in StartupPipeline.ExecuteAsync
        // (line ~518) and already emits a clear warning + falls back to
        // manual Vless.Servers / Vless.Server, OR throws on truly empty
        // aggregate via the ConfigGenerator empty-servers hard guard.
        //
        // Before BR-1: this test asserted IsValid=false with a "Refresh"
        // hint. brat's r5 logs at 21:39:47.920 showed F-12 firing for this
        // exact case while v2.32.2 successfully connected by falling
        // through to the resolver's manual-fallback log line:
        //   [WRN] [VlessServersResolver] config_mode=subscribe but no
        //   enabled subscription has servers. Falling back to manually-
        //   configured Vless.Servers / Vless.Server.
        //
        // The defense-in-depth net was preempting the resolver's own
        // intended behaviour. Now we trust the resolver to handle it.
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "fresh-unfetched",
                        Url = "https://example.com/sub",
                        Enabled = true,
                        Servers = new List<VlessServerEntry>()
                    }
                }
            }
        };

        var result = LeakProtection.ValidateAppSettings(settings);

        Assert.True(result.IsValid,
            "BR-1: empty-subs + subscribe mode should defer to " +
            "VlessServersResolver fallback, not throw at validation. " +
            "Errors: " + string.Join("; ", result.Errors));
    }

    [Fact]
    public void Subscribe_NoServersButManualFallbackPresent_Passes()
    {
        // If the user has manually added a VLESS server (Vless.Servers),
        // we treat that as a fallback and don't fail the subscribe-mode
        // invariant — the engine can route through the manual entry.
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "fresh", Url = "https://example.com/sub", Enabled = true,
                        Servers = new List<VlessServerEntry>()
                    }
                }
            },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    new() { Server = "5.6.7.8", Port = 443, Uuid = "manual-uuid" }
                }
            }
        };

        var result = LeakProtection.ValidateAppSettings(settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Generated_WithVlessServer_Passes()
    {
        var settings = new AppSettings
        {
            App = new AppConfig { ConfigMode = "generated" },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    new() { Server = "1.2.3.4", Port = 443, Uuid = "u" }
                }
            }
        };

        var result = LeakProtection.ValidateAppSettings(settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Generated_WithoutVlessServerAndNoSubFallback_Fails()
    {
        var settings = new AppSettings
        {
            App = new AppConfig { ConfigMode = "generated" },
            Vless = new VlessConfig { Servers = new List<VlessServerEntry>() }
        };

        var result = LeakProtection.ValidateAppSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("generated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Custom_AlwaysSkipped()
    {
        // Custom mode loads JSON from disk — out of scope for this check.
        var settings = new AppSettings
        {
            App = new AppConfig { ConfigMode = "custom" }
        };

        var result = LeakProtection.ValidateAppSettings(settings);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void NullSettings_Fails()
    {
        var result = LeakProtection.ValidateAppSettings(null!);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Subscribe_BratScenarioTwoSubsBothEmpty_DefersToResolverFallback()
    {
        // BR-1 pin (brat 2026-05-19) — reproduces the exact AppSettings
        // shape that triggered v2.35.0-r5's incorrect F-12 fire at the
        // moment brat clicked "Ignore conflict, retry" after his LAN
        // subscription started returning 0 servers:
        //
        // - configMode = subscribe (his standard mode)
        // - 2 subscriptions, BOTH enabled, BOTH with empty Servers
        //   (the user's primary sub had momentarily-empty servers in
        //   memory due to the same-cycle SaveSettings flow, and the
        //   LAN sub was permanently empty because his self-hosted
        //   endpoint returns "JSON response has no 'config' field")
        // - No manual Vless.Servers list (subscribe mode = empty list)
        // - No legacy Vless.Server scalar (cleared)
        //
        // Pre-BR-1: F-12 fired, user got "ConfigMode=subscribe but no
        // subscription has fetched any servers" and had to roll back.
        // Post-BR-1: validation passes, VlessServersResolver runs next
        // and either falls back (manual entry exists somewhere) or the
        // ConfigGenerator empty-servers hard guard throws with a
        // clearer message ("VLESS servers list is empty").
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "ninitux",
                        Url = "https://ninitux.example/sub",
                        Enabled = true,
                        Servers = new List<VlessServerEntry>()
                    },
                    new()
                    {
                        Name = "lan-self-hosted",
                        Url = "http://192.168.0.236:18402/sub/redacted",
                        Enabled = true,
                        Servers = new List<VlessServerEntry>()
                    }
                }
            }
        };

        var result = LeakProtection.ValidateAppSettings(settings);

        Assert.True(result.IsValid,
            "brat r5 scenario must NOT throw F-12 — resolver " +
            "owns the empty-aggregate fallback. Errors: " +
            string.Join("; ", result.Errors));
    }
}
