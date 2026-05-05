using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VPNRouter.App.Services;

/// <summary>
/// Detects mixed-version DLL state caused by an interrupted / broken
/// auto-update. v2.31.8-r1.
///
/// <para><b>The migration trap (Bug 2 in v2.31.7 release notes)</b>: pre-r1
/// the in-app update helper (<c>vpnrouter-update-{pid}.cmd</c>) was NOT
/// Service-aware. When the Windows Service was installed and running, it
/// held kernel file-locks on <c>VPNRouter.Service.dll</c>,
/// <c>VPNRouter.Core.dll</c> and friends. <c>xcopy /R</c> silently skipped
/// locked files. After the broken update the on-disk state was a mix:
/// <c>VPNRouter.App.exe/dll</c> = NEW (replaced), <c>VPNRouter.Core.dll</c>
/// = OLD (locked), <c>VPNRouter.Service.dll</c> = OLD. Result: ABI mismatch
/// at next launch, AppVersion still showed old, or runtime crashed.</para>
///
/// <para>v2.31.7-r1 fixed the .cmd helper to stop the Service before
/// xcopy. But the fix only takes effect for users <b>already on
/// v2.31.7+</b>. Users still on v2.31.5 / v2.31.6 / earlier with Service
/// installed run their OLD broken helper on first upgrade attempt → land
/// in mixed-version state and can't escape without manual install.ps1.</para>
///
/// <para><b>What this check does</b>: every build's
/// <c>InformationalVersion</c> attribute carries the source commit hash
/// (e.g. <c>1.0.0+f3da9a3</c>). All <c>VPNRouter.*.dll</c> shipped from
/// the same release share the same commit hash. If we read the hashes
/// off disk and any differ, the install is mixed → triggered the broken
/// auto-update path → auto-repair via <see cref="SelfRepair"/>.</para>
///
/// <para>Loop prevention: <see cref="SelfRepair"/> writes a marker file;
/// if a repair attempt is recent (≤ 10 min ago) we don't retry —
/// surfacing the failure to the user instead so they can act.</para>
/// </summary>
public static class InstallHealthCheck
{
    /// <summary>DLL files we expect to be commit-hash-consistent.</summary>
    private static readonly string[] TrackedDlls =
    {
        "VPNRouter.App.dll",
        "VPNRouter.Core.dll",
        "VPNRouter.Service.dll",
    };

    public sealed record Report(bool IsHealthy, string Diagnostic, IReadOnlyDictionary<string, string> Hashes);

    /// <summary>
    /// Inspect installed DLLs in <paramref name="appDir"/> and return a
    /// health report. Pure read — no side effects, no exceptions thrown.
    /// </summary>
    public static Report Check(string? appDir = null)
    {
        appDir ??= AppContext.BaseDirectory;
        var hashes = new Dictionary<string, string>();

        // v2.31.8-r6 PRIMARY check: compare App.exe's compile-time
        // AppVersion.Version literal against Core.dll's runtime
        // AppVersion.Version reflection read.
        //
        // Rationale: when App.exe is BUILT, references to
        // VPNRouter.Core.AppVersion.Version (a `const string`) get
        // INLINED into App.exe's IL — so App.exe's view is forever the
        // value Core.dll had at App.exe's build time. At RUNTIME,
        // however, GetField+GetRawConstantValue on the loaded Core.dll
        // type reads the value from Core.dll's metadata as it currently
        // exists on disk. If they differ, the on-disk Core.dll was NOT
        // rebuilt at the same time as App.exe → mixed-version state.
        //
        // This check works EVEN when ProductVersion is empty / unreadable
        // (we observed this in user's environment — Service-locked DLLs
        // returned empty ProductVersion via FileVersionInfo, leading our
        // ProductVersion-based check to false-negative as "all DLLs
        // empty == healthy"). The compile-time-vs-runtime check uses
        // the always-present const value which doesn't depend on
        // version-info resource being intact.
        try
        {
            var compiled = VPNRouter.Core.AppVersion.Version;
            var runtimeField = typeof(VPNRouter.Core.AppVersion)
                .GetField(nameof(VPNRouter.Core.AppVersion.Version),
                          BindingFlags.Public | BindingFlags.Static);
            var runtime = runtimeField?.GetRawConstantValue() as string ?? string.Empty;
            hashes["compiled-AppVersion"] = compiled;
            hashes["runtime-AppVersion"]  = runtime;

            if (!string.IsNullOrEmpty(runtime) &&
                !string.Equals(compiled, runtime, StringComparison.Ordinal))
            {
                return new Report(
                    IsHealthy: false,
                    Diagnostic: $"AppVersion mismatch: App.exe compiled with '{compiled}', Core.dll on disk reports '{runtime}'",
                    Hashes: hashes);
            }
        }
        catch (Exception ex)
        {
            hashes["AppVersion-check-error"] = ex.Message;
            // Don't return — fall through to ProductVersion-based check.
        }

        // Secondary check: per-DLL ProductVersion (commit-hash) consistency.
        // Catches mismatches when the AppVersion string happens to match
        // (e.g. two builds with the same -rN tag but different commits)
        // and surfaces a more detailed diagnostic.
        foreach (var dll in TrackedDlls)
        {
            var path = Path.Combine(appDir, dll);
            if (!File.Exists(path))
            {
                hashes[dll] = "<missing>";
                continue;
            }

            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var pv = info.ProductVersion ?? string.Empty;
                var plusIdx = pv.IndexOf('+');
                hashes[dll] = plusIdx >= 0 ? pv[(plusIdx + 1)..] : pv;
            }
            catch
            {
                hashes[dll] = "<read-error>";
            }
        }

        var present = hashes.Where(kv => TrackedDlls.Contains(kv.Key)
                                          && !string.IsNullOrEmpty(kv.Value)
                                          && !kv.Value.StartsWith("<"))
                            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (present.Count < 2)
            return new Report(true,
                $"AppVersion match (compile==runtime); only {present.Count} DLL ProductVersion(s) populated — skipping commit-hash cross-check",
                hashes);

        var distinct = present.Values.Distinct().ToList();
        if (distinct.Count == 1)
            return new Report(true,
                $"AppVersion match + all {present.Count} DLL ProductVersions @ {Trim(distinct[0])}",
                hashes);

        var summary = string.Join(", ",
            present.Select(kv => $"{kv.Key.Replace("VPNRouter.", "").Replace(".dll", "")}={Trim(kv.Value)}"));
        return new Report(false, $"mixed-version DLLs (commit hashes): {summary}", hashes);
    }

    private static string Trim(string h) => h.Length > 7 ? h[..7] : h;
}
