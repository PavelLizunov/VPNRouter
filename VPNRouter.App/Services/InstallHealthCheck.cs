using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VPNRouter.App.Services;

public static class InstallHealthCheck
{
    private static readonly string[] TrackedDlls =
    {
        "VPNRouter.App.dll",
        "VPNRouter.Core.dll",
        "VPNRouter.Service.dll",
    };

    public sealed record Report(bool IsHealthy, string Diagnostic, IReadOnlyDictionary<string, string> Hashes);

    public static Report Check(string? appDir = null)
    {
        appDir ??= AppContext.BaseDirectory;
        var hashes = new Dictionary<string, string>();

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
        }

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
