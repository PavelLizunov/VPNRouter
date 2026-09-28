using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using VPNRouter.Core;
using VPNRouter.Core.Services.Diagnostics;

namespace VPNRouter.Android;

internal static class AndroidDiagnosticsExporter
{
    private const int LogTailLines = 800;

    private const int MaxCrashFiles = 3;

    private const long MaxTailReadBytes = 2L * 1024 * 1024;

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
            var ext = ctx.GetExternalFilesDir(null);
            if (ext is not null)
            {
                var legacy = Path.Combine(ext.AbsolutePath, "singbox.log");
                if (File.Exists(legacy)) return legacy;
            }
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

            var singboxLog = ResolveSingboxLogPath();
            AddLogTail(staging, singboxLog, "singbox-tail.log", entries, warnings);

            AddLogTail(staging, Path.Combine(AppPaths.DataDir, "singbox.stderr.log"),
                "singbox-stderr-tail.log", entries, warnings);

            AddRecentCrashes(staging, entries, warnings);

            var zipPath = BuildZip(staging, stamp, warnings);
            return new Result(zipPath, entries, warnings);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

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

    private static void AddText(string staging, string name, string content, List<string> entries)
    {
        try
        {
            File.WriteAllText(Path.Combine(staging, name), content);
            entries.Add(name);
        }
        catch { }
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
        if (seeked && all.Length > 1) all = all.Skip(1).ToArray();
        if (all.Length <= maxLines) return string.Join("\n", all);
        return string.Join("\n", all.Skip(all.Length - maxLines));
    }
}
