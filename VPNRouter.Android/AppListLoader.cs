using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content.PM;
using Android.Graphics.Drawables;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Phase 7.5 (2026-05-04) — wraps the Android PackageManager so the
/// per-app filter UI can paint a checkbox-list of installed apps.
///
/// <para>Two reads:
/// <list type="bullet">
///   <item><see cref="ListUserApps"/> returns user-installed apps only —
///   the common case (you're picking which of YOUR apps go through the
///   VPN).</item>
///   <item><see cref="ListAllApps"/> additionally surfaces system
///   packages — useful when you want to filter Google Play Services,
///   carrier apps, Samsung services etc. (they often dominate
///   background DNS traffic).</item>
/// </list></para>
///
/// <para>Sorted by user-visible label, case-insensitive. Apps without a
/// loadable label fall back to package name. We call
/// <see cref="PackageManager.GetApplicationLabel"/> via the
/// ApplicationInfo path because it tolerates missing labels (returns the
/// package name) better than reaching into Resources directly.</para>
/// </summary>
internal static class AppListLoader
{
    public sealed class AppEntry
    {
        public string PackageName { get; set; } = string.Empty;
        public string Label       { get; set; } = string.Empty;
        public Drawable? Icon     { get; set; }
        public bool IsSystem      { get; set; }
    }

    public static List<AppEntry> ListUserApps()
    {
        return Load(includeSystem: false);
    }

    public static List<AppEntry> ListAllApps()
    {
        return Load(includeSystem: true);
    }

    private static List<AppEntry> Load(bool includeSystem)
    {
        var ctx = Application.Context;
        if (ctx is null) return new List<AppEntry>();
        var pm = ctx.PackageManager;
        if (pm is null) return new List<AppEntry>();

        IList<ApplicationInfo>? apps = null;
        try
        {
            // GetInstalledApplications returns ALL installed apps regardless
            // of system/user; we filter via ApplicationInfoFlags.System.
            apps = pm.GetInstalledApplications(PackageInfoFlags.MatchAll);
        }
        catch
        {
            return new List<AppEntry>();
        }

        if (apps is null) return new List<AppEntry>();

        var ownPackage = ctx.PackageName ?? string.Empty;
        var result = new List<AppEntry>(apps.Count);
        foreach (var info in apps)
        {
            // Hide our own package — VpnRouterService.openTun already
            // self-disallows it so the VPN doesn't loop on itself; no
            // reason to expose it in the filter UI.
            if (info.PackageName == ownPackage) continue;

            var isSystem = (info.Flags & ApplicationInfoFlags.System) != 0;
            if (isSystem && !includeSystem) continue;

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
                // Some packages (especially short-lived stubs) throw on
                // icon load. Fall back to no icon — UI will show empty
                // icon slot.
            }

            result.Add(new AppEntry
            {
                PackageName = info.PackageName ?? string.Empty,
                Label = label,
                Icon = icon,
                IsSystem = isSystem,
            });
        }

        return result.OrderBy(a => a.Label, System.StringComparer.OrdinalIgnoreCase).ToList();
    }
}
