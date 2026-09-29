using System;
using System.Collections.Generic;
using System.IO;
using Android.App;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

public static class AndroidConfigShare
{
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

    public static ApplyResult ApplySnapshot(
        ConfigShareDocument doc,
        bool applySettings,
        bool applyPerApp)
    {
        if (doc is null)
            return ApplyResult.Failure("document is null");

        string? backupPath = null;
        try
        {
            backupPath = BackupCurrentState();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.ConfigShare",
                $"backup before import failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            AndroidStorage.SetConfigMode(doc.ConfigMode ?? "subscribe");

            AndroidStorage.SetSubscriptions(doc.Subscriptions ?? new List<SubscriptionEntry>());

            if (string.Equals(doc.ConfigMode, "manual", StringComparison.OrdinalIgnoreCase))
            {
                AndroidStorage.SetVlessUri(doc.ManualVlessUri);
            }
            else
            {
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

        if (applySettings && doc.Settings is not null)
        {
            try
            {
                var s = doc.Settings;
                if (!string.IsNullOrWhiteSpace(s.Theme)) AndroidStorage.SetTheme(s.Theme);
                if (!string.IsNullOrWhiteSpace(s.Language)) AndroidStorage.SetLanguage(s.Language);
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

    public static string BackupCurrentState()
    {
        var ctx = Application.Context
            ?? throw new InvalidOperationException("Application.Context is null (process not initialised)");

        var dir = Path.Combine(ctx.FilesDir!.AbsolutePath, "backup");
        Directory.CreateDirectory(dir);

        var doc = BuildSnapshot(includeSettings: true, includePerApp: true);
        var json = ConfigShareDocument.Serialize(doc);

        var ts = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(dir, $"before-import-{ts}.json");
        File.WriteAllText(path, json);

        try
        {
            var existing = new DirectoryInfo(dir).GetFiles("before-import-*.json");
            Array.Sort(existing, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = 5; i < existing.Length; i++)
            {
                try { existing[i].Delete(); }
                catch { }
            }
        }
        catch { }

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

    public sealed class ApplyResult
    {
        public bool Ok { get; }
        public string? Error { get; }
        public string? BackupPath { get; }

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
