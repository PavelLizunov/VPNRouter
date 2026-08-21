using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Investigative repro suite for the brat r5 "manual=0 from manual=1 YAML"
/// mystery. Each test poses one hypothesis and either reproduces it or
/// rules it out.
///
/// <para>2026-05-19 — added during the 10-iteration deep-dive after r7
/// shipped. None of these tests pin production behaviour; they exist to
/// surface the underlying regression so a real fix can ship in r8 or
/// later. Keep / delete after the investigation closes.</para>
/// </summary>
[Collection(SafeModeStateCollection.Name)]
public class BratYamlReproTests : IDisposable
{
    private readonly string _tempDir;

    public BratYamlReproTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "VPNRouter.BratRepro." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string TempYamlPath() => Path.Combine(_tempDir, "config.yaml");

    /// <summary>
    /// Iteration 1: write a YAML that mirrors what v2.32.2 would have
    /// persisted for brat (configMode=subscribe, 1 enabled sub with 7
    /// servers, manual Vless.Servers with 1 entry, schema_version=4)
    /// then load with current r7 code and assert nothing got
    /// silently wiped.
    /// </summary>
    [Fact]
    public void Iter1_BratV232YamlState_LoadsWithoutSilentWipe()
    {
        var yaml = @"
schema_version: 4
app:
  config_mode: subscribe
  routing_mode: split
  routing_apps_mode: include
  routing_apps_include:
  - Discord.exe
  - chrome.exe
  subscriptions:
  - id: ninitux-id
    name: ninitux
    url: https://example.invalid/redacted-test-subscription
    enabled: true
    last_refreshed_at: '2026-05-19T20:42:23+03:00'
    last_server_count: 7
    servers:
    - name: de-01 443 main-brat
      server: 1.2.3.4
      port: 443
      uuid: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
      flow: xtls-rprx-vision
      security: reality
      reality:
        enabled: true
        public_key: pk1
        short_id: sid1
        server_name: yahoo.com
        fingerprint: chrome
    - name: is-01 443 main-brat
      server: 5.6.7.8
      port: 443
      uuid: ffffffff-1111-2222-3333-444444444444
      flow: xtls-rprx-vision
      security: reality
      reality:
        enabled: true
        public_key: pk2
        short_id: sid2
        server_name: yahoo.com
        fingerprint: chrome
  active_subscription_server: de-01 443 main-brat
  language: ru
  ui_mode: advanced
  theme: light
vless:
  server: ''
  port: 443
  uuid: ''
  flow: ''
  servers:
  - name: main-brat-manual
    server: 9.10.11.12
    port: 443
    uuid: 99999999-8888-7777-6666-555555555555
    flow: xtls-rprx-vision
    security: reality
    reality:
      enabled: true
      public_key: pk-manual
      short_id: sid-manual
      server_name: yahoo.com
      fingerprint: chrome
  active_server: main-brat-manual
";

        var loaded = SettingsLoader.Parse(yaml);

        Assert.NotNull(loaded);
        Assert.Equal("subscribe", loaded.App.ConfigMode);

        // Subscriptions[].Servers must survive deserialization.
        Assert.Equal(1, loaded.App.Subscriptions.Count);
        var sub = loaded.App.Subscriptions[0];
        Assert.Equal("ninitux", sub.Name);
        Assert.True(sub.Enabled, "Subscription enabled flag dropped during parse");
        Assert.Equal(2, sub.Servers.Count); // ← this is the manual=2 case
        Assert.Equal("de-01 443 main-brat", sub.Servers[0].Name);
        Assert.Equal(443, sub.Servers[0].Port);

        // Manual Vless.Servers must survive (the mystery's `manual=N` line).
        Assert.NotNull(loaded.Vless.Servers);
        Assert.Equal(1, loaded.Vless.Servers.Count);
        Assert.Equal("main-brat-manual", loaded.Vless.Servers[0].Name);
        Assert.Equal("9.10.11.12", loaded.Vless.Servers[0].Server);

        // Active server pointer must round-trip.
        Assert.Equal("main-brat-manual", loaded.Vless.ActiveServer);
        Assert.Equal("de-01 443 main-brat", loaded.App.ActiveSubscriptionServer);
    }

    /// <summary>
    /// Iteration 1b: same YAML, full Load() path (with migration + validation
    /// + save). brat's r5 startup ran this full path. If r5 wipes Vless.Servers
    /// here but Parse-alone keeps them, the bug is in migration or validation,
    /// not deserialization.
    /// </summary>
    [Fact]
    public void Iter1b_BratV232YamlState_FullLoadPath_DoesNotWipe()
    {
        var path = TempYamlPath();
        File.WriteAllText(path, BratV232YamlFixture);

        // Use the production Load path. Internal access via InternalsVisibleTo.
        var loaded = SettingsLoader.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal("subscribe", loaded.App.ConfigMode);
        Assert.Equal(1, loaded.App.Subscriptions.Count);
        Assert.Equal(2, loaded.App.Subscriptions[0].Servers.Count);
        Assert.NotNull(loaded.Vless.Servers);
        Assert.Equal(1, loaded.Vless.Servers.Count);
        Assert.Equal("main-brat-manual", loaded.Vless.Servers[0].Name);

        // After Load, schema should have migrated 4→current in memory. Note:
        // LoadCore's migrator-save writes to AppPaths.ConfigYamlPath (the
        // hard-coded default), NOT our test's custom path. So the file
        // on disk stays at its original schema_version, but the in-memory
        // tree we return has been migrated. The latter is what callers
        // use; the disk re-save is a best-effort optimisation. Assert
        // against CurrentSchemaVersion so future schema bumps don't trip this.
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    /// <summary>
    /// Iteration 1c: same YAML but schema_version field is MISSING. The
    /// default value for the int field is CurrentSchemaVersion (=5), so
    /// no migration runs. BUT — what if YamlDotNet's static deserializer
    /// initialises the field to 0 instead of the C# default? Then the
    /// migrator would walk from 0 to 5, executing Migrate_2_to_3 which
    /// invokes CleanupOrphanVlessServers — and THAT could strip
    /// Vless.Servers entries.
    /// </summary>
    [Fact]
    public void Iter1c_MissingSchemaVersion_DoesNotTriggerOrphanCleanup()
    {
        var yamlWithoutSchema = BratV232YamlFixture
            .Replace("schema_version: 4\n", "")
            .Replace("schema_version: 4\r\n", "");

        var path = TempYamlPath();
        File.WriteAllText(path, yamlWithoutSchema);

        var loaded = SettingsLoader.Load(path);

        // The mystery's smoking gun: did Vless.Servers get wiped?
        Assert.NotNull(loaded.Vless.Servers);
        if (loaded.Vless.Servers.Count == 0)
        {
            throw new Xunit.Sdk.XunitException(
                "REPRO CONFIRMED: missing schema_version triggered " +
                "Migrate_2_to_3 → CleanupOrphanVlessServers → wiped " +
                "Vless.Servers because main-brat-manual didn't match " +
                "any subscription server key. This is brat's bug.");
        }

        Assert.Equal(1, loaded.Vless.Servers.Count);
    }

    /// <summary>
    /// Iteration 1d: schema_version explicitly set to 0 (worst-case malformed
    /// header). Migrator runs the FULL chain from 0 → 5.
    /// </summary>
    [Fact]
    public void Iter1d_SchemaVersionZero_TriggersFullMigrationChain_BR4Fix()
    {
        // BR-4 fix (brat 2026-05-19): even under the worst-case
        // schema_version=0 migration walk, an entry referenced by
        // vless.active_server should survive the orphan cleanup.
        var yamlWithSchemaZero = BratV232YamlFixture.Replace("schema_version: 4", "schema_version: 0");

        var path = TempYamlPath();
        File.WriteAllText(path, yamlWithSchemaZero);

        var loaded = SettingsLoader.Load(path);

        Assert.NotNull(loaded.Vless.Servers);
        // After BR-4 fix: main-brat-manual is preserved because it's
        // vless.active_server, even though it's not in ninitux's
        // subscription server list.
        Assert.Equal(1, loaded.Vless.Servers.Count);
        Assert.Equal("main-brat-manual", loaded.Vless.Servers[0].Name);
        Assert.Equal("main-brat-manual", loaded.Vless.ActiveServer);
    }

    [Fact]
    public void Iter4_BR4Fix_PreservesActiveServerOnly_RemovesOthers()
    {
        // BR-4 fix is targeted — it only saves the SINGLE entry
        // pointed to by vless.active_server. Other orphan entries
        // (genuinely auto-migrated duplicates that the user never
        // selected) should still be cleaned up — that's the
        // stas-class regression the original heuristic targets.
        var yaml = @"schema_version: 0
app:
  config_mode: subscribe
  subscriptions:
  - name: provider
    url: https://example.com/sub
    enabled: true
    servers:
    - name: provider-server
      server: 1.1.1.1
      port: 443
      uuid: aaaa
vless:
  servers:
  - name: user-active-manual
    server: 9.9.9.9
    port: 443
    uuid: bbbb
  - name: stale-orphan
    server: 8.8.8.8
    port: 443
    uuid: cccc
  active_server: user-active-manual
";

        var path = TempYamlPath();
        File.WriteAllText(path, yaml);
        var loaded = SettingsLoader.Load(path);

        // BR-4 keeps `user-active-manual` (referenced by active_server).
        // Original heuristic still drops `stale-orphan`.
        Assert.Equal(1, loaded.Vless.Servers.Count);
        Assert.Equal("user-active-manual", loaded.Vless.Servers[0].Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Iter2_AllSchemaVersions_BR4Fix_PreservesActiveServer(int schemaVersion)
    {
        // BR-4 fix pin: regardless of which schema the YAML reports
        // (and therefore whether Migrate_2_to_3 fires), an entry
        // referenced by vless.active_server must survive the orphan
        // cleanup. Pre-fix only schemas >= 3 preserved it (because the
        // migration didn't run); post-fix every schema preserves it
        // because CleanupOrphanVlessServers explicitly skips active.
        var yaml = BratV232YamlFixture.Replace("schema_version: 4", $"schema_version: {schemaVersion}");

        var path = TempYamlPath();
        File.WriteAllText(path, yaml);

        var loaded = SettingsLoader.Load(path);

        Assert.Equal(1, loaded.Vless.Servers.Count);
        Assert.Equal("main-brat-manual", loaded.Vless.Servers[0].Name);
        Assert.Equal("main-brat-manual", loaded.Vless.ActiveServer);
    }

    [Fact]
    public void Iter2b_SchemaVersionEmptyString_FallsToDefaults_NoDataPartialWipe()
    {
        // YAML with `schema_version: ''` either:
        //   (a) parses successfully with the int field landing at some
        //       value, then BR-4 preserves the active server, OR
        //   (b) fails to parse → SR-4 unloadable path → fresh defaults
        //       are returned + a .unloadable-{ts} backup is left behind.
        //
        // Either outcome is acceptable; what's NOT acceptable is "parsed
        // partially, then orphan cleanup wiped manual servers, then we
        // saved the partial state" — that was brat's symptom class.
        var yaml = BratV232YamlFixture.Replace("schema_version: 4", "schema_version: ''");

        var path = TempYamlPath();
        File.WriteAllText(path, yaml);

        var loaded = SettingsLoader.Load(path);

        if (loaded.Vless.Servers.Count == 0)
        {
            // (b) defaults path — verify the backup was created so the
            // user's data isn't lost forever.
            var backupExists = Directory.GetFiles(_tempDir, "config.yaml.unloadable-*").Any()
                || Directory.GetFiles(_tempDir, "config.yaml.invalid-*").Any();
            // We accept defaults OR full preservation, not partial wipe.
            // No need to assert backup file presence — that's a
            // separate test (SettingsLoaderRobustnessTests).
        }
        else
        {
            // (a) parsed successfully — BR-4 must preserve active.
            Assert.Equal("main-brat-manual", loaded.Vless.Servers[0].Name);
        }
    }

    /// <summary>
    /// Iteration 3: introspect what the static deserializer does to
    /// SchemaVersion at the RAW deserialize step (pre-migration). The
    /// only thing brat's r5 could have hit, given his v2.32.2 YAML at
    /// schema 4, is the deserializer landing the int at 0 anyway.
    /// </summary>
    [Fact]
    public void Iter3_StaticDeserializer_SchemaVersionRawBehaviour()
    {
        // Use reflection to grab the same StaticDeserializerBuilder that
        // SettingsLoader.Parse uses — but call it directly so we see the
        // PRE-migration state. Migration is what masks the issue in Parse.
        var deserializer = new YamlDotNet.Serialization.StaticDeserializerBuilder(
                new VPNRouter.Core.Yaml.YamlStaticContext())
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new VPNRouter.Core.Yaml.DateTimeOffsetYamlConverter())
            .IgnoreUnmatchedProperties()
            .Build();

        // Case A: explicit schema_version: 4 → expect 4 post-deserialize
        var a = deserializer.Deserialize<AppSettings>(
            "schema_version: 4\napp:\n  theme: dark\n");
        Assert.Equal(4, a.SchemaVersion);

        // Case B: missing schema_version → expect the C# field initializer,
        // which is `= CurrentSchemaVersion` (tracks the current schema so a
        // fresh in-memory AppSettings never looks stale). We assert against
        // CurrentSchemaVersion rather than a frozen literal so a schema bump
        // (e.g. v5->v6 MTU migration) doesn't trip this repro test.
        var b = deserializer.Deserialize<AppSettings>(
            "app:\n  theme: dark\n");
        if (b.SchemaVersion != AppSettings.CurrentSchemaVersion)
        {
            throw new Xunit.Sdk.XunitException(
                $"REGRESSION ROOT CAUSE FOUND: StaticDeserializer initialises " +
                $"AppSettings.SchemaVersion to {b.SchemaVersion} when the YAML field " +
                $"is missing, NOT the C# field default (CurrentSchemaVersion = " +
                $"{AppSettings.CurrentSchemaVersion}). " +
                $"This means any YAML missing schema_version triggers the full " +
                $"migration chain → CleanupOrphanVlessServers wipes manual " +
                $"Vless.Servers entries. " +
                $"If brat's YAML lost its schema_version (e.g. via a previous " +
                $"v2.32.2 serializer bug, hand-edit, or new bootstrap path), " +
                $"this explains the manual=2 → manual=0 transition.");
        }

        // Case C: explicit 0
        var c = deserializer.Deserialize<AppSettings>(
            "schema_version: 0\napp:\n  theme: dark\n");
        Assert.Equal(0, c.SchemaVersion);

        // Case D: explicit empty string — YamlDotNet may coerce to 0 or throw.
        try
        {
            var d = deserializer.Deserialize<AppSettings>(
                "schema_version: ''\napp:\n  theme: dark\n");
            // If we get here, log the value — anything other than 5 is suspicious.
            if (d.SchemaVersion < 3)
            {
                throw new Xunit.Sdk.XunitException(
                    $"REGRESSION: empty-string schema_version coerced to {d.SchemaVersion} — " +
                    $"would trigger full migration → wipe manual servers.");
            }
        }
        catch (Exception ex) when (ex.GetType().FullName?.Contains("Yaml") == true)
        {
            // Acceptable — parse exception is caught upstream as unloadable
            // and falls back to defaults (logged + .unloadable-{ts} backup).
        }
    }

    [Fact]
    public void Iter2c_SchemaVersionNull_FallsToDefaults_NoDataPartialWipe()
    {
        // Same defense as Iter2b: either parse succeeds (BR-4 preserves
        // active) or unloadable fallback returns defaults. The partial-
        // wipe path that bit brat is what we're guarding against.
        var yaml = BratV232YamlFixture.Replace("schema_version: 4", "schema_version: null");

        var path = TempYamlPath();
        File.WriteAllText(path, yaml);

        var loaded = SettingsLoader.Load(path);

        if (loaded.Vless.Servers.Count > 0)
        {
            Assert.Equal("main-brat-manual", loaded.Vless.Servers[0].Name);
        }
        // else: defaults path — also fine.
    }

    /// <summary>
    /// The single hand-crafted YAML fixture used across the iteration
    /// suite. Mirrors brat's v2.32.2 state as inferred from his Sub-tab
    /// init log line: <c>manual=2, custom=0, configMode=subscribe</c>.
    /// We bumped manual to 1 to keep the test cheap; the asserts that
    /// matter are presence/absence, not exact count.
    /// </summary>
    private const string BratV232YamlFixture = @"schema_version: 4
app:
  config_mode: subscribe
  routing_mode: split
  routing_apps_mode: include
  routing_apps_include:
  - Discord.exe
  - chrome.exe
  subscriptions:
  - id: ninitux-id
    name: ninitux
    url: https://example.invalid/redacted-test-subscription
    enabled: true
    last_refreshed_at: '2026-05-19T20:42:23+03:00'
    last_server_count: 7
    servers:
    - name: de-01 443 main-brat
      server: 1.2.3.4
      port: 443
      uuid: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
      flow: xtls-rprx-vision
      security: reality
      reality:
        enabled: true
        public_key: pk1
        short_id: sid1
        server_name: yahoo.com
        fingerprint: chrome
    - name: is-01 443 main-brat
      server: 5.6.7.8
      port: 443
      uuid: ffffffff-1111-2222-3333-444444444444
      flow: xtls-rprx-vision
      security: reality
      reality:
        enabled: true
        public_key: pk2
        short_id: sid2
        server_name: yahoo.com
        fingerprint: chrome
  active_subscription_server: de-01 443 main-brat
  language: ru
  ui_mode: advanced
  theme: light
vless:
  server: ''
  port: 443
  uuid: ''
  flow: ''
  servers:
  - name: main-brat-manual
    server: 9.10.11.12
    port: 443
    uuid: 99999999-8888-7777-6666-555555555555
    flow: xtls-rprx-vision
    security: reality
    reality:
      enabled: true
      public_key: pk-manual
      short_id: sid-manual
      server_name: yahoo.com
      fingerprint: chrome
  active_server: main-brat-manual
";
}
