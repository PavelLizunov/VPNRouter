using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Util;
using Avalonia.Media.Imaging;

namespace VPNRouter.Android;

internal static class AppListLoader
{
    private const string LogTag = "VPNRouter.AppList";

    public sealed class AppEntry
    {
        public string PackageName { get; set; } = string.Empty;
        public string Label       { get; set; } = string.Empty;
        public Bitmap? IconBitmap { get; set; }
        public bool IsSystem      { get; set; }
    }

    public static List<AppEntry> ListUserApps()
    {
        return Load(includeSystem: false, curatedHintAllowlist: AndroidCategoryDefaults.AllBuiltInPackages());
    }

    public static List<AppEntry> ListAllApps()
    {
        return Load(includeSystem: true, curatedHintAllowlist: null);
    }

    private static List<AppEntry> Load(bool includeSystem, HashSet<string>? curatedHintAllowlist)
    {
        var ctx = Application.Context;
        if (ctx is null) return new List<AppEntry>();
        var pm = ctx.PackageManager;
        if (pm is null) return new List<AppEntry>();

        var merged = new Dictionary<string, ApplicationInfo>(StringComparer.OrdinalIgnoreCase);
        int matchAllCount = 0;
        int launcherCount = 0;
        int launcherUnique = 0;

        try
        {
            var apps = pm.GetInstalledApplications(PackageInfoFlags.MatchAll);
            if (apps is not null)
            {
                matchAllCount = apps.Count;
                foreach (var info in apps)
                {
                    var pkg = info.PackageName;
                    if (string.IsNullOrEmpty(pkg)) continue;
                    merged[pkg] = info;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"GetInstalledApplications failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            using var launcherIntent = new Intent(Intent.ActionMain);
            launcherIntent.AddCategory(Intent.CategoryLauncher);
            var resolved = pm.QueryIntentActivities(launcherIntent, PackageInfoFlags.MatchAll);
            if (resolved is not null)
            {
                launcherCount = resolved.Count;
                foreach (var ri in resolved)
                {
                    var pkg = ri?.ActivityInfo?.PackageName;
                    if (string.IsNullOrEmpty(pkg)) continue;
                    if (merged.ContainsKey(pkg)) continue;
                    var info = ri!.ActivityInfo!.ApplicationInfo;
                    if (info is null) continue;
                    merged[pkg] = info;
                    launcherUnique++;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"QueryIntentActivities failed: {ex.GetType().Name}: {ex.Message}");
        }

        Log.Info(LogTag,
            $"AppListLoader.Load(includeSystem={includeSystem}): " +
            $"MatchAll={matchAllCount}, Launcher={launcherCount} " +
            $"(+{launcherUnique} unique), merged={merged.Count}");

        var ownPackage = ctx.PackageName ?? string.Empty;
        var result = new List<AppEntry>(merged.Count);
        foreach (var info in merged.Values)
        {
            if (info.PackageName == ownPackage) continue;

            var isSystem = (info.Flags & ApplicationInfoFlags.System) != 0;
            if (isSystem && !includeSystem)
            {
                if (curatedHintAllowlist is null
                    || string.IsNullOrEmpty(info.PackageName)
                    || !curatedHintAllowlist.Contains(info.PackageName))
                    continue;
            }

            string label;
            try
            {
                label = pm.GetApplicationLabel(info)?.ToString() ?? info.PackageName;
            }
            catch
            {
                label = info.PackageName ?? "(unknown)";
            }

            Drawable? icon = null;
            try
            {
                icon = pm.GetApplicationIcon(info);
            }
            catch
            {
            }

            var pkgName = info.PackageName ?? string.Empty;
            Bitmap? iconBitmap = null;
            if (!string.IsNullOrEmpty(pkgName))
            {
                try
                {
                    iconBitmap = AppIconCache.GetOrConvert(pkgName, icon);
                }
                catch
                {
                    iconBitmap = null;
                }
            }

            result.Add(new AppEntry
            {
                PackageName = pkgName,
                Label = label,
                IconBitmap = iconBitmap,
                IsSystem = isSystem,
            });
        }

        return result.OrderBy(a => a.Label, System.StringComparer.OrdinalIgnoreCase).ToList();
    }
}
