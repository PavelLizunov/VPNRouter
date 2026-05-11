using System.Collections.Generic;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// AM-1 (2026-05-11): pin the schema v2→v3 migration for the
/// Applications include/exclude 2-mode feature. The migrator must:
///
/// <list type="bullet">
/// <item>Seed <see cref="AppConfig.RoutingAppsInclude"/> from the
/// legacy top-level <see cref="AppSettings.CustomApps"/> list when the
/// new field is empty.</item>
/// <item>Be idempotent — second migration must not double-seed or
/// touch a user-edited list.</item>
/// <item>Leave the mode at the default "include" unless explicitly
/// changed by the user — the migration is a pure data-shape rebuild,
/// not a UX flip.</item>
/// <item>Survive a cleanly-initialised v3 instance (no legacy data).</item>
/// </list>
///
/// <para>See <c>plans/r10-stas-confirmed-and-apps-2mode.md</c> §2 for
/// the full schema/UX rationale.</para>
/// </summary>
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
        // Mode stays at include — migration doesn't flip UX.
        Assert.Equal("include", migrated.App.RoutingAppsMode);
        // ExcludeList must stay empty until user opts in.
        Assert.Empty(migrated.App.RoutingAppsExclude);
        // Legacy field is NOT cleared — it's still consumed by VpnEngine
        // as a process-name source for the legacy Profile.Processes
        // path, and removing it would silently break that fallback.
        Assert.Equal(3, migrated.CustomApps.Count);
    }

    [Fact]
    public void Migrate_V2_DeduplicatesByCaseInsensitiveKey_PreservingFirstCasing()
    {
        var s = new AppSettings { SchemaVersion = 2 };
        s.CustomApps.Add("Discord.exe");
        s.CustomApps.Add("discord.exe"); // case dup
        s.CustomApps.Add("Discord.EXE"); // case dup
        s.CustomApps.Add("firefox.exe");

        var migrated = SettingsMigrator.Migrate(s, from: 2, to: 3);

        Assert.Equal(2, migrated.App.RoutingAppsInclude.Count);
        // Preserve original casing of the first entry — sing-box
        // process_name matching is case-sensitive on Windows
        // (see VPNRouter.Core/CLAUDE.md).
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
        // Idempotency: when the user has already edited the new list
        // (perhaps via a desktop pre-release), the migrator must not
        // overwrite their work even if CustomApps still has entries.
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
        // Run the v2→v3 step twice. After the first run schema_version
        // is bumped to 3 so the second invocation is a no-op (it scans
        // `from >= to` and returns immediately).
        var s = new AppSettings { SchemaVersion = 2 };
        s.CustomApps.Add("chrome.exe");

        var firstPass = SettingsMigrator.Migrate(s, from: 2, to: 3);
        Assert.Single(firstPass.App.RoutingAppsInclude);

        // Add a new legacy entry AFTER the first migration and re-run.
        firstPass.CustomApps.Add("brave.exe");
        var secondPass = SettingsMigrator.Migrate(firstPass, from: firstPass.SchemaVersion, to: 3);

        // Should still be just chrome.exe — second pass is gated by
        // SchemaVersion AND by the "already populated" idempotency check.
        Assert.Single(secondPass.App.RoutingAppsInclude);
        Assert.Equal("chrome.exe", secondPass.App.RoutingAppsInclude[0]);
    }

    [Fact]
    public void Migrate_FromV0_RunsAllSteps_LandsOnV3()
    {
        // Full chain v0→v1→v2→v3. Migrator runs each step in order and
        // the v2→v3 step still seeds RoutingAppsInclude from CustomApps.
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
        // If a user previously toggled the (yet-unimplemented) Exclude
        // mode in a pre-release build, RoutingAppsExclude may already
        // have data. Migrator must not touch that path.
        var s = new AppSettings { SchemaVersion = 2 };
        s.CustomApps.Add("Discord.exe");
        s.App.RoutingAppsExclude.Add("Steam.exe");

        var migrated = SettingsMigrator.Migrate(s, from: 2, to: 3);

        Assert.Single(migrated.App.RoutingAppsExclude);
        Assert.Equal("Steam.exe", migrated.App.RoutingAppsExclude[0]);
        // Include list still seeded.
        Assert.Single(migrated.App.RoutingAppsInclude);
        Assert.Equal("Discord.exe", migrated.App.RoutingAppsInclude[0]);
    }
}
