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
    public void Subscribe_EnabledSubButNoServersAndNoFallback_Fails()
    {
        // F-12 critical scenario: ConfigMode=subscribe with an enabled sub
        // but no servers fetched yet AND no manual VLESS fallback. The
        // engine would otherwise generate a config with empty outbounds.
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

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("Refresh", StringComparison.OrdinalIgnoreCase));
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
}
