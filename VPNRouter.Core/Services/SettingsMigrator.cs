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
                1 => Migrate_1_to_2(settings, logger),
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

    /// <summary>
    /// v2.30.0: migrate <see cref="AppConfig.CustomDirectRules"/>
    /// (v2.29.0-r4..r8 schema) to <see cref="AppConfig.CustomRules"/>
    /// with explicit Action="direct". Preserves Type, Value, Comment,
    /// Enabled. After migration the legacy field is left empty in
    /// memory but the property is retained on the AppConfig class for
    /// back-compat with v2.29 binaries that may share the same yaml
    /// file (no-op for them).
    ///
    /// <para>Idempotent: if <see cref="AppConfig.CustomRules"/> is
    /// already populated (v2.30+ user already migrated), skips. If
    /// <see cref="AppConfig.CustomDirectRules"/> is empty, also skips.</para>
    /// </summary>
    private static AppSettings Migrate_1_to_2(AppSettings s, ILogger? logger)
    {
        if (s.App.CustomRules.Count > 0)
        {
            logger?.Information(
                "[SettingsMigrator] v1->v2: CustomRules already populated ({Count}), " +
                "skipping migration of CustomDirectRules", s.App.CustomRules.Count);
            return s;
        }

        if (s.App.CustomDirectRules.Count == 0)
        {
            // Nothing to migrate — first-run / clean install.
            return s;
        }

        var migrated = s.App.CustomDirectRules
            .Select(legacy => new CustomRule
            {
                Action = "direct",  // legacy CustomDirectRule was direct-only
                Type = legacy.Type,
                Value = legacy.Value,
                Comment = string.IsNullOrEmpty(legacy.Comment)
                    ? string.Empty
                    : legacy.Comment,
                Enabled = legacy.Enabled,
            })
            .ToList();

        s.App.CustomRules = migrated;
        // Empty the legacy list so future loads don't double-migrate.
        // Keep the property on AppConfig (class shape stays); just clear
        // the data.
        s.App.CustomDirectRules = new List<CustomDirectRule>();

        logger?.Information(
            "[SettingsMigrator] v1->v2: migrated {Count} CustomDirectRules to CustomRules",
            migrated.Count);
        return s;
    }
}
