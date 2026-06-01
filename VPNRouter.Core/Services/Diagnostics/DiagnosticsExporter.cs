using System.IO.Compression;
using System.Text;

namespace VPNRouter.Core.Services.Diagnostics;

/// <summary>
/// Collects a redacted diagnostics bundle (config + sing-box config + bounded
/// log tails + env/health summary + geo file manifest) into a single ZIP on
/// the user's Desktop, so a support request becomes a one-click attachment
/// instead of a hand-collected pile of files.
///
/// Variant 0 (settled 2026-05-30): we host NOTHING. Collect → redact → ZIP →
/// the user attaches it wherever they already get support. Everything is
/// redacted by <see cref="DiagnosticsRedactor"/> before it lands in the ZIP;
/// see that class for the fail-safe policy.
///
/// All collection is best-effort: a missing or locked file is noted as a
/// warning and skipped, never fatal — a partial bundle still helps.
/// </summary>
public static class DiagnosticsExporter
{
    /// <summary>Max number of log lines kept per log file (bounded bundle size).</summary>
    public const int LogTailLines = 800;

    public sealed record Result(
        string ZipPath,
        IReadOnlyList<string> Entries,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Build the bundle. <paramref name="timestamp"/> stamps the filename
    /// (pass DateTime.Now from the UI; injected so tests are deterministic).
    /// <paramref name="connected"/> is the current VPN connected-state.
    /// <paramref name="destinationDir"/> defaults to the Desktop.
    /// </summary>
    public static Result Export(DateTime timestamp, bool connected, string? destinationDir = null)
    {
        var warnings = new List<string>();
        var entries = new List<string>();

        var stamp = timestamp.ToString("yyyyMMdd-HHmmss");
        var staging = Path.Combine(Path.GetTempPath(), $"vpnrouter-diag-{stamp}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            AddText(staging, "README.txt", BuildReadme(), entries);
            AddText(staging, "summary.txt", BuildSummary(timestamp, connected, warnings), entries);

            // config.yaml (redacted)
            AddRedactedFile(staging, AppPaths.ConfigYamlPath, "config.redacted.yaml",
                DiagnosticsRedactor.RedactConfigYaml, entries, warnings);

            // current.json — what sing-box actually loaded (redacted)
            AddRedactedFile(staging, AppPaths.CurrentConfigPath, "current.redacted.json",
                DiagnosticsRedactor.RedactSingboxJson, entries, warnings);

            // state.json (PID/paths — redact as JSON, fail-safe)
            AddRedactedFile(staging, AppPaths.StatePath, "state.redacted.json",
                DiagnosticsRedactor.RedactSingboxJson, entries, warnings);

            // app log tail (latest vpnrouter*.log), scrubbed
            AddLogTail(staging, FindLatestAppLog(), "vpnrouter-tail.log", entries, warnings);

            // sing-box log tail, scrubbed
            AddLogTail(staging, AppPaths.SingBoxLogPath, "singbox-tail.log", entries, warnings);

            // emergency channel log, if present
            AddLogTail(staging, AppPaths.WgturnCliLogPath, "wgturn-cli-tail.log", entries, warnings);

            // geo file manifest (sizes + dates, NOT the files)
            AddText(staging, "geo-manifest.txt", BuildGeoManifest(), entries);

            // ── zip it ──
            var destDir = ResolveDestination(destinationDir);
            Directory.CreateDirectory(destDir);
            var zipPath = Path.Combine(destDir, $"VPNRouter-diagnostics-{stamp}.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

            return new Result(zipPath, entries, warnings);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // ── section builders ────────────────────────────────────────────────

    private static string BuildReadme() => string.Join(Environment.NewLine, new[]
    {
        "VPNRouter diagnostics bundle",
        "============================",
        "",
        "This archive was generated locally on your machine. Nothing was uploaded.",
        "Credentials have been removed: VLESS UUIDs, passwords, Reality short IDs,",
        "subscription tokens and unknown fields are replaced with \"***\". Only",
        "non-secret values (server host, ports, routing rules, log lines) are kept.",
        "",
        "PLEASE OPEN AND REVIEW THIS ARCHIVE before attaching it to a support",
        "message, so you are comfortable with what it contains. Then attach it",
        "wherever you already get support (Discord / Telegram / GitHub issue).",
        "",
        "Contents:",
        "  summary.txt            - version, OS, channel, connected state, health check",
        "  config.redacted.yaml   - your settings (secrets removed)",
        "  current.redacted.json  - the config sing-box actually loaded (secrets removed)",
        "  state.redacted.json    - runtime state (PID etc.)",
        "  vpnrouter-tail.log      - last app log lines (scrubbed)",
        "  singbox-tail.log        - last sing-box log lines (scrubbed)",
        "  geo-manifest.txt        - geo rule file sizes & dates (not the files)",
    });

    private static string BuildSummary(DateTime timestamp, bool connected, List<string> warnings)
    {
        var sb = new StringBuilder();
        var isPrerelease = AppVersion.Version.Contains("-r", StringComparison.OrdinalIgnoreCase);
        sb.AppendLine("VPNRouter diagnostics summary");
        sb.AppendLine("=============================");
        sb.AppendLine($"Version:    {AppVersion.Version}");
        sb.AppendLine($"Channel:    {(isPrerelease ? "experimental (prerelease)" : "stable")}");
        sb.AppendLine($"OS:         {Environment.OSVersion}");
        sb.AppendLine($"Platform:   {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "other")}");
        sb.AppendLine($"64-bit:     {Environment.Is64BitProcess}");
        sb.AppendLine($"CLR:        {Environment.Version}");
        sb.AppendLine($"Connected:  {connected}");
        sb.AppendLine($"Generated:  {timestamp:o} (local) / {timestamp.ToUniversalTime():o} (UTC)");
        sb.AppendLine();
        sb.AppendLine("──── Health check ────");
        try
        {
            sb.AppendLine(HealthCheck.FormatReport(HealthCheck.RunAll()));
        }
        catch (Exception ex)
        {
            warnings.Add($"health check failed: {ex.GetType().Name}");
            sb.AppendLine("(health check could not run)");
        }
        return sb.ToString();
    }

    private static string BuildGeoManifest()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Geo rule files (sizes + dates only — files themselves not included)");
        sb.AppendLine("===================================================================");
        try
        {
            if (Directory.Exists(AppPaths.GeoDir))
            {
                var files = Directory.GetFiles(AppPaths.GeoDir, "*.srs")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (files.Count == 0)
                    sb.AppendLine("(no .srs files present)");
                foreach (var f in files)
                {
                    var fi = new FileInfo(f);
                    sb.AppendLine($"{fi.Name,-24} {fi.Length,10} bytes   {fi.LastWriteTimeUtc:o}");
                }
            }
            else
            {
                sb.AppendLine("(geo directory does not exist)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(could not enumerate geo dir: {ex.GetType().Name})");
        }
        return sb.ToString();
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static void AddText(string staging, string name, string content, List<string> entries)
    {
        File.WriteAllText(Path.Combine(staging, name), content);
        entries.Add(name);
    }

    private static void AddRedactedFile(string staging, string sourcePath, string outName,
        Func<string, string> redact, List<string> entries, List<string> warnings)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                warnings.Add($"{Path.GetFileName(sourcePath)} not found — skipped");
                return;
            }
            var raw = ReadAllTextShared(sourcePath);
            File.WriteAllText(Path.Combine(staging, outName), redact(raw));
            entries.Add(outName);
        }
        catch (Exception ex)
        {
            warnings.Add($"{Path.GetFileName(sourcePath)} could not be read ({ex.GetType().Name}) — skipped");
        }
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

    private static string? FindLatestAppLog()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogsDir)) return null;
            return Directory.GetFiles(AppPaths.LogsDir, "vpnrouter*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Read a file even if another process holds it open for writing.</summary>
    private static string ReadAllTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>Return the last <paramref name="maxLines"/> lines of a file (share-read).</summary>
    private static string TailLines(string path, int maxLines)
    {
        var all = ReadAllTextShared(path).Replace("\r\n", "\n").Split('\n');
        if (all.Length <= maxLines) return string.Join(Environment.NewLine, all);
        return string.Join(Environment.NewLine, all.Skip(all.Length - maxLines));
    }

    private static string ResolveDestination(string? destinationDir)
    {
        if (!string.IsNullOrWhiteSpace(destinationDir)) return destinationDir!;
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop)) return desktop;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) ? home : Path.GetTempPath();
    }
}
