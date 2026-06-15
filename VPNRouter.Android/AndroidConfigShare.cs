using System;
using System.Collections.Generic;
using System.IO;
using Android.App;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

/// <summary>
/// v2.32.0 (Android-led, 2026-05-07) — adapter between
/// <see cref="ConfigShareDocument"/> and Android's SharedPreferences-backed
/// <see cref="AndroidStorage"/>. Three responsibilities:
///
/// <list type="bullet">
///   <item><b>BuildSnapshot</b>: read current SharedPreferences state and
///   compose a Document. The opt-in flags decide whether settings + per-app
///   filter are populated; ConfigMode-specific fields (manual URI / custom
///   JSON) are filled when relevant.</item>
///   <item><b>ApplySnapshot</b>: take a parsed Document, snapshot the
///   current state to a backup file, then atomically replace the
///   SharedPreferences keys with values from the document. Backup path
///   returned to the caller for surfacing in the UI.</item>
///   <item><b>BackupCurrentState</b>: snapshot-only — used independently
///   when the user wants to capture before a risky action.</item>
/// </list>
///
/// <para>Mirror — once desktop adopts ConfigShareDocument, this class's
/// counterpart on desktop will write to <c>AppSettings</c> via
/// <see cref="VPNRouter.Core.Services.SettingsLoader"/>.</para>
/// </summary>
public static class AndroidConfigShare
{
    /// <summary>
    /// Read every persisted SharedPreferences key relevant to a config
    /// share and compose a Document.
    /// </summary>
    /// <param name="includeSettings">
    /// When true, populates <see cref="ConfigShareDocument.Settings"/>
    /// with theme/language/routing/dns/etc. Off by default because a
    /// shared file would otherwise carry the donor user's UI preferences.
    /// </param>
    /// <param name="includePerApp">
    /// When true, populates <see cref="ConfigShareDocument.PerAppFilter"/>
    /// with the user's package selection. Off by default because the
    /// recipient may not have the same apps installed.
    /// </param>
    public static ConfigShareDocument BuildSnapshot(
        bool includeSettings, bool includePerApp)
    {
        var doc = new ConfigShareDocument
        {
            ExportedAt = DateTimeOffset.UtcNow,
            ExportedFrom = new ExportedFromInfo
            {
                Platform = "android",
                AppVersion = VPNRouter.Core.AppVersion.Version,
                DeviceLabel = SafeBuildModel(),
            },
            ConfigMode = AndroidStorage.GetConfigMode(),
            Subscriptions = AndroidStorage.GetSubscriptions(),
        };

        // Mode-specific payload: only carry the field that's actually in
        // use. A subscriber who flipped to manual mode briefly and then
        // back wouldn't want their stale URI tagging along.
        if (string.Equals(doc.ConfigMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            var uri = AndroidStorage.GetVlessUri();
            if (!string.IsNullOrWhiteSpace(uri)) doc.ManualVlessUri = uri;
        }
        else if (string.Equals(doc.ConfigMode, "custom", StringComparison.OrdinalIgnoreCase))
        {
            var rawJson = AndroidStorage.GetCustomConfigJson();
            if (!string.IsNullOrWhiteSpace(rawJson))
            {
                doc.CustomConfig = new CustomConfigPayload
                {
                    Name = AndroidStorage.GetCustomConfigName(),
                    SingBoxJson = rawJson,
                };
            }
        }

        if (includeSettings)
        {
            doc.Settings = new ExportedSettings
            {
                Theme = AndroidStorage.GetTheme(),
                Language = AndroidStorage.GetLanguage(),
                RoutingMode = AndroidStorage.GetRoutingMode(),
                BypassRussianTraffic = AndroidStorage.GetBypassRussianTraffic(),
                BlockOnVpnFail = AndroidStorage.GetBlockOnVpnFail(),
                DnsStrategy = AndroidStorage.GetDnsStrategy(),
                UpdateChannel = AndroidStorage.GetUpdateChannel(),
                AutostartVpn = AndroidStorage.GetAutostartVpn(),
                AutostartZapret = AndroidStorage.GetAutostartZapret(),
                AutostartTgProxy = AndroidStorage.GetAutostartTgProxy(),
            };
        }

        if (includePerApp)
        {
            doc.PerAppFilter = new PerAppFilterExport
            {
                Mode = AndroidStorage.GetPerAppMode(),
                Packages = AndroidStorage.GetPerAppPackages(),
            };
        }

        return doc;
    }

    /// <summary>
    /// Apply a parsed Document to SharedPreferences. Takes a backup of the
    /// current state first (returned in <see cref="ApplyResult.BackupPath"/>)
    /// so a regretful user has a recovery file. Settings + per-app filter
    /// blocks are applied only when the corresponding flag is true AND the
    /// document carries that block.
    /// </summary>
    public static ApplyResult ApplySnapshot(
        ConfigShareDocument doc,
        bool applySettings,
        bool applyPerApp)
    {
        if (doc is null)
            return ApplyResult.Failure("document is null");

        // 1. Snapshot the current state into a backup file before mutation.
        string? backupPath = null;
        try
        {
            backupPath = BackupCurrentState();
        }
        catch (Exception ex)
        {
            // Backup failed — be loud, but proceed. Better to apply the
            // imported settings than to refuse and leave the user without
            // recourse. The next StampRecoveryNotice will surface the warning.
            global::Android.Util.Log.Warn("VpnRouter.ConfigShare",
                $"backup before import failed: {ex.GetType().Name}: {ex.Message}");
        }

        // 2. Apply ConfigMode + mode-specific payload first so that on
        // partial-fail later, the routing-relevant data is at least
        // consistent.
        try
        {
            AndroidStorage.SetConfigMode(doc.ConfigMode ?? "subscribe");

            // Always-applied: subscriptions list.
            AndroidStorage.SetSubscriptions(doc.Subscriptions ?? new List<SubscriptionEntry>());

            // Mode-specific:
            if (string.Equals(doc.ConfigMode, "manual", StringComparison.OrdinalIgnoreCase))
            {
                AndroidStorage.SetVlessUri(doc.ManualVlessUri);
            }
            else
            {
                // Don't clobber an unrelated stored URI when the doc is in
                // a different mode — leave manual_uri as-is so a future
                // mode-flip restores it.
            }

            if (string.Equals(doc.ConfigMode, "custom", StringComparison.OrdinalIgnoreCase) &&
                doc.CustomConfig is not null)
            {
                AndroidStorage.SetCustomConfigJson(doc.CustomConfig.SingBoxJson);
                AndroidStorage.SetCustomConfigName(doc.CustomConfig.Name);
            }
        }
        catch (Exception ex)
        {
            return ApplyResult.Failure(
                $"applying config_mode/payload failed: {ex.GetType().Name}: {ex.Message}",
                backupPath);
        }

        // 3. Opt-in: settings.
        if (applySettings && doc.Settings is not null)
        {
            try
            {
                var s = doc.Settings;
                if (!string.IsNullOrWhiteSpace(s.Theme)) AndroidStorage.SetTheme(s.Theme);
                if (!string.IsNullOrWhiteSpace(s.Language)) AndroidStorage.SetLanguage(s.Language);
                // F6 follow-up (2026-06-16) — routing_mode is intentionally NOT
                // applied in the settings block. Post-F6 AndroidStorage.SetRoutingMode
                // is a pure projection that mutates PerAppMode (the real per-app
                // filter — the routing source of truth on Android). Applying it here
                // would silently flip the per-app filter the user opted NOT to import
                // on a SETTINGS-ONLY import (applySettings=true, applyPerApp=false).
                // The routing intent rightly travels only with the per-app block
                // below (SetPerAppMode); the exported RoutingMode stays in the JSON
                // for desktop/forward-compat but is a no-op on Android import.
                if (s.BypassRussianTraffic.HasValue) AndroidStorage.SetBypassRussianTraffic(s.BypassRussianTraffic.Value);
                if (s.BlockOnVpnFail.HasValue) AndroidStorage.SetBlockOnVpnFail(s.BlockOnVpnFail.Value);
                if (!string.IsNullOrWhiteSpace(s.DnsStrategy)) AndroidStorage.SetDnsStrategy(s.DnsStrategy!);
                if (!string.IsNullOrWhiteSpace(s.UpdateChannel)) AndroidStorage.SetUpdateChannel(s.UpdateChannel!);
                if (s.AutostartVpn.HasValue) AndroidStorage.SetAutostartVpn(s.AutostartVpn.Value);
                if (s.AutostartZapret.HasValue) AndroidStorage.SetAutostartZapret(s.AutostartZapret.Value);
                if (s.AutostartTgProxy.HasValue) AndroidStorage.SetAutostartTgProxy(s.AutostartTgProxy.Value);
            }
            catch (Exception ex)
            {
                return ApplyResult.PartialSuccess(
                    $"settings partially applied: {ex.GetType().Name}: {ex.Message}",
                    backupPath);
            }
        }

        // 4. Opt-in: per-app filter.
        if (applyPerApp && doc.PerAppFilter is not null)
        {
            try
            {
                AndroidStorage.SetPerAppMode(doc.PerAppFilter.Mode);
                AndroidStorage.SetPerAppPackages(doc.PerAppFilter.Packages ?? new List<string>());
            }
            catch (Exception ex)
            {
                return ApplyResult.PartialSuccess(
                    $"per-app filter partially applied: {ex.GetType().Name}: {ex.Message}",
                    backupPath);
            }
        }

        return ApplyResult.Success(backupPath);
    }

    /// <summary>
    /// Snapshot the current SharedPreferences state into a JSON file under
    /// <c>filesDir/backup/before-import-{ts}.json</c>. Returns the file path
    /// for surfacing in the UI ("Reverted? backup is at …"). Lightweight —
    /// re-uses BuildSnapshot with all opt-ins ON so the backup carries the
    /// full state regardless of which subset the user is about to import.
    /// </summary>
    public static string BackupCurrentState()
    {
        var ctx = Application.Context
            ?? throw new InvalidOperationException("Application.Context is null (process not initialised)");

        var dir = Path.Combine(ctx.FilesDir!.AbsolutePath, "backup");
        Directory.CreateDirectory(dir);

        // Carry everything in the backup so a restore is total.
        var doc = BuildSnapshot(includeSettings: true, includePerApp: true);
        var json = ConfigShareDocument.Serialize(doc);

        var ts = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(dir, $"before-import-{ts}.json");
        File.WriteAllText(path, json);

        // Best-effort retention: keep the most recent 5 backups so the
        // dir doesn't grow unbounded across many import attempts.
        try
        {
            var existing = new DirectoryInfo(dir).GetFiles("before-import-*.json");
            Array.Sort(existing, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = 5; i < existing.Length; i++)
            {
                try { existing[i].Delete(); }
                catch { /* per-file best-effort */ }
            }
        }
        catch { /* retention is advisory; ignore */ }

        return path;
    }

    private static string SafeBuildModel()
    {
        try
        {
            var manuf = global::Android.OS.Build.Manufacturer ?? "";
            var model = global::Android.OS.Build.Model ?? "";
            var label = $"{manuf} {model}".Trim();
            return label.Length == 0 ? "android" : label;
        }
        catch
        {
            return "android";
        }
    }

    /// <summary>Outcome of <see cref="ApplySnapshot"/>.</summary>
    public sealed class ApplyResult
    {
        public bool Ok { get; }
        public string? Error { get; }
        public string? BackupPath { get; }

        /// <summary>True when an error string is present BUT some fields applied.</summary>
        public bool IsPartial { get; }

        private ApplyResult(bool ok, string? err, string? backupPath, bool partial)
        {
            Ok = ok;
            Error = err;
            BackupPath = backupPath;
            IsPartial = partial;
        }

        public static ApplyResult Success(string? backupPath) =>
            new(true, null, backupPath, partial: false);
        public static ApplyResult Failure(string error, string? backupPath = null) =>
            new(false, error, backupPath, partial: false);
        public static ApplyResult PartialSuccess(string warning, string? backupPath) =>
            new(true, warning, backupPath, partial: true);
    }
}
