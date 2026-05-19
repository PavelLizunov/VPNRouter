using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Wave-39 (<c>hotfix-dns-leak-firewall-lockdown-2026-05-19</c>) — pin the
/// new <c>App.DnsLeakLockdown</c> persistence contract introduced for the
/// firewall-level DNS lockdown feature.
///
/// <para>The brief specifies:</para>
/// <list type="bullet">
///   <item>Default <c>true</c> for new installs (privacy-by-default).</item>
///   <item>Default <c>false</c> for upgrade users (don't surprise them
///   — they get a UI prompt to opt in).</item>
///   <item>Persisted via YAML alias <c>dns_leak_lockdown</c> under the
///   <c>app:</c> section.</item>
///   <item>YAML round-trip preserves true/false; missing-key legacy
///   configs deserialize to the C# default (true), THEN
///   <see cref="SettingsMigrator"/> may flip to false during the v2→v3
///   upgrade path so existing users don't get surprised.</item>
/// </list>
///
/// <para><strong>Test strategy.</strong> The new property and migrator
/// branch don't exist in this worktree (pre-Agent-A code). All tests use
/// reflection so the file compiles cleanly against pre-Wave-39 source —
/// the assertions fail loudly when the property is absent, turning them
/// into regression-detectors that pass after Agent A's merge. See
/// <see cref="AutostartContractTests"/> for the canonical pattern.</para>
///
/// <para><strong>Which tests fail pre-Wave-39?</strong> Every test fails
/// against the pre-Agent-A production code in this worktree — they
/// either pin a property that doesn't exist (<c>App.DnsLeakLockdown</c>)
/// or a migrator branch that hasn't been added. After Agent A lands they
/// go green and stay green.</para>
///
/// <para>Layer ordering matters and is tested separately:</para>
/// <list type="number">
///   <item>YAML deserialization defaults to C# default (true) when the
///   key is absent from the file. This is the BOTTOM layer.</item>
///   <item>SettingsMigrator runs AFTER YAML deserialization and MAY flip
///   the value to false based on the prior schema version (don't
///   surprise legacy users). This is the TOP layer.</item>
/// </list>
/// </summary>
public class AppSettingsDnsLeakLockdownTests
{
    private const string PropertyName = "DnsLeakLockdown";
    private const string YamlAlias = "dns_leak_lockdown";

    // ─── Task 2.1: New install default = true ────────────────────────────

    [Fact]
    public void NewInstall_DefaultsToTrue()
    {
        // Pins POST-Wave-39 behavior. FAILS against pre-Wave-39 because
        // the property doesn't exist on AppConfig yet.
        //
        // What this pins: a freshly-constructed AppSettings (i.e. new
        // install on a machine with no config.yaml) has
        // App.DnsLeakLockdown == true. Privacy-by-default — new users
        // get protected without opting in.
        var settings = new AppSettings();
        Assert.NotNull(settings.App);

        var prop = GetDnsLeakLockdownProperty();
        var defaultValue = (bool)prop.GetValue(settings.App)!;

        Assert.True(defaultValue,
            "Wave 39 brief §'Files to touch (Agent A)': new installs must " +
            "default to true (privacy-by-default). SettingsMigrator handles " +
            "the upgrade-path opt-in separately.");
    }

    // ─── Task 2.2 + 2.3: YAML round-trip preserves true/false ────────────

    [Fact]
    public void Yaml_RoundTrip_PreservesValue_True()
    {
        // Pins POST-Wave-39. FAILS against pre-Wave-39 — the property
        // doesn't exist so the YAML alias mapping is missing and round-
        // trip drops to the default.
        //
        // Build a YAML blob that explicitly sets dns_leak_lockdown: true,
        // parse via SettingsLoader.Parse (the production path).
        // Wave 39 bumped CurrentSchemaVersion from 4 to 5. Use 5 so the
        // SettingsMigrator skips the v4→v5 step (which would flip the
        // value to false for upgrade users) and we observe pure YAML
        // round-trip behaviour.
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
        // Pins POST-Wave-39. FAILS against pre-Wave-39. Symmetric to the
        // True case to catch a hardcoded "always-true" bug in the
        // round-trip.
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

    // ─── Task 2.4: Legacy YAML without key → C# default (true) ───────────

    [Fact]
    public void Yaml_LegacyConfigWithoutField_DefaultsToTrue()
    {
        // Pins POST-Wave-39 behavior at the YAML deserialization LAYER
        // only. FAILS against pre-Wave-39 because the property doesn't
        // exist.
        //
        // Note this conflicts with SettingsMigrator's intended behavior
        // (which should set false for v2→v3 upgrades to avoid surprising
        // existing users). The migrator runs AFTER YAML load, so YAML-
        // default is true, then migrator may flip to false. This test
        // exercises ONLY the YAML layer — the migrator behavior is
        // pinned separately by SettingsMigrator_FromLegacyV2_*.
        //
        // To exercise the YAML layer in isolation, parse a yaml with
        // schema_version: 4 (already-migrated) so no migration runs
        // and we observe the pure deserialization default.
        // Schema 5 (post-Wave-39) so SettingsMigrator skips v4→v5 step
        // that would flip dns_leak_lockdown to false for upgrade users.
        // The intent of THIS test is to exercise YAML deserialization
        // in isolation; the upgrade-path migration is pinned by
        // SettingsMigrator_FromLegacyV2_DefaultsLockdownFalse instead.
        var yaml = """
            schema_version: 5
            app:
              theme: dark
            """;

        var settings = SettingsLoader.Parse(yaml);
        var prop = GetDnsLeakLockdownProperty();
        var value = (bool)prop.GetValue(settings.App)!;

        Assert.True(value,
            "Legacy YAML (no dns_leak_lockdown key) at the YAML layer " +
            "must deserialize to the C# default (true). SettingsMigrator " +
            "may flip this to false later for upgrade users; that's pinned " +
            "by SettingsMigrator_FromLegacyV2_DefaultsLockdownFalse.");
    }

    // ─── Task 2.5: Migrator from v2 sets DnsLeakLockdown = false ─────────

    [Fact]
    public void SettingsMigrator_FromLegacyV2_DefaultsLockdownTrue_BR5()
    {
        // BR-5 (brat 2026-05-19) — flipped from opt-out (false) to
        // opt-in-by-default (true). Original Wave 39 was cautious about
        // LAN-DNS-proxy users; brat's logs showed DNS leaking to a RU
        // ISP resolver (95.85.16.212) because the protection that was
        // the whole point of Wave 39 never activated for upgrade users.
        // Now everyone gets the default-on protection; LAN-proxy users
        // can disable via Settings → Leak Protection.
        var s = new AppSettings { SchemaVersion = 2 };
        var migrated = SettingsMigrator.Migrate(
            s, from: 2, to: AppSettings.CurrentSchemaVersion);

        var prop = GetDnsLeakLockdownProperty();
        var value = (bool)prop.GetValue(migrated.App)!;

        Assert.True(value,
            "BR-5: upgrade users from legacy schema must end up with " +
            "DnsLeakLockdown=true so DNS leak protection is active out " +
            "of the box. Original false default was the trigger for " +
            "brat's RU ISP DNS leak (singbox.log dns: exchanged trace " +
            "to 95.85.16.212).");
    }

    // ─── Task 2.6: Migrator on already-migrated config preserves user choice ─

    [Fact]
    public void SettingsMigrator_AlreadyV3WithLockdown_PreservesUserChoice()
    {
        // Pins POST-Wave-39. FAILS against pre-Wave-39 — property doesn't
        // exist.
        //
        // What this pins: an already-Wave-39 settings tree with the user
        // having explicitly toggled the value (either to true or to
        // false) must NOT be touched by the migrator on subsequent
        // loads. This prevents a "stuck-on / stuck-off" bug where the
        // toggle reverts on every restart.
        var prop = GetDnsLeakLockdownProperty();

        // Case A: user toggled to true on an already-current schema
        // → migrator no-op preserves true.
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

        // Case B: user toggled to false on an already-current schema
        // → migrator no-op preserves false (this is the more important
        // direction because it's the one user might toggle deliberately
        // off if their local DNS proxy broke).
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

    // ─── helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve <c>AppConfig.DnsLeakLockdown</c> via reflection. Throws
    /// (via Assert.NotNull) when the property doesn't exist — that's the
    /// pre-Wave-39 failure mode every test in this class relies on for
    /// regression detection.
    /// </summary>
    private static PropertyInfo GetDnsLeakLockdownProperty()
    {
        var prop = typeof(AppConfig).GetProperty(
            PropertyName,
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(prop);
        Assert.Equal(typeof(bool), prop!.PropertyType);

        // Verify the YamlMemberAttribute alias matches the brief's
        // expectation. Loose attribute lookup (by type-name) so we don't
        // create a hard dep on YamlDotNet that already exists transitively
        // through Core.
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
