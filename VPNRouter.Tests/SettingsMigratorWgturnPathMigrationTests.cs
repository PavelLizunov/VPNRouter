using System;
using System.IO;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// W-2 (2026-05-12): pin the v3→v4 schema migration that relocates
/// the wgturn-cli binary out of the shared <c>bin/</c> directory into
/// the dedicated <c>wgturn/bin/</c> subtree (parallel to <c>zapret/</c>,
/// <c>tg-proxy/</c>) ahead of the W-1 on-demand download flow.
///
/// <para>Source of the change: v2.32.1 was the first release to bundle
/// wgturn-cli.exe in the shared <c>bin/</c>. The W-1 chip introduces an
/// on-demand <see cref="VPNRouter.Core.Services.EmergencyChannel"/>
/// downloader that owns its own private subtree (matching the
/// zapret / tg-proxy layout). This test pins the one-shot relocation
/// of any pre-existing binary so the new
/// <see cref="AppPaths.WgturnCliExePath"/> resolves to a real file on
/// first launch after upgrade.</para>
///
/// <para>Migration is best-effort + idempotent — failures during
/// <see cref="File.Move(string, string)"/> are swallowed so settings
/// load never blocks on IO; the W-1 downloader will (re)fetch into
/// the new location if it's missing. See
/// <c>plans/wgturn-on-demand-download.md</c> §3 + §5 for the full
/// design.</para>
/// </summary>
public class SettingsMigratorWgturnPathMigrationTests
{
    private static string LegacyExeName =>
        OperatingSystem.IsWindows() ? "wgturn-cli.exe" : "wgturn-cli";

    /// <summary>Helper — run <paramref name="body"/> with
    /// <see cref="AppPaths.DataDir"/> rebound to a fresh temp directory.
    /// Restores the previous DataDir + cleans the temp tree on exit.
    /// </summary>
    private static void WithTempDataDir(Action<string> body)
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"vpnrouter-migr-w2-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var previous = AppPaths.DataDir;
        try
        {
            AppPaths.OverrideDataDir(tempDir);
            // Pre-create shared bin/ — the legacy location the test
            // populates before invoking the migrator.
            Directory.CreateDirectory(AppPaths.BinDir);
            body(tempDir);
        }
        finally
        {
            AppPaths.OverrideDataDir(previous);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch { /* test-cleanup is best-effort */ }
        }
    }

    [Fact]
    public void Migrate_3_to_4_MovesLegacyWgturnCli_WhenLegacyExists_NewMissing()
    {
        WithTempDataDir(_ =>
        {
            var legacyPath = Path.Combine(AppPaths.BinDir, LegacyExeName);
            File.WriteAllBytes(legacyPath, new byte[] { 0x4D, 0x5A }); // MZ header (fake exe)

            var s = new AppSettings { SchemaVersion = 3 };
            var migrated = SettingsMigrator.Migrate(s, from: 3, to: 4);

            Assert.Equal(4, migrated.SchemaVersion);
            Assert.False(File.Exists(legacyPath),
                "legacy bin/wgturn-cli must be moved away");
            Assert.True(File.Exists(AppPaths.WgturnCliExePath),
                "new wgturn/bin/wgturn-cli must exist after migration");
            // Content survived the move.
            Assert.Equal(new byte[] { 0x4D, 0x5A }, File.ReadAllBytes(AppPaths.WgturnCliExePath));
        });
    }

    [Fact]
    public void Migrate_3_to_4_NoOp_WhenLegacyMissing()
    {
        WithTempDataDir(_ =>
        {
            // No legacy binary present — clean install / fresh upgrade
            // where the user never had v2.32.1 bundle installed.
            var legacyPath = Path.Combine(AppPaths.BinDir, LegacyExeName);
            Assert.False(File.Exists(legacyPath));

            var s = new AppSettings { SchemaVersion = 3 };
            var migrated = SettingsMigrator.Migrate(s, from: 3, to: 4);

            Assert.Equal(4, migrated.SchemaVersion);
            Assert.False(File.Exists(legacyPath));
            Assert.False(File.Exists(AppPaths.WgturnCliExePath),
                "new path must NOT be auto-created when there's nothing to migrate");
        });
    }

    [Fact]
    public void Migrate_3_to_4_PreservesNew_WhenBothExist()
    {
        WithTempDataDir(_ =>
        {
            // Defensive — if both legacy AND new locations have a
            // binary (e.g. user ran a custom downloader before
            // upgrading), the migrator must not overwrite the new
            // location. W-1 owns ProgramData\VPNRouter\wgturn\bin\
            // exclusively post-migration.
            var legacyPath = Path.Combine(AppPaths.BinDir, LegacyExeName);
            File.WriteAllBytes(legacyPath, new byte[] { 0x01, 0x01 });

            Directory.CreateDirectory(AppPaths.WgturnBinDir);
            File.WriteAllBytes(AppPaths.WgturnCliExePath, new byte[] { 0x02, 0x02 });

            var s = new AppSettings { SchemaVersion = 3 };
            SettingsMigrator.Migrate(s, from: 3, to: 4);

            // New file is untouched.
            Assert.Equal(new byte[] { 0x02, 0x02 }, File.ReadAllBytes(AppPaths.WgturnCliExePath));
            // Legacy file is also untouched (we don't delete on conflict).
            Assert.True(File.Exists(legacyPath));
            Assert.Equal(new byte[] { 0x01, 0x01 }, File.ReadAllBytes(legacyPath));
        });
    }

    [Fact]
    public void Migrate_3_to_4_BumpsSchemaVersionTo4()
    {
        WithTempDataDir(_ =>
        {
            var s = new AppSettings { SchemaVersion = 3 };
            // Wave 39 (2026-05-19) — CurrentSchemaVersion bumped 4→5.
            // The 3-to-4 migration step still exists as an intermediate
            // hop (wgturn-cli binary path move); use the explicit `to: 4`
            // upper bound so we exercise ONLY this step and don't roll
            // forward to the new v5 (DnsLeakLockdown) step too.
            var migrated = SettingsMigrator.Migrate(s, from: 3, to: 4);

            Assert.Equal(4, migrated.SchemaVersion);
            // Sanity (Wave 39+ no longer ties this step's target to
            // CurrentSchemaVersion; v5 is one step beyond).
        });
    }

    [Fact]
    public void Migrate_3_to_4_Idempotent()
    {
        WithTempDataDir(_ =>
        {
            var legacyPath = Path.Combine(AppPaths.BinDir, LegacyExeName);
            File.WriteAllBytes(legacyPath, new byte[] { 0x4D, 0x5A });

            var s = new AppSettings { SchemaVersion = 3 };
            var firstPass = SettingsMigrator.Migrate(s, from: 3, to: 4);

            Assert.Equal(4, firstPass.SchemaVersion);
            Assert.False(File.Exists(legacyPath));
            Assert.True(File.Exists(AppPaths.WgturnCliExePath));

            // Second pass — SchemaVersion already at 4 so Migrate is
            // guarded by `from >= to` and returns immediately. Even if
            // we forced the step to run again it would be a no-op
            // because the legacy file is gone.
            var secondPass = SettingsMigrator.Migrate(firstPass, from: firstPass.SchemaVersion, to: 4);
            Assert.Equal(4, secondPass.SchemaVersion);
            Assert.True(File.Exists(AppPaths.WgturnCliExePath));
        });
    }

    [Fact]
    public void Migrate_3_to_4_MovesVersionTxt_WhenLegacyVersionFileExists()
    {
        WithTempDataDir(_ =>
        {
            // Pre-existing version stamp from an earlier hand-installed
            // bundle should ride along with the binary into the new
            // wgturn/ subtree.
            var legacyVer = Path.Combine(AppPaths.BinDir, "wgturn-cli-version.txt");
            File.WriteAllText(legacyVer, "0.3.0\n");

            var s = new AppSettings { SchemaVersion = 3 };
            SettingsMigrator.Migrate(s, from: 3, to: 4);

            Assert.False(File.Exists(legacyVer));
            Assert.True(File.Exists(AppPaths.WgturnVersionPath));
            Assert.Equal("0.3.0\n", File.ReadAllText(AppPaths.WgturnVersionPath));
        });
    }

    [Fact]
    public void Migrate_3_to_4_HandlesIOErrors_DoesNotThrow()
    {
        WithTempDataDir(_ =>
        {
            // Simulate an environment where the new directory can't be
            // created — e.g. a file (not directory) sits in place of
            // `wgturn/`. Best-effort migration must swallow the error
            // and still bump the schema version so we don't get stuck
            // looping on every load.
            var legacyPath = Path.Combine(AppPaths.BinDir, LegacyExeName);
            File.WriteAllBytes(legacyPath, new byte[] { 0x4D, 0x5A });

            // Block creation of wgturn/ by writing a regular file at
            // that path.
            File.WriteAllText(AppPaths.WgturnDir, "blocker");

            var s = new AppSettings { SchemaVersion = 3 };
            var migrated = SettingsMigrator.Migrate(s, from: 3, to: 4);

            // No throw + schema bumped.
            Assert.Equal(4, migrated.SchemaVersion);
            // Legacy file still in place because the move failed silently.
            Assert.True(File.Exists(legacyPath));

            // Cleanup — remove the blocker so WithTempDataDir's tree
            // delete succeeds.
            try { File.Delete(AppPaths.WgturnDir); } catch { }
        });
    }
}
