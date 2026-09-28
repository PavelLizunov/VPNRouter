using System.Collections.Generic;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class SettingsMigratorAppsModeTests
{
    [Fact]
    public void Migrate_V2_SeedsIncludeListFromLegacyCustomApps()
    {
        var s = new AppSettings { SchemaVersion = 2 };
        s.CustomApps.Add("Discord.exe");
        s.CustomApps.Add("firefox.exe");
        s.CustomApps.Add("Spotify.exe");

        var migrated = SettingsMigrator.Migrate(s, from: 2, to: 3);

        Assert.Equal(3, migrated.SchemaVersion);
        Assert.Equal(3, migrated.App.RoutingAppsInclude.Count);
        Assert.Contains("Discord.exe", migrated.App.RoutingAppsInclude);
        Assert.Contains("firefox.exe", migrated.App.RoutingAppsInclude);
        Assert.Contains("Spotify.exe", migrated.App.RoutingAppsInclude);
        Assert.Equal("include", migrated.App.RoutingAppsMode);
        Assert.Empty(migrated.App.RoutingAppsExclude);
        Assert.Equal(3, migrated.CustomApps.Count);
    }

    [Fact]
    public void Migrate_V2_DeduplicatesByCaseInsensitiveKey_PreservingFirstCasing()
    {
        var s = new AppSettings { SchemaVersion = 2 };
        s.CustomApps.Add("Discord.exe");
        s.CustomApps.Add("discord.exe");
        s.CustomApps.Add("Discord.EXE");
        s.CustomApps.Add("firefox.exe");

        var migrated = SettingsMigrator.Migrate(s, from: 2, to: 3);

        Assert.Equal(2, migrated.App.RoutingAppsInclude.Count);
        Assert.Equal("Discord.exe", migrated.App.RoutingAppsInclude[0]);
        Assert.Equal("firefox.exe", migrated.App.RoutingAppsInclude[1]);
    }

    [Fact]
    public void Migrate_V2_SkipsNullAndWhitespaceEntries()
    {
        var s = new AppSettings { SchemaVersion = 2 };
        s.CustomApps.Add("chrome.exe");
        s.CustomApps.Add("");
        s.CustomApps.Add("   ");
        s.CustomApps.Add("firefox.exe");

        var migrated = SettingsMigrator.Migrate(s, from: 2, to: 3);

        Assert.Equal(2, migrated.App.RoutingAppsInclude.Count);
        Assert.Equal("chrome.exe", migrated.App.RoutingAppsInclude[0]);
        Assert.Equal("firefox.exe", migrated.App.RoutingAppsInclude[1]);
    }

    [Fact]
    public void Migrate_V2_NoLegacyApps_LeavesEverythingEmpty()
    {
        var s = new AppSettings { SchemaVersion = 2 };

        var migrated = SettingsMigrator.Migrate(s, from: 2, to: 3);

        Assert.Equal(3, migrated.SchemaVersion);
        Assert.Empty(migrated.App.RoutingAppsInclude);
        Assert.Empty(migrated.App.RoutingAppsExclude);
        Assert.Equal("include", migrated.App.RoutingAppsMode);
    }

    [Fact]
    public void Migrate_V2_RoutingAppsIncludeAlreadyPopulated_SkipsSeed()
    {
        var s = new AppSettings { SchemaVersion = 2 };
        s.CustomApps.Add("legacy.exe");
        s.App.RoutingAppsInclude.Add("user-edited.exe");

        var migrated = SettingsMigrator.Migrate(s, from: 2, to: 3);

        Assert.Single(migrated.App.RoutingAppsInclude);
        Assert.Equal("user-edited.exe", migrated.App.RoutingAppsInclude[0]);
    }

    [Fact]
    public void Migrate_V2_DoubleApply_StaysIdempotent()
    {
        var s = new AppSettings { SchemaVersion = 2 };
        s.CustomApps.Add("chrome.exe");

        var firstPass = SettingsMigrator.Migrate(s, from: 2, to: 3);
        Assert.Single(firstPass.App.RoutingAppsInclude);

        firstPass.CustomApps.Add("brave.exe");
        var secondPass = SettingsMigrator.Migrate(firstPass, from: firstPass.SchemaVersion, to: 3);

        Assert.Single(secondPass.App.RoutingAppsInclude);
        Assert.Equal("chrome.exe", secondPass.App.RoutingAppsInclude[0]);
    }

    [Fact]
    public void Migrate_FromV0_RunsAllSteps_LandsOnV3()
    {
        var s = new AppSettings { SchemaVersion = 0 };
        s.CustomApps.Add("Slack.exe");

        var migrated = SettingsMigrator.Migrate(s, from: 0, to: 3);

        Assert.Equal(3, migrated.SchemaVersion);
        Assert.Single(migrated.App.RoutingAppsInclude);
        Assert.Equal("Slack.exe", migrated.App.RoutingAppsInclude[0]);
    }

    [Fact]
    public void Migrate_V2_KeepsRoutingAppsExcludeUntouched_EvenWithLegacyData()
    {
        var s = new AppSettings { SchemaVersion = 2 };
        s.CustomApps.Add("Discord.exe");
        s.App.RoutingAppsExclude.Add("Steam.exe");

        var migrated = SettingsMigrator.Migrate(s, from: 2, to: 3);

        Assert.Single(migrated.App.RoutingAppsExclude);
        Assert.Equal("Steam.exe", migrated.App.RoutingAppsExclude[0]);
        Assert.Single(migrated.App.RoutingAppsInclude);
        Assert.Equal("Discord.exe", migrated.App.RoutingAppsInclude[0]);
    }
}
