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
///
/// <para>Bug #2 (2026-05-11) — augment <see cref="PackageManager.GetInstalledApplications"/>
/// with <see cref="PackageManager.QueryIntentActivities(Intent, PackageInfoFlags)"/>
/// for <c>Intent.ACTION_MAIN</c> + <c>CATEGORY_LAUNCHER</c>. Some OEM ROMs
/// (Xiaomi MIUI, Huawei EMUI, certain Samsung One UI builds) silently drop
/// apps from <c>MatchAll</c> when work-profile / private-space / multi-user
/// containers are involved, but still expose them via launcher queries. The
/// merged set is the union of both paths, deduped by package name.</para>
/// </summary>
internal static class AppListLoader
{
    private const string LogTag = "VPNRouter.AppList";

    public sealed class AppEntry
    {
        public string PackageName { get; set; } = string.Empty;
        public string Label       { get; set; } = string.Empty;
        // v3.0 v2.32.0 (2026-05-07) — converted via AppIconCache so the
        // per-app picker can render real app icons (handbook §5.5
        // follow-up). Null when the package returned no icon or the
        // Drawable→Bitmap conversion threw (rare; some short-lived
        // stub packages misbehave).
        public Bitmap? IconBitmap { get; set; }
        public bool IsSystem      { get; set; }
    }

    public static List<AppEntry> ListUserApps()
    {
        // Bug-AND-007 (2026-05-16) — surface system apps that match a
        // curated category hint even when the "Show system apps" toggle is
        // off. Many OEM-bundled apps (Chrome on KYOCERA, Edge on certain
        // Samsung builds, Yandex Browser on Russian ROMs, Telegram on some
        // budget OEMs) ship with the SYSTEM flag set. Without this
        // override, the Browsers / Messengers / Streaming categories
        // appear empty on stock devices even though the user clearly
        // recognises those apps. The full "Show system apps" toggle
        // still adds the remaining OEM bloat (Google Play Services,
        // carrier helpers, etc.) on top.
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

        // Path 1 — GetInstalledApplications (the canonical AOSP API).
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

        // Path 2 — QueryIntentActivities(ACTION_MAIN + CATEGORY_LAUNCHER).
        // Catches apps that some OEM ROMs hide from MatchAll. We ignore
        // failures here entirely — the canonical path is path 1, this is
        // strictly additive.
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
            // Hide our own package — VpnRouterService.openTun already
            // self-disallows it so the VPN doesn't loop on itself; no
            // reason to expose it in the filter UI.
            if (info.PackageName == ownPackage) continue;

            var isSystem = (info.Flags & ApplicationInfoFlags.System) != 0;
            // Bug-AND-007 (2026-05-16) — keep system apps that are
            // explicitly listed in a curated category hint set, even
            // when includeSystem == false. This is what surfaces
            // Chrome (system app on KYOCERA + most stock ROMs) inside
            // the Browsers category without forcing the user to flip
            // the "Show system apps" toggle.
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
                // Some packages (especially short-lived stubs) throw on
                // icon load. Fall back to no icon — UI will show empty
                // icon slot.
            }

            // v3.0 v2.32.0 — eager Drawable→Bitmap conversion inside the
            // existing background Task.Run that wraps Load(). Cache hit
            // is O(1); cold conversion is the bulk of the load latency
            // (~10 ms × 100 apps ≈ 1 s on KYOCERA A101BM). Doing it
            // here keeps the row builder synchronous, avoiding the
            // need to dispatch back to the UI thread per-row to update
            // an Image.Source after the fact.
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
