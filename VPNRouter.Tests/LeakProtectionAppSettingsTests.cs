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
}
