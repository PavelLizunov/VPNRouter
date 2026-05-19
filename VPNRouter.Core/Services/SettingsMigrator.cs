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
                4 => Migrate_4_to_5(settings, logger),
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
    /// v2.32.3 (2026-05-17): aggressive one-shot sweep that strips
    /// <i>any</i> entry tagged as a known-bad placeholder by
    /// <see cref="PlaceholderGuard"/> from the persisted settings tree.
    /// Targets the legacy <see cref="VlessConfig.Server"/> scalar trio,
    /// the manual <see cref="VlessConfig.Servers"/> list, and every
    /// <see cref="SubscriptionEntry.Servers"/> list on every
    /// <see cref="AppConfig.Subscriptions"/> entry.
    ///
    /// <para>The Reality placeholder <c>DnT9hI…</c> leaked from old
    /// Android smoke-test code and has lived in real user configs for
    /// weeks. F-A / F-D / F-E catch it at start/validate/runtime but
    /// each layer rejects-or-bypasses; only this migrator step actually
    /// purges the bytes from disk so the user stops seeing the dead
    /// entry in the Servers tab.</para>
    ///
    /// <para>Conservative wipe semantics — never touches an entry that
    /// <see cref="PlaceholderGuard"/> reports as clean. For the scalar
    /// trio (<see cref="VlessConfig.Server"/> +
    /// <see cref="VlessConfig.Reality"/>), a single hit zeroes the
    /// related fields atomically (server, port, uuid, reality) because
    /// the bad pubkey usually surfaces together with bad server/uuid
    /// values from the same placeholder source; leaving the port or
    /// uuid behind would invite another silent half-config.</para>
    ///
    /// <para>If <see cref="VlessConfig.ActiveServer"/> pointed at an
    /// entry we just removed, clear it — caller / UI is expected to
    /// auto-pick or prompt. We deliberately don't auto-promote a
    /// surviving entry because the placeholder set is small and the
    /// risk of picking another stale entry as "active" outweighs the
    /// UX hit of one extra click.</para>
    ///
    /// <para>Returns the total number of items removed (scalar wipe =
    /// 1, plus one per list element). Idempotent — re-running on
    /// already-cleaned state returns 0 with no log noise.</para>
    /// </summary>
    public static int PruneKnownPlaceholders(AppSettings settings, ILogger? logger)
    {
        if (settings == null) return 0;
        int removed = 0;

        // (a) Scalar Vless.* trio — pubkey/sid/server.
        var vless = settings.Vless;
        if (vless != null)
        {
            var scalarHit = PlaceholderGuard.Inspect(
                vless.Reality?.PublicKey,
                vless.Reality?.ShortId,
                vless.Server);
            if (scalarHit != null)
            {
                var truncated = TruncateForLog(MatchedScalarValue(vless, scalarHit));
                logger?.Warning(
                    "[v2.32.3] PruneKnownPlaceholders: removed placeholder {Field} from {Location} (was: {Value})",
                    scalarHit,
                    "vless (scalar)",
                    truncated);
                vless.Server = string.Empty;
                vless.Port = 0;
                vless.Uuid = string.Empty;
                vless.Reality = new VlessRealityConfig();
                removed++;
            }

            // (b) Vless.Servers list.
            if (vless.Servers != null && vless.Servers.Count > 0)
            {
                var initial = vless.Servers.Count;
                vless.Servers.RemoveAll(entry =>
                {
                    var field = PlaceholderGuard.Inspect(entry);
                    if (field == null) return false;
                    var truncated = TruncateForLog(MatchedEntryValue(entry, field));
                    logger?.Warning(
                        "[v2.32.3] PruneKnownPlaceholders: removed placeholder {Field} from {Location} (was: {Value})",
                        field,
                        $"vless.servers[{(string.IsNullOrEmpty(entry?.Name) ? "(unnamed)" : entry!.Name)}]",
                        truncated);
                    return true;
                });
                removed += initial - vless.Servers.Count;
            }
        }

        // (c) Each subscription's Servers list.
        var subs = settings.App?.Subscriptions;
        if (subs != null)
        {
            foreach (var sub in subs)
            {
                if (sub?.Servers == null || sub.Servers.Count == 0) continue;
                var initial = sub.Servers.Count;
                sub.Servers.RemoveAll(entry =>
                {
                    var field = PlaceholderGuard.Inspect(entry);
                    if (field == null) return false;
                    var truncated = TruncateForLog(MatchedEntryValue(entry, field));
                    logger?.Warning(
                        "[v2.32.3] PruneKnownPlaceholders: removed placeholder {Field} from {Location} (was: {Value})",
                        field,
                        $"app.subscriptions[{(string.IsNullOrEmpty(sub.Name) ? "(unnamed)" : sub.Name)}].servers[{(string.IsNullOrEmpty(entry?.Name) ? "(unnamed)" : entry!.Name)}]",
                        truncated);
                    return true;
                });
                removed += initial - sub.Servers.Count;
            }
        }

        // (d) ActiveServer pointed at something we removed → clear it.
        // Caller / UI handles graceful replacement (we deliberately do
        // NOT auto-pick — the surviving entries may be from a different
        // subscription / different mode than the user expected).
        if (vless != null && !string.IsNullOrEmpty(vless.ActiveServer))
        {
            var effective = vless.GetEffectiveServers();
            var stillPresent = effective.Any(s =>
                string.Equals(s.Name, vless.ActiveServer, StringComparison.OrdinalIgnoreCase));
            if (!stillPresent)
            {
                logger?.Warning(
                    "[v2.32.3] PruneKnownPlaceholders: vless.active_server '{Active}' was on a pruned entry; cleared",
                    vless.ActiveServer);
                vless.ActiveServer = string.Empty;
            }
        }

        return removed;
    }

    /// <summary>Truncate a placeholder value for log output (full value
    /// is reconstructable via the static hash-sets in
    /// <see cref="ConfigSanityCheck"/>; here we only need enough to
    /// disambiguate which fingerprint matched).</summary>
    private static string TruncateForLog(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "(empty)";
        return v.Length <= 16 ? v : $"{v[..8]}…{v[^4..]}";
    }

    private static string MatchedScalarValue(VlessConfig v, string field) => field switch
    {
        "reality.public_key" => v.Reality?.PublicKey ?? string.Empty,
        "reality.short_id"   => v.Reality?.ShortId   ?? string.Empty,
        "server"             => v.Server                ?? string.Empty,
        _ => string.Empty,
    };

    private static string MatchedEntryValue(VlessServerEntry? e, string field)
    {
        if (e == null) return string.Empty;
        return field switch
        {
            "reality.public_key" => e.Reality?.PublicKey ?? string.Empty,
            "reality.short_id"   => e.Reality?.ShortId   ?? string.Empty,
            "server"             => e.Server                ?? string.Empty,
            _ => string.Empty,
        };
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

        // BR-4 (brat 2026-05-19): preserve the entry referenced by
        // vless.active_server even when it doesn't match a subscription
        // server key. That entry is the user's intentional manual
        // fallback — wiping it broke brat's connect-via-Ignore path on
        // r5. Original F-B heuristic assumed every vless.servers[] entry
        // outside the subscription list was an auto-migrated duplicate
        // from the stas-class shadow-override bug; in practice users
        // also add manual servers via the Servers tab, and those land
        // in the same list. Use ActiveServer membership as the user-
        // intent signal: if the user selected this row as active, it's
        // not a stale auto-migrated leftover.
        var activeServerName = vless.ActiveServer ?? string.Empty;

        var keep = new List<VlessServerEntry>(vless.Servers.Count);
        var removed = new List<VlessServerEntry>();
        foreach (var srv in vless.Servers)
        {
            if (srv == null) continue;
            var matchesSub = subKeys.Contains(MakeServerKey(srv));
            var isActive = !string.IsNullOrEmpty(activeServerName)
                && string.Equals(srv.Name, activeServerName, StringComparison.OrdinalIgnoreCase);
            if (matchesSub || isActive)
                keep.Add(srv);
            else
                removed.Add(srv);
        }

        if (removed.Count == 0) return;

        foreach (var r in removed)
        {
            logger?.Warning(
                "[SettingsMigrator] Removed orphan vless.servers entry: " +
                "{Name} ({Server}:{Port}) — not in any enabled subscription and not " +
                "referenced by vless.active_server (BR-4: brat 2026-05-19)",
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

    /// <summary>
    /// v2.35.0-r5 Wave 39 (2026-05-19): introduces
    /// <see cref="AppConfig.DnsLeakLockdown"/> — a firewall-level outbound
    /// block on UDP/53, TCP/53, and TCP/853 to prevent the Windows DNS
    /// Client multi-resolver race from leaking queries to ISP resolvers
    /// despite our SMHNR/ParallelAAAA registry hardening.
    ///
    /// <para>Fresh installs inherit the C# default <c>true</c> (active
    /// protection out of the box). For users upgrading from an older
    /// schema (anyone whose yaml records a schema version &lt; 5), we
    /// flip the flag to <c>false</c> so we don't surprise people running
    /// a local DNS proxy on a non-loopback IP (dnscrypt-proxy on a LAN
    /// address, AdGuard Home on a sibling NIC, etc.). They can opt in
    /// later via the Settings toggle once they understand the
    /// implications.</para>
    ///
    /// <para>Detection: the schema-version walker only enters this step
    /// when the file's recorded version was &lt; 5, which by definition
    /// means "existing user from before the field existed". Fresh
    /// installs never run this step because <see cref="AppSettings.SchemaVersion"/>
    /// defaults to <see cref="AppSettings.CurrentSchemaVersion"/>.</para>
    ///
    /// <para>Idempotent: re-running on a v5 settings tree is a no-op
    /// because the migrator's outer loop only fires steps whose source
    /// version is below the target. The body is a single field write so
    /// even a manually-triggered re-run is harmless.</para>
    /// </summary>
    private static AppSettings Migrate_4_to_5(AppSettings s, ILogger? logger)
    {
        if (s.App == null)
        {
            // Defensive — shouldn't happen because AppSettings ctor
            // initialises App, but the migrator chain shouldn't NRE on
            // a hand-edited yaml with a stripped section.
            logger?.Warning(
                "[SettingsMigrator] v4->v5 (Wave 39): settings.App was null, " +
                "skipping DnsLeakLockdown setup");
            return s;
        }

        // BR-5 (brat 2026-05-19): flipped from opt-out (false) to
        // opt-in (true) for upgrade users. Original Wave 39 logic was
        // cautious about users running a local DNS proxy on non-
        // loopback IPs (dnscrypt-proxy on a LAN address, AdGuard Home
        // on a sibling NIC) — those installations would suddenly see
        // their DNS blocked. In practice that's a small minority and
        // brat-2026-05-19 surfaced the cost of the opt-out default:
        //   - DNS queries leaked to RU ISP resolver (95.85.16.212
        //     visible in singbox.log dns: exchanged trace) because
        //     Windows DNS Client kept racing the configured resolvers
        //     in parallel despite SMHNR + ParallelAAAA registry hardening.
        //   - The user did not know to flip the Settings toggle, so the
        //     protection that was the whole point of Wave 39 never
        //     activated.
        //
        // Flipping the default protects everyone on upgrade with the
        // smaller cost (LAN-proxy users see the block, follow up with
        // a support ping, and disable via Settings). The toggle still
        // exists at Settings → Leak Protection → "Block DNS outside
        // VPN" so power users can opt out at any time.
        s.App.DnsLeakLockdown = true;
        logger?.Information(
            "[SettingsMigrator] v4->v5 (Wave 39 + BR-5): set DnsLeakLockdown=true for " +
            "pre-Wave-39 config (default-on protects against the brat-2026-05-19 RU " +
            "ISP DNS leak class; LAN-proxy users can disable via Settings → Leak Protection)");
        return s;
    }
}
