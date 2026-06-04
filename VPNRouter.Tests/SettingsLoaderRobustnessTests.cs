using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

// Phase 6 (v3.0 refactor): this suite pins the static
// SettingsLoader.Load/Save back-compat surface that RealSettingsStore
// delegates to. Test cases must keep calling the static API directly
// (not via ISettingsStore) because we want crash-mode behaviour,
// .unloadable-{ts} rename, parse-error backups, etc. — those are
// static-loader semantics that the interface intentionally abstracts
// away. After Wave 27, Load/Save are `internal static` (no longer
// `[Obsolete]`); friend-assembly access comes from
// VPNRouter.Core.csproj's <InternalsVisibleTo Include="VPNRouter.Tests" />.

namespace VPNRouter.Tests;

/// <summary>
/// SR-4 (v2.32.0): structural robustness tests for
/// <see cref="SettingsLoader.Load"/>. Sister suite SR-1 covers semantic
/// validation (bad-but-parseable values); these tests cover what happens
/// when the file itself can't be loaded — missing, empty, garbled, partial,
/// type-mismatched, locked, or contains duplicate keys.
///
/// <para><b>Contract under test:</b> Load() never throws. Any failure mode
/// yields a fully-defaulted <see cref="AppSettings"/> with every reference-
/// typed sub-section non-null (the sane sweep handled by
/// <see cref="AppSettingsSane.EnsureSane"/>). Catastrophic failures (parse
/// errors, schema-coercion errors) additionally back the original up as
/// <c>config.yaml.unloadable-{ts}</c> so users can recover values manually.</para>
///
/// <para>Tests use unique temp paths per case so they can run in parallel
/// without colliding on the shared default config path.</para>
///
/// <para><b>3G-1 (v3.0 refactor):</b> joined <see cref="SafeModeStateCollection"/>
/// so the loader's <c>if (SafeMode.Enabled) return defaults</c> short-circuit
/// at the top of <see cref="SettingsLoader.Load"/> can't fire while a
/// SafeMode-flipping test (StartupPipelineTests) is mid-flight in a parallel
/// thread. This was the documented flake — when AutoFailoverEngineTests +
/// StartupPipelineTests flipped SafeMode for unrelated reasons, ~14 cases in
/// this suite tripped because Load returned defaults instead of parsing
/// the fixture.</para>
/// </summary>
[Collection(SafeModeStateCollection.Name)]
public class SettingsLoaderRobustnessTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsLoaderRobustnessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "VPNRouter.SR4." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string PathFor(string filename) => Path.Combine(_tempDir, filename);

    private static void AssertSane(AppSettings s)
    {
        // Every reference-typed sub-section must be non-null after load.
        Assert.NotNull(s);
        Assert.NotNull(s.App);
        Assert.NotNull(s.Vless);
        Assert.NotNull(s.Tun);
        Assert.NotNull(s.Dns);
        Assert.NotNull(s.SingBox);
        Assert.NotNull(s.Monitoring);
        Assert.NotNull(s.Update);
        Assert.NotNull(s.ProfileSources);
        Assert.NotNull(s.CustomApps);
        Assert.NotNull(s.CustomGroupApps);
        Assert.NotNull(s.CustomCategories);
        Assert.NotNull(s.ExcludedApps);
        Assert.NotNull(s.Vless.Reality);
        Assert.NotNull(s.Vless.Tls);
        Assert.NotNull(s.Vless.Transport);
        Assert.NotNull(s.Vless.Servers);
        Assert.NotNull(s.Vless.Transport.Headers);
        Assert.NotNull(s.Tun.RouteExcludeAddress);
        Assert.NotNull(s.App.CustomConfigs);
        Assert.NotNull(s.App.SubscriptionServers);
        Assert.NotNull(s.App.Subscriptions);
        Assert.NotNull(s.App.CustomDirectRules);
        Assert.NotNull(s.App.CustomRules);
        Assert.NotNull(s.App.UserFreeSources);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 1. Missing file → defaults (and example written if possible)
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var path = PathFor("missing.yaml");
        Assert.False(File.Exists(path));

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
        // WriteExample also lays down an example file — verify side-effect.
        Assert.True(File.Exists(path),
            "Load() with missing file should write an example config side-effect.");
    }

    // ─────────────────────────────────────────────────────────────────────
    // 2. Empty file → defaults (no backup needed; YamlDotNet treats this
    // as "zero documents" not a parse error)
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Load_EmptyFile_ReturnsDefaults_NoBackup()
    {
        var path = PathFor("empty.yaml");
        File.WriteAllText(path, string.Empty);

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        // Empty file is structurally valid (zero YAML documents) — no
        // unloadable backup should be created.
        var unloadable = Directory.GetFiles(_tempDir, "*.unloadable-*");
        Assert.Empty(unloadable);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 3. Malformed YAML → defaults + unloadable backup
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Load_MalformedYaml_ReturnsDefaults_BackupsCorruptFile()
    {
        var path = PathFor("malformed.yaml");
        // Indentation chaos + unclosed mapping — guaranteed YamlException.
        File.WriteAllText(path,
            "app:\n" +
            "  routing_mode: split\n" +
            "    bad: indentation\n" +
            "vless:\n" +
            "  servers: [unterminated\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        // Original file must be moved out of the way so next Save doesn't
        // re-corrupt onto the same handle.
        Assert.False(File.Exists(path),
            "Unloadable file should be renamed to .unloadable-{ts}");
        var backups = Directory.GetFiles(_tempDir, "malformed.yaml.unloadable-*");
        Assert.Single(backups);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 4. Top-level section missing → defaults filled in for absent sections
    //    while present sections remain readable
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Load_MissingTopLevelSection_FillsInDefaults()
    {
        var path = PathFor("partial.yaml");
        // Intentionally missing app:, tun:, dns:, singbox:, monitoring:, update:
        // — only vless: present, with one server.
        File.WriteAllText(path,
            "vless:\n" +
            "  server: example.com\n" +
            "  port: 443\n" +
            "  uuid: aaaa-bbbb-cccc-dddd\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        // Vless was present in YAML — its values must be preserved.
        Assert.Equal("example.com", s.Vless.Server);
        Assert.Equal(443, s.Vless.Port);
        Assert.Equal("aaaa-bbbb-cccc-dddd", s.Vless.Uuid);
        // App was absent but defaults must apply. v2.32.0 default RoutingMode
        // = "split" (revert 2026-05-10 d9f7027 — desktop was never supposed
        // to flip to "full"; F-02 chip's default-flip was reverted with the
        // rest of the desktop changes).
        Assert.Equal("split", s.App.RoutingMode);
        Assert.Equal("system", s.App.Theme);   // Fix #7: default theme is now "system" (follow OS)
    }

    // ─────────────────────────────────────────────────────────────────────
    // 5. Partial sub-section with explicit empty values → other fields
    //    preserved + missing nested objects filled with defaults
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Load_PartialSubSection_PreservesOtherFields_FillsMissing()
    {
        var path = PathFor("partial-vless.yaml");
        // vless: with explicit ~ for nested objects — YamlDotNet sets them
        // to null. EnsureSane must replace with defaults without dropping
        // the populated server field.
        File.WriteAllText(path,
            "vless:\n" +
            "  server: real.example.com\n" +
            "  reality: ~\n" +
            "  tls: ~\n" +
            "  transport: ~\n" +
            "  servers: ~\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.Equal("real.example.com", s.Vless.Server);
        // Nested objects nulled in YAML must be re-initialized non-null.
        Assert.NotNull(s.Vless.Reality);
        Assert.NotNull(s.Vless.Tls);
        Assert.NotNull(s.Vless.Transport);
        Assert.NotNull(s.Vless.Servers);
        Assert.Empty(s.Vless.Servers);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 6. Type-coercion failure (string for int) → defaults + backup
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Load_TypeCoercionFailure_ReturnsDefaults_BackupsFile()
    {
        var path = PathFor("typemismatch.yaml");
        // tg_proxy_port expects int — feeding it a non-numeric string
        // forces YamlDotNet to throw on Deserialize.
        File.WriteAllText(path,
            "app:\n" +
            "  tg_proxy_port: \"not-a-number\"\n" +
            "  tg_proxy_enabled: true\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        // Original must be moved out of the way; backup created.
        Assert.False(File.Exists(path));
        var backups = Directory.GetFiles(_tempDir, "typemismatch.yaml.unloadable-*");
        Assert.Single(backups);
        // Defaults restored.
        Assert.Equal(1443, s.App.TgProxyPort);
        Assert.False(s.App.TgProxyEnabled);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 7. File locked by another process → defaults, original untouched
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Load_FileLocked_ReturnsDefaults_OriginalUntouched()
    {
        var path = PathFor("locked.yaml");
        File.WriteAllText(path, "app:\n  theme: dark\n");

        // Hold the file with FileShare.None so File.ReadAllText fails.
        using (var holder = new FileStream(path, FileMode.Open,
                   FileAccess.Read, FileShare.None))
        {
            var s = SettingsLoader.Load(path);

            AssertSane(s);
            // Failed-to-read should NOT touch the file on disk — next
            // launch can retry once the lock is released.
            Assert.True(File.Exists(path));
            var backups = Directory.GetFiles(_tempDir, "locked.yaml.unloadable-*");
            Assert.Empty(backups);
            // Defaults active because we couldn't read user values.
            Assert.Equal("system", s.App.Theme);   // Fix #7: default theme is now "system" (follow OS)
            // Hold the handle until after the assertions to keep the lock
            // active for the duration of the test.
            GC.KeepAlive(holder);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 8. Unknown future fields → silently ignored, known fields preserved
    // (positive case — verifies IgnoreUnmatchedProperties wiring)
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Load_UnknownFutureFields_AreIgnored_KnownFieldsPreserved()
    {
        var path = PathFor("future.yaml");
        File.WriteAllText(path,
            "schema_version: 2\n" +
            "future_field_we_have_not_invented_yet: 42\n" +
            "app:\n" +
            "  theme: dark\n" +
            "  some_field_from_v2_99: enabled\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.Equal("dark", s.App.Theme);
        // No backup — the file was structurally valid; we just dropped
        // unknown keys.
        Assert.True(File.Exists(path));
        var backups = Directory.GetFiles(_tempDir, "future.yaml.unloadable-*");
        Assert.Empty(backups);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 9. UTF-8 BOM at start of file → loads OK (positive case)
    // YamlDotNet handles BOM, but the read step has to tolerate it too.
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Load_FileWithUtf8Bom_LoadsSuccessfully()
    {
        var path = PathFor("bom.yaml");
        // Write UTF-8 BOM (EF BB BF) followed by valid YAML.
        var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
        bytes.AddRange(Encoding.UTF8.GetBytes(
            "app:\n  theme: dark\n  language: ru\n"));
        File.WriteAllBytes(path, bytes.ToArray());

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.Equal("dark", s.App.Theme);
        Assert.Equal("ru", s.App.Language);
        // BOM is structurally fine — no backup expected.
        Assert.True(File.Exists(path));
    }

    // ─────────────────────────────────────────────────────────────────────
    // 10. Garbage non-YAML root (scalar, not a mapping) → defaults + backup
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Load_NonMappingRoot_ReturnsDefaults_BackupsFile()
    {
        var path = PathFor("scalar-root.yaml");
        // A plain scalar is valid YAML but not valid AppSettings —
        // Parse() throws InvalidDataException via the structural check.
        File.WriteAllText(path, "this is just a string");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.False(File.Exists(path));
        var backups = Directory.GetFiles(_tempDir, "scalar-root.yaml.unloadable-*");
        Assert.Single(backups);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 11. Unrecognized top-level keys → defaults + backup (don't silently
    // accept a config from a different application)
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Load_NoRecognizedKeys_ReturnsDefaults_BackupsFile()
    {
        var path = PathFor("alien.yaml");
        // Valid YAML, valid mapping — but no recognized AppSettings keys.
        // This catches "user pasted a different program's config by mistake".
        File.WriteAllText(path,
            "completely:\n  unrelated:\n    config: file\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.False(File.Exists(path));
        var backups = Directory.GetFiles(_tempDir, "alien.yaml.unloadable-*");
        Assert.Single(backups);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 12. EnsureSane is idempotent — calling it twice on the same instance
    // doesn't drop or duplicate state
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void EnsureSane_IsIdempotent()
    {
        var s = new AppSettings();
        s.Vless.Servers.Add(new VlessServerEntry { Server = "a.example.com" });
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Url = "https://example.com/sub",
            Servers = { new VlessServerEntry { Server = "b.example.com" } }
        });
        s.CustomGroupApps["Discord"] = new List<string> { "Discord.exe" };

        var firstPass = s.EnsureSane();
        var firstServersCount = firstPass.Vless.Servers.Count;
        var firstSubCount = firstPass.App.Subscriptions.Count;

        var secondPass = firstPass.EnsureSane();

        // Same instance returned, same counts.
        Assert.Same(firstPass, secondPass);
        Assert.Equal(firstServersCount, secondPass.Vless.Servers.Count);
        Assert.Equal(firstSubCount, secondPass.App.Subscriptions.Count);
        Assert.Equal("a.example.com", secondPass.Vless.Servers[0].Server);
        Assert.Equal("b.example.com",
            secondPass.App.Subscriptions[0].Servers[0].Server);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 13. EnsureSane on null receiver → fresh defaults (covers the
    // "Deserialize returned null on whitespace YAML" path inside Parse)
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void EnsureSane_OnNullReceiver_ReturnsFreshDefaults()
    {
        AppSettings? nothing = null;
        var s = nothing.EnsureSane();
        AssertSane(s);
        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 14. Existing SettingsMigrator path still runs end-to-end through Load
    // (regression — SR-4 must not break SR's predecessor migration logic)
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Load_TriggersSchemaMigration_FromV1ToCurrent()
    {
        var path = PathFor("legacy.yaml");
        // schema_version 1 with legacy CustomDirectRules — migration step
        // converts to CustomRules with Action="direct". The full chain
        // also walks past v2→v3 (AM-1 + F-B), which is a no-op for this
        // fixture (no legacy custom_apps, no enabled subscriptions) but
        // must land us on the current schema version.
        File.WriteAllText(path,
            "schema_version: 1\n" +
            "app:\n" +
            "  custom_direct_rules:\n" +
            "    - type: ip_cidr\n" +
            "      value: 10.0.0.0/8\n" +
            "      comment: LAN\n" +
            "      enabled: true\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
        Assert.Single(s.App.CustomRules);
        Assert.Equal("direct", s.App.CustomRules[0].Action);
        Assert.Equal("ip_cidr", s.App.CustomRules[0].Type);
        Assert.Equal("LAN", s.App.CustomRules[0].Comment);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Bug-r9-I (2026-05-11): ExcludedApps round-trips through SettingsLoader
    // Save → Load → AppSettingsSane.EnsureSane intact. Without this pin a
    // future field rename or aliased serialiser tweak would silently break
    // the per-app exclusion persistence the user reported as missing
    // («каждый раз когда захожу отправляю фаерфокс в исключения... а когда
    // перезапускаю винду галочка на нем опять стоит»).
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Save_ThenLoad_PersistsExcludedApps()
    {
        var path = PathFor("excluded-apps-roundtrip.yaml");
        var s = new AppSettings
        {
            ExcludedApps = new List<string> { "firefox.exe", "msedge.exe" }
        };
        SettingsLoader.Save(s, path);

        // Inspect raw YAML for the alias — catches a future field-rename
        // that would migrate existing users to an empty list.
        var yaml = File.ReadAllText(path);
        Assert.Contains("excluded_apps:", yaml);
        Assert.Contains("firefox.exe", yaml);

        var reloaded = SettingsLoader.Load(path);
        AssertSane(reloaded);
        Assert.Equal(2, reloaded.ExcludedApps.Count);
        Assert.Contains("firefox.exe", reloaded.ExcludedApps);
        Assert.Contains("msedge.exe", reloaded.ExcludedApps);
    }

    [Fact]
    public void Load_PreV9IConfigWithoutExcludedApps_DefaultsToEmptyList()
    {
        // Forward-compat: a config from before Bug-r9-I shipped won't have
        // the excluded_apps key. EnsureSane must initialise an empty list
        // so the rest of the pipeline can iterate without NRE.
        var path = PathFor("legacy-no-excluded.yaml");
        File.WriteAllText(path,
            "schema_version: 2\n" +
            "app:\n" +
            "  routing_mode: split\n" +
            "  theme: dark\n");

        var s = SettingsLoader.Load(path);

        AssertSane(s);
        Assert.NotNull(s.ExcludedApps);
        Assert.Empty(s.ExcludedApps);
    }
}
