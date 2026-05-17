using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
public class CustomRulesV2_30_MigrationTests
{
    [Fact]
    public void Migration_v1_to_v2_ConvertsLegacyDirectRules()
    {
        var settings = new VPNRouter.Core.Models.AppSettings { SchemaVersion = 1 };
        settings.App.CustomDirectRules = new List<VPNRouter.Core.Models.CustomDirectRule>
        {
            new() { Type = "ip_cidr", Value = "10.0.0.0/8", Comment = "LAN", Enabled = true },
            new() { Type = "domain_suffix", Value = ".internal", Enabled = false },
        };

        var migrated = VPNRouter.Core.Services.SettingsMigrator.Migrate(settings, 1, 2);

        Assert.Equal(2, migrated.App.CustomRules.Count);
        Assert.All(migrated.App.CustomRules, r => Assert.Equal("direct", r.Action));
        Assert.Equal("ip_cidr", migrated.App.CustomRules[0].Type);
        Assert.Equal("LAN", migrated.App.CustomRules[0].Comment);
        Assert.False(migrated.App.CustomRules[1].Enabled);
        Assert.Empty(migrated.App.CustomDirectRules);
    }

    [Fact]
    public void Migration_v1_to_v2_Idempotent_WhenCustomRulesPopulated()
    {
        var settings = new VPNRouter.Core.Models.AppSettings { SchemaVersion = 1 };
        settings.App.CustomRules.Add(new VPNRouter.Core.Models.CustomRule
        {
            Action = "proxy", Type = "domain", Value = "manual.example", Enabled = true,
        });
        settings.App.CustomDirectRules.Add(new VPNRouter.Core.Models.CustomDirectRule
        {
            Type = "ip_cidr", Value = "10.0.0.0/8", Enabled = true,
        });

        var migrated = VPNRouter.Core.Services.SettingsMigrator.Migrate(settings, 1, 2);

        Assert.Single(migrated.App.CustomRules);
        Assert.Equal("proxy", migrated.App.CustomRules[0].Action);
        Assert.Single(migrated.App.CustomDirectRules);
    }

    [Fact]
    public void Migration_v1_to_v2_NoLegacyData_NoOp()
    {
        var settings = new VPNRouter.Core.Models.AppSettings { SchemaVersion = 1 };
        var migrated = VPNRouter.Core.Services.SettingsMigrator.Migrate(settings, 1, 2);
        Assert.Empty(migrated.App.CustomRules);
        Assert.Empty(migrated.App.CustomDirectRules);
        Assert.Equal(2, migrated.SchemaVersion);
    }
}
