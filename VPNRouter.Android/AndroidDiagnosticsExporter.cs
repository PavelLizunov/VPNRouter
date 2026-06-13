using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using VPNRouter.Core;
using VPNRouter.Core.Services.Diagnostics;

namespace VPNRouter.Android;

/// <summary>
/// v2.40.0 night-shift (2026-06-02) — Android parity for the desktop
/// "Export diagnostics" feature (Variant 0: collect → redact → ZIP locally,
/// nothing uploaded).
///
/// <para>The desktop <see cref="DiagnosticsExporter"/> is Windows-path centric
/// (config.yaml, %ProgramData%, geo .srs) and mostly no-ops on Android, which
/// keeps settings in SharedPreferences (NOT a YAML file) and writes its
/// artifacts to the app sandbox. This Android exporter gathers the artifacts
/// that actually exist on the device:</para>
/// <list type="bullet">
///   <item><c>summary.txt</c> — version / Android SDK / device / connected
///   state / config mode / server count. ALL non-secret — no URLs, UUIDs,
///   tokens or the subscription itself.</item>
///   <item><c>singbox-tail.log</c> — last lines of <c>singbox.log</c>
///   (GetExternalFilesDir), scrubbed via <see cref="DiagnosticsRedactor.RedactLogText"/>.</item>
///   <item><c>singbox-stderr-tail.log</c> — last lines of the Go-runtime
///   stderr (<c>filesDir/singbox.stderr.log</c>), scrubbed.</item>
///   <item><c>crash-*.txt</c> — recent crash reports from
///   <c>DataDir/crashes/</c>. These are already self-scrubbed by the C#
///   CrashReporter + Java <c>scrubSecrets</c>; we re-run the redactor anyway
///   (belt-and-suspenders).</item>
/// </list>
///
/// <para><strong>Secret hygiene</strong>: we deliberately do NOT dump the
/// SharedPreferences / subscription URL / server list contents — only a count
/// + the config mode. Everything textual goes through the allowlist redactor.
/// The bundle lands in <c>GetExternalFilesDir</c> so the user can retrieve +
/// review it (and a share intent can target it) before sending it anywhere.</para>
///
/// <para>All collection is best-effort: a missing / unreadable artifact is
/// noted as a warning and skipped, never fatal — a partial bundle still helps.
/// This whole type is additive: it reads existing files + writes a new ZIP, and
/// never touches the tunnel / config / routing paths.</para>
/// </summary>
internal static class AndroidDiagnosticsExporter
{
    /// <summary>Max log lines kept per log file (bounded bundle size).</summary>
    private const int LogTailLines = 800;

    /// <summary>Max recent crash files included.</summary>
    private const int MaxCrashFiles = 3;

    /// <summary>
    /// Hard cap on bytes read when tailing a log (audit MEDIUM, 2026-06-02):
    /// only the END of the file is needed, so seek to the last 2 MB instead of
    /// reading the whole thing — a corrupt/runaway multi-GB log can't OOM.
    /// </summary>
    private const long MaxTailReadBytes = 2L * 1024 * 1024;

    /// <summary>
    /// A6 (2026-06-13) — single source of truth for the runtime sing-box log
    /// path on Android. The service + health probe write/read
    /// <c>FilesDir/singbox.log</c> (private sandbox, Bug-AND-011), so every
    /// reader (in-app log viewer, diagnostics exporter, "copy log path" kebab)
    /// must resolve the same location or a freeze report looks empty/stale.
    ///
    /// <para>Prefers <c>FilesDir/singbox.log</c>. Falls back to the legacy
    /// <c>GetExternalFilesDir(null)/singbox.log</c> only when the FilesDir copy
    /// is absent — migration safety for a report carried over from a
    /// pre-Bug-AND-011 build that wrote to external storage. Returns null when
    /// neither directory is available.</para>
    /// </summary>
    internal static string? ResolveSingboxLogPath()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var filesDir = ctx.FilesDir;
            if (filesDir is not null)
            {
                var primary = Path.Combine(filesDir.AbsolutePath, "singbox.log");
                if (File.Exists(primary)) return primary;
            }
            // Legacy fallback: only honour an external copy when the private
            // sandbox copy doesn't exist (a report from an older build).
            var ext = ctx.GetExternalFilesDir(null);
            if (ext is not null)
            {
                var legacy = Path.Combine(ext.AbsolutePath, "singbox.log");
                if (File.Exists(legacy)) return legacy;
            }
            // Neither exists yet — return the canonical primary path so callers
            // surface an honest "empty" state for the right location.
            return filesDir is not null
                ? Path.Combine(filesDir.AbsolutePath, "singbox.log")
                : null;
        }
        catch
        {
            return null;
        }
    }

    public sealed record Result(string? ZipPath, IReadOnlyList<string> Entries, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Build the bundle. <paramref name="timestamp"/> stamps the filename
    /// (caller passes DateTime.Now). <paramref name="connected"/> is the
    /// current tunnel state. <paramref name="configMode"/> /
    /// <paramref name="serverCount"/> are non-secret summary inputs the caller
    /// reads from AndroidStorage (kept here as plain params so this type has no
    /// AndroidStorage dependency + stays trivially reviewable).
    /// </summary>
    public static Result Export(DateTime timestamp, bool connected, string configMode, int serverCount)
    {
        var warnings = new List<string>();
        var entries = new List<string>();

        var stamp = timestamp.ToString("yyyyMMdd-HHmmss");
        string staging;
        try
        {
            staging = Path.Combine(Path.GetTempPath(), $"vpnrouter-diag-{stamp}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
        }
        catch (Exception ex)
        {
            return new Result(null, entries, new List<string> { $"could not create staging dir: {ex.GetType().Name}" });
        }

        try
        {
            AddText(staging, "README.txt", BuildReadme(), entries);
            AddText(staging, "summary.txt", BuildSummary(timestamp, connected, configMode, serverCount), entries);

            // sing-box log — FilesDir/singbox.log (private sandbox, Bug-AND-011),
            // matching the runtime writer + health probe + in-app log viewer.
            // A6 (2026-06-13): was GetExternalFilesDir, which never exists post-
            // Bug-AND-011 → bundle's singbox-tail.log was always empty.
            var singboxLog = ResolveSingboxLogPath();
            AddLogTail(staging, singboxLog, "singbox-tail.log", entries, warnings);

            // Go-runtime stderr (private sandbox — Bug-AND-011 path).
            AddLogTail(staging, Path.Combine(AppPaths.DataDir, "singbox.stderr.log"),
                "singbox-stderr-tail.log", entries, warnings);

            // recent crash reports (already self-scrubbed; re-redact anyway).
            AddRecentCrashes(staging, entries, warnings);

            var zipPath = BuildZip(staging, stamp, warnings);
            return new Result(zipPath, entries, warnings);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── section builders ────────────────────────────────────────────────

    private static string BuildReadme() => string.Join("\n", new[]
    {
        "VPNRouter (Android) diagnostics bundle",
        "======================================",
        "",
        "Generated locally on your device. Nothing was uploaded. Secrets are",
        "removed: VLESS UUIDs, passwords, Reality short IDs, subscription tokens",
        "and unknown fields are replaced with \"***\". The subscription URL and",
        "server list themselves are NOT included — only a count + the config mode.",
        "",
        "PLEASE REVIEW this archive before sharing it. Then attach it wherever you",
        "already get support.",
        "",
        "Contents:",
        "  summary.txt               - version, Android, device, connected, mode, server count",
        "  singbox-tail.log          - last sing-box log lines (scrubbed)",
        "  singbox-stderr-tail.log   - last Go-runtime stderr lines (scrubbed)",
        "  crash-*.txt               - recent crash reports (scrubbed)",
    });

    private static string BuildSummary(DateTime timestamp, bool connected, string configMode, int serverCount)
    {
        var sb = new StringBuilder();
        var isPrerelease = AppVersion.Version.Contains("-r", StringComparison.OrdinalIgnoreCase);
        sb.AppendLine("VPNRouter (Android) diagnostics summary");
        sb.AppendLine("=======================================");
        sb.AppendLine($"Version:    {AppVersion.Version}");
        sb.AppendLine($"Channel:    {(isPrerelease ? "experimental (prerelease)" : "stable")}");
        try
        {
            // NOTE: `global::` cannot appear at the top level of an interpolation
            // hole — the parser reads the first `:` as the format separator. Pull
            // the values into locals first.
            var osRel = global::Android.OS.Build.VERSION.Release;
            var sdkInt = (int)global::Android.OS.Build.VERSION.SdkInt;
            var mfg = global::Android.OS.Build.Manufacturer;
            var mdl = global::Android.OS.Build.Model;
            var abis = global::Android.OS.Build.SupportedAbis ?? Array.Empty<string>();
            sb.AppendLine($"Android:    {osRel} (SDK {sdkInt})");
            sb.AppendLine($"Device:     {mfg} {mdl}");
            sb.AppendLine($"ABIs:       {string.Join(", ", abis)}");
        }
        catch (Exception ex) { sb.AppendLine($"(device info unavailable: {ex.GetType().Name})"); }
        sb.AppendLine($"Connected:  {connected}");
        sb.AppendLine($"ConfigMode: {configMode}");
        sb.AppendLine($"Servers:    {serverCount}");
        sb.AppendLine($"Generated:  {timestamp:o} (local)");
        return sb.ToString();
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static void AddText(string staging, string name, string content, List<string> entries)
    {
        try
        {
            File.WriteAllText(Path.Combine(staging, name), content);
            entries.Add(name);
        }
        catch { /* best-effort */ }
    }

    private static void AddLogTail(string staging, string? sourcePath, string outName,
        List<string> entries, List<string> warnings)
    {
        try
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                warnings.Add($"{outName} source not found — skipped");
                return;
            }
            var tail = TailLines(sourcePath, LogTailLines);
            File.WriteAllText(Path.Combine(staging, outName), DiagnosticsRedactor.RedactLogText(tail));
            entries.Add(outName);
        }
        catch (Exception ex)
        {
            warnings.Add($"{outName} could not be read ({ex.GetType().Name}) — skipped");
        }
    }

    private static void AddRecentCrashes(string staging, List<string> entries, List<string> warnings)
    {
        try
        {
            var crashesDir = Path.Combine(AppPaths.DataDir, "crashes");
            if (!Directory.Exists(crashesDir))
            {
                warnings.Add("no crashes dir — skipped (good news)");
                return;
            }
            var files = Directory.GetFiles(crashesDir, "*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(MaxCrashFiles)
                .ToList();
            if (files.Count == 0)
            {
                warnings.Add("no crash reports — skipped (good news)");
                return;
            }
            foreach (var f in files)
            {
                try
                {
                    var raw = ReadAllTextShared(f);
                    var outName = "crash-" + Path.GetFileName(f);
                    File.WriteAllText(Path.Combine(staging, outName), DiagnosticsRedactor.RedactLogText(raw));
                    entries.Add(outName);
                }
                catch (Exception ex) { warnings.Add($"crash {Path.GetFileName(f)} unreadable ({ex.GetType().Name})"); }
            }
        }
        catch (Exception ex) { warnings.Add($"crash dir enumerate failed: {ex.GetType().Name}"); }
    }

    private static string? BuildZip(string staging, string stamp, List<string> warnings)
    {
        try
        {
            // Land in external files dir so the user can retrieve + review it
            // (and a share intent can target the path). Fall back to cache dir.
            string destDir;
            var ext = global::Android.App.Application.Context.GetExternalFilesDir(null);
            destDir = ext?.AbsolutePath ?? AppPaths.CacheDir;
            Directory.CreateDirectory(destDir);
            var zipPath = Path.Combine(destDir, $"VPNRouter-diagnostics-{stamp}.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return zipPath;
        }
        catch (Exception ex)
        {
            warnings.Add($"zip creation failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string ReadAllTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private static string TailLines(string path, int maxLines)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        bool seeked = fs.Length > MaxTailReadBytes;
        if (seeked) fs.Seek(-MaxTailReadBytes, SeekOrigin.End);
        using var sr = new StreamReader(fs);
        var all = sr.ReadToEnd().Replace("\r\n", "\n").Split('\n');
        // Seeked mid-file → drop the partial first line.
        if (seeked && all.Length > 1) all = all.Skip(1).ToArray();
        if (all.Length <= maxLines) return string.Join("\n", all);
        return string.Join("\n", all.Skip(all.Length - maxLines));
    }
}
