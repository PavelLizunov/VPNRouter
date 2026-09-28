using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

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
