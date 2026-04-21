using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Forward migrates <see cref="AppSettings"/> from an older schema
/// version to the current one. Each migration step is a pure function
/// from settings@N to settings@N+1. Migrator chains them from the
/// file's recorded version up to <see cref="AppSettings.CurrentSchemaVersion"/>.
///
/// Called by <see cref="SettingsLoader.Parse"/> on load. After migration,
/// settings get re-saved so the next load starts clean.
///
/// v2.24.0 Level 3 of plans/vpnrouter-self-healing.md.
/// </summary>
public static class SettingsMigrator
{
    /// <summary>
    /// Walk from <paramref name="from"/> to <paramref name="to"/>,
    /// applying step functions. Returns the (same) settings instance
    /// with fields mutated + SchemaVersion updated.
    /// </summary>
    public static AppSettings Migrate(AppSettings settings, int from, int to, ILogger? logger = null)
    {
        if (from >= to) return settings;
        if (from < 0) from = 0;

        logger?.Information(
            "[SettingsMigrator] Migrating config from schema v{From} to v{To}",
            from, to);

        for (int v = from; v < to; v++)
        {
            logger?.Debug("[SettingsMigrator] Step v{V} -> v{Next}", v, v + 1);
            settings = v switch
            {
                0 => Migrate_0_to_1(settings),
                // Future: 1 => Migrate_1_to_2(settings),
                //         2 => Migrate_2_to_3(settings),
                _ => throw new InvalidOperationException(
                    $"No SettingsMigrator step defined for schema v{v} -> v{v + 1}. " +
                    $"This means the config file schema is newer than the running app — " +
                    $"downgrade the app or delete the config file.")
            };
            settings.SchemaVersion = v + 1;
        }

        return settings;
    }

    // ─── individual migration steps ──────────────────────────────────────

    /// <summary>
    /// Baseline: "no schema_version in yaml" -> schema_version 1. Nothing
    /// structural to change — v0 and v1 have the same field layout. We
    /// just tag the file with its version so future migrations have a
    /// reference point.
    /// </summary>
    private static AppSettings Migrate_0_to_1(AppSettings s)
    {
        return s;
    }
}
