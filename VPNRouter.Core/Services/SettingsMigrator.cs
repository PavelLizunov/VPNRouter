using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class SettingsMigrator
{
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
                5 => Migrate_5_to_6(settings, logger),
                6 => Migrate_6_to_7(settings, logger),
                7 => Migrate_7_to_8(settings, logger),
                _ => throw new InvalidOperationException(
                    $"No SettingsMigrator step defined for schema v{v} -> v{v + 1}. " +
                    $"This means the config file schema is newer than the running app — " +
                    $"downgrade the app or delete the config file.")
            };
            settings.SchemaVersion = v + 1;
        }

        return settings;
    }

    public static int PruneKnownPlaceholders(AppSettings settings, ILogger? logger)
    {
        if (settings == null) return 0;
        int removed = 0;

        var vless = settings.Vless;
        if (vless != null)
        {
            var scalarHit = PlaceholderDefense.Inspect(
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

            if (vless.Servers != null && vless.Servers.Count > 0)
            {
                var initial = vless.Servers.Count;
                vless.Servers.RemoveAll(entry =>
                {
                    var field = PlaceholderDefense.Inspect(entry);
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

        var subs = settings.App?.Subscriptions;
        if (subs != null)
        {
            foreach (var sub in subs)
            {
                if (sub?.Servers == null || sub.Servers.Count == 0) continue;
                var initial = sub.Servers.Count;
                sub.Servers.RemoveAll(entry =>
                {
                    var field = PlaceholderDefense.Inspect(entry);
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

    internal static void CleanupOrphanVlessServers(AppSettings settings, ILogger? logger = null)
    {
        var app = settings.App;
        var vless = settings.Vless;
        if (app == null || vless == null) return;

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

        var subKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in enabledSubs)
        {
            foreach (var srv in sub.Servers!)
            {
                if (srv == null) continue;
                subKeys.Add(MakeServerKey(srv));
            }
        }

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

    private static string MakeServerKey(VlessServerEntry s)
    {
        var name = s.Name ?? string.Empty;
        var server = s.Server ?? string.Empty;
        var uuid = s.Uuid ?? string.Empty;
        return $"{name}|{server}|{s.Port}|{uuid}";
    }

    private static AppSettings Migrate_0_to_1(AppSettings s)
    {
        return s;
    }

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
            return s;
        }

        var migrated = s.App.CustomDirectRules
            .Select(legacy => new CustomRule
            {
                Action = "direct",
                Type = legacy.Type,
                Value = legacy.Value,
                Comment = string.IsNullOrEmpty(legacy.Comment)
                    ? string.Empty
                    : legacy.Comment,
                Enabled = legacy.Enabled,
            })
            .ToList();

        s.App.CustomRules = migrated;
        s.App.CustomDirectRules = new List<CustomDirectRule>();

        logger?.Information(
            "[SettingsMigrator] v1->v2: migrated {Count} CustomDirectRules to CustomRules",
            migrated.Count);
        return s;
    }

    private static AppSettings Migrate_2_to_3(AppSettings s, ILogger? logger)
    {
        if (s.App.RoutingAppsInclude.Count == 0 && (s.CustomApps?.Count ?? 0) > 0)
        {
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

        if (string.IsNullOrWhiteSpace(s.App.RoutingAppsMode))
            s.App.RoutingAppsMode = "include";

        CleanupOrphanVlessServers(s, logger);

        return s;
    }

    private static AppSettings Migrate_3_to_4(AppSettings s, ILogger? logger)
    {
        try
        {
            var legacyExeName = OperatingSystem.IsWindows() ? "wgturn-cli.exe" : "wgturn-cli";
            var legacyPath = Path.Combine(AppPaths.BinDir, legacyExeName);
            if (File.Exists(legacyPath))
            {
                File.Delete(legacyPath);
            }
            var legacyVer = Path.Combine(AppPaths.BinDir, "wgturn-cli-version.txt");
            if (File.Exists(legacyVer))
            {
                File.Delete(legacyVer);
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[SettingsMigrator] v3->v4: legacy wgturn cleanup skipped");
        }

        return s;
    }

    private static AppSettings Migrate_4_to_5(AppSettings s, ILogger? logger)
    {
        if (s.App == null)
        {
            logger?.Warning(
                "[SettingsMigrator] v4->v5 (Wave 39): settings.App was null, " +
                "skipping DnsLeakLockdown setup");
            return s;
        }

        s.App.DnsLeakLockdown = false;
        logger?.Information(
            "[SettingsMigrator] v4->v5 (Wave 39 + BR-10): set DnsLeakLockdown=false for " +
            "pre-Wave-39 config (opt-in by default — sing-box DNS routing via VLESS:443 " +
            "is the primary leak protection; user enables firewall block via Settings → " +
            "Leak Protection if desired)");
        return s;
    }

    private static AppSettings Migrate_5_to_6(AppSettings s, ILogger? logger)
    {
        if (s.Tun != null && s.Tun.Mtu == 9000)
        {
            s.Tun.Mtu = 1280;
            logger?.Information(
                "[SettingsMigrator] v5->v6: lowered TUN MTU 9000 -> 1280 (jumbo MTU broke " +
                "HTTP/2 over TCP-only proxies -> browser ERR_CONNECTION_CLOSED on YouTube; " +
                "1280 = IPv6 minimum; narrower underlays still require measurement)");
        }
        return s;
    }

    private static AppSettings Migrate_6_to_7(AppSettings s, ILogger? logger)
    {
        if (s.Tun != null && (s.Tun.Mtu == 1500 || s.Tun.Mtu == 9000))
        {
            var old = s.Tun.Mtu;
            s.Tun.Mtu = 1280;
            logger?.Information(
                "[SettingsMigrator] v6->v7: lowered legacy/stuck TUN MTU {Old} -> 1280 " +
                "(jumbo/large MTU blackholes PMTUD on the encapsulated path -> stalled " +
                "DoH/joins -> Roblox 277; deliberate custom values are preserved)", old);
        }
        return s;
    }

    private static AppSettings Migrate_7_to_8(AppSettings s, ILogger? logger)
    {
        if (s.Tun == null) return s;

        if (s.Tun.Mtu == 1500 ||
            s.Tun.Mtu < TunSettings.MinimumMtu ||
            s.Tun.Mtu > TunSettings.MaximumMtu)
        {
            var old = s.Tun.Mtu;
            s.Tun.Mtu = TunSettings.DefaultMtu;
            logger?.Information(
                "[SettingsMigrator] v7->v8: moved TUN MTU {Old} -> {New} " +
                "(1420 fits observed VLESS/TUN path and avoids Steam SDR-class UDP regression; " +
                "explicit custom MTUs including 1280 are preserved)",
                old,
                s.Tun.Mtu);
        }

        return s;
    }
}
