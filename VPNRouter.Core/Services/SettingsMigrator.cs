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
                2 => Migrate_2_to_3(settings, logger),
                3 => Migrate_3_to_4(settings, logger),
                _ => throw new InvalidOperationException(
                    $"No SettingsMigrator step defined for schema v{v} -> v{v + 1}. " +
                    $"This means the config file schema is newer than the running app — " +
                    $"downgrade the app or delete the config file.")
            };
            settings.SchemaVersion = v + 1;
        }

        return settings;
    }

    /// <summary>
    /// Cleanup orphan <see cref="VlessConfig.Servers"/> entries that
    /// aren't part of any enabled subscription. Closes the
    /// stas-class shadow-override bug at the config-load layer
    /// (F-B in <c>plans/r10-stas-confirmed-and-apps-2mode.md</c>).
    ///
    /// <para>Triggered as part of v2→v3 migration AND callable
    /// independently (idempotent — no-op when there's no
    /// subscription mode active OR when <c>vless.servers</c> is
    /// already empty).</para>
    ///
    /// <para>Heuristic: when <i>any</i> enabled subscription has
    /// at least one server, treat <see cref="VlessConfig.Servers"/>
    /// as a legacy list and strip entries that don't appear in any
    /// enabled subscription's server list (matched by the composite
    /// key <c>name|server|port|uuid</c>). When no enabled
    /// subscription exists, treat <see cref="VlessConfig.Servers"/>
    /// as the user's direct-mode manual list and leave it
    /// untouched.</para>
    ///
    /// <para>If <see cref="VlessConfig.ActiveServer"/> pointed at a
    /// removed entry, it gets reassigned to the first surviving
    /// entry's name (or cleared if nothing remains). Both branches
    /// keep the field in a consistent state.</para>
    /// </summary>
    /// <remarks>Internal so unit tests can call directly.</remarks>
    internal static void CleanupOrphanVlessServers(AppSettings settings, ILogger? logger = null)
    {
        var app = settings.App;
        var vless = settings.Vless;
        if (app == null || vless == null) return;

        // Are any subscriptions enabled with at least one server? Only
        // then do we treat `vless.servers` as legacy. Otherwise the
        // user may still be in direct VLESS mode and `vless.servers`
        // is the source of truth.
        var enabledSubs = app.Subscriptions?
            .Where(s => s != null && s.Enabled && s.Servers != null && s.Servers.Count > 0)
            .ToList() ?? new List<SubscriptionEntry>();
        if (enabledSubs.Count == 0)
        {
            return;
        }

        if (vless.Servers == null || vless.Servers.Count == 0)
        {
            return;
        }

        // Build a key-set of subscription-owned servers. We don't
        // collapse on (server,port,uuid) alone because the same IP +
        // port may legitimately be served under different names /
        // different uuids — keep the full composite key.
        var subKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in enabledSubs)
        {
            foreach (var srv in sub.Servers!)
            {
                if (srv == null) continue;
                subKeys.Add(MakeServerKey(srv));
            }
        }

        var keep = new List<VlessServerEntry>(vless.Servers.Count);
        var removed = new List<VlessServerEntry>();
        foreach (var srv in vless.Servers)
        {
            if (srv == null) continue;
            if (subKeys.Contains(MakeServerKey(srv)))
                keep.Add(srv);
            else
                removed.Add(srv);
        }

        if (removed.Count == 0) return;

        foreach (var r in removed)
        {
            logger?.Warning(
                "[SettingsMigrator] Removed orphan vless.servers entry: " +
                "{Name} ({Server}:{Port}) — not in any enabled subscription",
                string.IsNullOrEmpty(r.Name) ? "(unnamed)" : r.Name,
                r.Server,
                r.Port);
        }

        vless.Servers = keep;

        // Reset ActiveServer when its target was removed.
        if (!string.IsNullOrEmpty(vless.ActiveServer))
        {
            var stillPresent = keep.Any(s =>
                string.Equals(s.Name, vless.ActiveServer, StringComparison.OrdinalIgnoreCase));
            if (!stillPresent)
            {
                var previous = vless.ActiveServer;
                vless.ActiveServer = keep.FirstOrDefault()?.Name ?? string.Empty;
                logger?.Information(
                    "[SettingsMigrator] vless.active_server '{Previous}' was orphaned; " +
                    "reassigned to '{New}'",
                    previous,
                    string.IsNullOrEmpty(vless.ActiveServer) ? "(none)" : vless.ActiveServer);
            }
        }
    }

    /// <summary>Composite identity key for orphan detection. Case-
    /// insensitive across all components (host casing is irrelevant;
    /// uuid is conventionally lower-case but YAML may carry mixed).
    /// </summary>
    private static string MakeServerKey(VlessServerEntry s)
    {
        var name = s.Name ?? string.Empty;
        var server = s.Server ?? string.Empty;
        var uuid = s.Uuid ?? string.Empty;
        return $"{name}|{server}|{s.Port}|{uuid}";
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

    /// <summary>
    /// v2.32.x (AM-1 + F-B, 2026-05-11): bundle two related changes
    /// that both touch settings load:
    ///
    /// <list type="number">
    /// <item><b>AM-1</b> — populate
    /// <see cref="AppConfig.RoutingAppsInclude"/> from
    /// <see cref="AppConfig.CustomApps"/> on first-time-after-upgrade.
    /// <see cref="AppConfig.RoutingAppsMode"/> stays at its default
    /// ("include") so legacy users see no behaviour change. The
    /// <see cref="AppConfig.CustomApps"/> list is NOT cleared — it's
    /// still consumed by <see cref="VpnEngine"/> as a process-name
    /// source for the legacy <see cref="Profile.Processes"/> path and
    /// removing it would silently break that fallback. Idempotent:
    /// if <see cref="AppConfig.RoutingAppsInclude"/> already has
    /// entries we treat it as already-migrated and skip the copy.</item>
    ///
    /// <item><b>F-B</b> — one-shot cleanup of orphan
    /// <see cref="VlessConfig.Servers"/> entries when a subscription
    /// is active. Closes the stas-class shadow-override bug at the
    /// config-load layer. See
    /// <c>plans/r10-stas-confirmed-and-apps-2mode.md</c> §1 Fix-B for
    /// the full context. The cleanup is implemented in
    /// <see cref="CleanupOrphanVlessServers"/> so the same routine can
    /// be invoked outside the migrator (defensive runtime sanity
    /// passes, manual reset, tests).</item>
    /// </list>
    /// </summary>
    private static AppSettings Migrate_2_to_3(AppSettings s, ILogger? logger)
    {
        // AM-1: seed the include list from legacy top-level CustomApps
        // (yaml: `custom_apps:`) if the new field is empty. Migration
        // is one-shot — once RoutingAppsInclude is non-empty, future
        // loads must respect user-driven edits.
        if (s.App.RoutingAppsInclude.Count == 0 && (s.CustomApps?.Count ?? 0) > 0)
        {
            // De-dupe with OrdinalIgnoreCase but preserve casing for the
            // surviving entries — sing-box `process_name` matching is
            // case-sensitive (see VPNRouter.Core/CLAUDE.md), so we never
            // mutate the user's casing here.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seeded = new List<string>(s.CustomApps!.Count);
            foreach (var app in s.CustomApps)
            {
                if (string.IsNullOrWhiteSpace(app)) continue;
                if (seen.Add(app))
                    seeded.Add(app);
            }
            if (seeded.Count > 0)
            {
                s.App.RoutingAppsInclude = seeded;
                logger?.Information(
                    "[SettingsMigrator] v2->v3 (AM-1): seeded routing_apps_include " +
                    "with {Count} entries from legacy custom_apps",
                    seeded.Count);
            }
        }

        // Ensure mode is canonical even when migrating fresh installs.
        if (string.IsNullOrWhiteSpace(s.App.RoutingAppsMode))
            s.App.RoutingAppsMode = "include";

        // F-B: legacy vless.servers cleanup. Idempotent.
        CleanupOrphanVlessServers(s, logger);

        return s;
    }

    /// <summary>
    /// v2.32.2 (W-2, 2026-05-12): wgturn-cli binary moved from the
    /// shared <c>bin/</c> directory into a dedicated <c>wgturn/bin/</c>
    /// subtree (parallel to <c>zapret/</c>, <c>tg-proxy/</c>) ahead of
    /// the W-1 on-demand download flow. v2.32.1 was the first release
    /// to bundle <c>wgturn-cli.exe</c> in the shared <c>bin/</c>; any
    /// pre-existing binary + version stamp must be relocated so the
    /// new <see cref="AppPaths.WgturnCliExePath"/> resolves to a real
    /// file on first launch after upgrade.
    ///
    /// <para>Best-effort + idempotent — every IO operation is wrapped
    /// in <c>try/catch</c> so a locked / missing source never throws
    /// out of the migrator (we tolerate a stale legacy binary; the
    /// W-1 downloader will (re)fetch into the new location if it's
    /// missing). Re-running the step on already-migrated state is a
    /// no-op because the source files no longer exist.</para>
    ///
    /// <para>No yaml schema changes — only on-disk layout. Bumping
    /// the schema version is the trigger for the one-shot move; the
    /// settings object itself is returned unchanged.</para>
    /// </summary>
    private static AppSettings Migrate_3_to_4(AppSettings s, ILogger? logger)
    {
        // 1. Relocate legacy bin/wgturn-cli[.exe] -> wgturn/bin/wgturn-cli[.exe]
        try
        {
            var legacyExeName = OperatingSystem.IsWindows() ? "wgturn-cli.exe" : "wgturn-cli";
            var legacyPath = Path.Combine(AppPaths.BinDir, legacyExeName);
            var newPath = AppPaths.WgturnCliExePath;

            if (File.Exists(legacyPath) && !File.Exists(newPath))
            {
                var newDir = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrEmpty(newDir))
                    Directory.CreateDirectory(newDir);
                File.Move(legacyPath, newPath);
                logger?.Information(
                    "[SettingsMigrator] v3->v4 (W-2): moved legacy wgturn-cli " +
                    "from {Legacy} to {New}",
                    legacyPath, newPath);
            }
        }
        catch (Exception ex)
        {
            // Best-effort — never block settings load on a migration IO
            // hiccup. W-1 downloader will reseed on demand.
            logger?.Warning(ex,
                "[SettingsMigrator] v3->v4 (W-2): wgturn-cli binary relocation " +
                "skipped due to IO error");
        }

        // 2. Relocate any pre-existing version stamp written by an
        //    earlier hand-installed copy. Older bundles wrote
        //    bin/wgturn-cli-version.txt next to the exe.
        try
        {
            var legacyVer = Path.Combine(AppPaths.BinDir, "wgturn-cli-version.txt");
            var newVer = AppPaths.WgturnVersionPath;
            if (File.Exists(legacyVer) && !File.Exists(newVer))
            {
                var newDir = Path.GetDirectoryName(newVer);
                if (!string.IsNullOrEmpty(newDir))
                    Directory.CreateDirectory(newDir);
                File.Move(legacyVer, newVer);
                logger?.Information(
                    "[SettingsMigrator] v3->v4 (W-2): moved legacy version stamp " +
                    "from {Legacy} to {New}",
                    legacyVer, newVer);
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex,
                "[SettingsMigrator] v3->v4 (W-2): wgturn-cli version stamp " +
                "relocation skipped due to IO error");
        }

        return s;
    }
}
