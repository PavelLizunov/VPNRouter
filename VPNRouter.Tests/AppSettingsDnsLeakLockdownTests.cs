using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class AppSettingsDnsLeakLockdownTests
{
    private const string PropertyName = "DnsLeakLockdown";
    private const string YamlAlias = "dns_leak_lockdown";

    [Fact]
    public void NewInstall_DefaultsToFalse()
    {
        var settings = new AppSettings();
        Assert.NotNull(settings.App);

        var prop = GetDnsLeakLockdownProperty();
        var defaultValue = (bool)prop.GetValue(settings.App)!;

        Assert.False(defaultValue,
            "BR-10 (2026-05-20): new installs must default to false. " +
            "User opts in via Settings → Leak Protection if they want " +
            "the firewall block layer. sing-box DNS routing via VLESS:443 " +
            "remains the primary leak protection.");
    }

    [Fact]
    public void Yaml_RoundTrip_PreservesValue_True()
    {
        var yaml = """
            schema_version: 5
            app:
              dns_leak_lockdown: true
            """;

        var settings = SettingsLoader.Parse(yaml);
        var prop = GetDnsLeakLockdownProperty();
        var value = (bool)prop.GetValue(settings.App)!;

        Assert.True(value,
            "Round-trip from explicit 'dns_leak_lockdown: true' must " +
            "preserve true. If this fails, the [YamlMember(Alias = " +
            "\"dns_leak_lockdown\")] mapping on the property is wrong.");
    }

    [Fact]
    public void Yaml_RoundTrip_PreservesValue_False()
    {
        var yaml = """
            schema_version: 5
            app:
              dns_leak_lockdown: false
            """;

        var settings = SettingsLoader.Parse(yaml);
        var prop = GetDnsLeakLockdownProperty();
        var value = (bool)prop.GetValue(settings.App)!;

        Assert.False(value,
            "Round-trip from explicit 'dns_leak_lockdown: false' must " +
            "preserve false. If this fails, the property setter is " +
            "ignoring input or the YAML alias is missing.");
    }

    [Fact]
    public void Yaml_LegacyConfigWithoutField_DefaultsToFalse()
    {
        var yaml = """
            schema_version: 5
            app:
              theme: dark
            """;

        var settings = SettingsLoader.Parse(yaml);
        var prop = GetDnsLeakLockdownProperty();
        var value = (bool)prop.GetValue(settings.App)!;

        Assert.False(value,
            "Legacy YAML (no dns_leak_lockdown key) at the YAML layer " +
            "must deserialize to the C# default (false post-BR-10). " +
            "SettingsMigrator independently sets the same value for " +
            "pre-v5 upgrades; that's pinned by " +
            "SettingsMigrator_FromLegacyV2_DefaultsLockdownFalse.");
    }

    [Fact]
    public void SettingsMigrator_FromLegacyV2_DefaultsLockdownFalse_BR10()
    {
        var s = new AppSettings { SchemaVersion = 2 };
        var migrated = SettingsMigrator.Migrate(
            s, from: 2, to: AppSettings.CurrentSchemaVersion);

        var prop = GetDnsLeakLockdownProperty();
        var value = (bool)prop.GetValue(migrated.App)!;

        Assert.False(value,
            "BR-10: upgrade users from legacy schema must end up with " +
            "DnsLeakLockdown=false. User opts in via Settings → Leak " +
            "Protection if they want the firewall block. sing-box DNS " +
            "routing via VLESS:443 remains the primary leak protection " +
            "regardless of this toggle.");
    }

    [Fact]
    public void SettingsMigrator_AlreadyV3WithLockdown_PreservesUserChoice()
    {
        var prop = GetDnsLeakLockdownProperty();

        {
            var s = new AppSettings { SchemaVersion = AppSettings.CurrentSchemaVersion };
            prop.SetValue(s.App, true);
            var migrated = SettingsMigrator.Migrate(
                s, from: AppSettings.CurrentSchemaVersion,
                to: AppSettings.CurrentSchemaVersion);
            var value = (bool)prop.GetValue(migrated.App)!;
            Assert.True(value,
                "Migrator at same-version (no migration steps) must NOT " +
                "touch user-set DnsLeakLockdown. User chose true; migrator " +
                "must respect that.");
        }

        {
            var s = new AppSettings { SchemaVersion = AppSettings.CurrentSchemaVersion };
            prop.SetValue(s.App, false);
            var migrated = SettingsMigrator.Migrate(
                s, from: AppSettings.CurrentSchemaVersion,
                to: AppSettings.CurrentSchemaVersion);
            var value = (bool)prop.GetValue(migrated.App)!;
            Assert.False(value,
                "Migrator at same-version (no migration steps) must NOT " +
                "touch user-set DnsLeakLockdown. User chose false " +
                "(opt-out — perhaps because of a local dnscrypt-proxy); " +
                "migrator must respect that. Otherwise the toggle would " +
                "revert on every restart.");
        }
    }

    private static PropertyInfo GetDnsLeakLockdownProperty()
    {
        var prop = typeof(AppConfig).GetProperty(
            PropertyName,
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(prop);
        Assert.Equal(typeof(bool), prop!.PropertyType);

        var yamlAttr = prop.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => a.GetType().Name == "YamlMemberAttribute");
        Assert.NotNull(yamlAttr);

        var aliasProp = yamlAttr!.GetType().GetProperty(
            "Alias", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(aliasProp);
        var actualAlias = aliasProp!.GetValue(yamlAttr) as string;
        Assert.Equal(YamlAlias, actualAlias);

        return prop;
    }
}
