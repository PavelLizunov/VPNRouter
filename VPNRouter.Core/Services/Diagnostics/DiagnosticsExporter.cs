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
    /// <summary>
    /// Max log lines kept per log file. v2.41.0 (user ask 2026-06-04): bumped
    /// 800 → 40000 so a bundle holds days of history, not a couple of busy hours.
    /// sing-box is verbose ("found process path" spam), so 800 lines was minutes
    /// on an active session; the byte cap below is the real bound now.
    /// </summary>
    public const int LogTailLines = 40_000;

    /// <summary>
    /// Hard cap on bytes read when tailing a log (audit MEDIUM, 2026-06-02).
    /// `TailLines` only ever needs the END of the file, so we seek to the last
    /// <c>MaxTailReadBytes</c> instead of reading the whole thing — a corrupt or
    /// runaway multi-GB log can no longer OOM the bundle. v2.41.0: 2 MB → 12 MB
    /// so a full sing-box rotation (rotates at 10 MB → singbox.old.log) is
    /// captured intact, and a daily app log is included whole.
    /// </summary>
    public const long MaxTailReadBytes = 12L * 1024 * 1024;

    /// <summary>
    /// How many days of daily-rolled app logs (<c>vpnrouter{date}.log</c>) to
    /// include. v2.41.0: was implicitly 1 (latest file only) → 3 days, so a
    /// "couple of days" of context lands in the bundle.
    /// </summary>
    public const int LogWindowDays = 3;

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
            AddText(staging, "windows-services.txt", BuildWindowsServicesSnapshot(warnings), entries);

            // Antivirus / install-integrity snapshot (Windows). The load-bearing
            // reason: multiple users report "VPNRouter disappears after a reboot"
            // and the app's own logs can never show its OWN external deletion. This
            // captures the AV state that CAN — Defender status + Tamper Protection
            // (which silently no-ops install.ps1's exclusion), the registered AV
            // product (catches 3rd-party Kaspersky/etc), our exclusion presence,
            // past threat detections, Defender quarantine/remove events filtered to
            // our binaries, whether our EXEs still exist, and their (un)signed state.
            AddText(staging, "antivirus-integrity.txt", BuildAntivirusSnapshot(warnings), entries);

            // config.yaml (redacted)
            AddRedactedFile(staging, AppPaths.ConfigYamlPath, "config.redacted.yaml",
                DiagnosticsRedactor.RedactConfigYaml, entries, warnings);

            // Backup config files (unloadable or invalid)
            AddConfigBackups(staging, entries, warnings);

            // current.json — what sing-box actually loaded (redacted)
            AddRedactedFile(staging, AppPaths.CurrentConfigPath, "current.redacted.json",
                DiagnosticsRedactor.RedactSingboxJson, entries, warnings);

            // state.json (PID/paths — redact as JSON, fail-safe)
            AddRedactedFile(staging, AppPaths.StatePath, "state.redacted.json",
                DiagnosticsRedactor.RedactSingboxJson, entries, warnings);

            // app log tails — last LogWindowDays of daily-rolled vpnrouter*.log,
            // each kept under its own name so a few days of context is visible
            // (v2.41.0: was the single latest file only).
            var appLogs = FindRecentAppLogs();
            if (appLogs.Count == 0)
                warnings.Add("no vpnrouter*.log found — skipped");
            foreach (var log in appLogs)
                AddLogTail(staging, log, Path.GetFileName(log), entries, warnings);

            // update.log — the auto-update helper's per-run trace (wait-for-parent /
            // stop-service / xcopy / relaunch, with timestamps + XCOPY_EXIT). The
            // load-bearing reason: "VPNRouter disappears after a reboot" on the
            // experimental channel is almost always the update APPLY window — the app
            // stops during the xcopy and briefly vanishes from the tray, then
            // relaunches as the new version. This log is the timeline that proves it
            // (or shows a failed helper if it stayed gone). Absent from bundles before.
            AddLogTail(staging, Path.Combine(AppPaths.LogsDir, "update.log"), "update.log", entries, warnings);

            // sing-box log tail (current + rotated .old), scrubbed. sing-box
            // rotates at 10 MB → singbox.old.log; include both so the bundle
            // spans more than the current session (v2.41.0).
            AddLogTail(staging, AppPaths.SingBoxLogPath, "singbox-tail.log", entries, warnings);
            var singBoxOld = Path.Combine(AppPaths.LogsDir, "singbox.old.log");
            if (File.Exists(singBoxOld))
                AddLogTail(staging, singBoxOld, "singbox-old-tail.log", entries, warnings);

            // emergency channel log, if present
            AddLogTail(staging, AppPaths.WgturnCliLogPath, "wgturn-cli-tail.log", entries, warnings);

            // DNS-tunnel (slipstream) transport log, if present — carries the
            // QUIC-over-DNS connection lifecycle (idle-timeout 0x433, resolver-
            // unavailable, reconnect backoff) needed to root-cause a dropped
            // dns-tunnel. No-op for every non-dns-tunnel user (file absent).
            AddLogTail(staging, AppPaths.SlipstreamLogPath, "slipstream-tail.log", entries, warnings);
            // r9 (DIAGNOSTIC): SlipstreamManager rotates the transport log to .prev at
            // the start of each session. Capture it too so a reconnect after the key
            // (degraded) session doesn't lose that session's per-path debug output.
            AddLogTail(staging, AppPaths.SlipstreamLogPath + ".prev", "slipstream-prev-tail.log", entries, warnings);

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
        "  windows-services.txt   - Windows service/driver state for VPNRouter + True Split",
        "  config.redacted.yaml   - your settings (secrets removed)",
        "  current.redacted.json  - the config sing-box actually loaded (secrets removed)",
        "  state.redacted.json    - runtime state (PID etc.)",
        "  vpnrouter*.log          - app logs, last few days (scrubbed)",
        "  singbox-tail.log        - sing-box log, current (scrubbed)",
        "  singbox-old-tail.log    - sing-box log, previous rotation if present (scrubbed)",
        "  slipstream-tail.log     - DNS-tunnel transport log, if dns-tunnel was used (scrubbed)",
        "  slipstream-prev-tail.log- DNS-tunnel transport log, previous session if present (scrubbed)",
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

    private static string BuildWindowsServicesSnapshot(List<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Windows services and drivers");
        sb.AppendLine("============================");
        sb.AppendLine("True Split notes:");
        sb.AppendLine("- err=5 on \\\\.\\MULLVADSPLITTUNNEL means another agent holds the exclusive split-driver device.");
        sb.AppendLine("- StartService err=183 means Windows still has an old split-driver object; reboot is usually required.");
        sb.AppendLine("- WFP/BFE 0x80320009 means a duplicate split-tunnel object exists, commonly after Amnezia/Mullvad split-driver conflict.");
        sb.AppendLine("- ping 'General failure' while True Split is active is a local WFP/driver block, not an MTU measurement.");
        if (!OperatingSystem.IsWindows())
        {
            sb.AppendLine("(not Windows)");
            return sb.ToString();
        }

        AppendCommand(sb, "sc qc VPNRouter", "sc.exe", "qc", "VPNRouter");
        AppendCommand(sb, "sc query VPNRouter", "sc.exe", "query", "VPNRouter");
        AppendCommand(sb, "sc qc mullvad-split-tunnel", "sc.exe", "qc", "mullvad-split-tunnel");
        AppendCommand(sb, "sc query mullvad-split-tunnel", "sc.exe", "query", "mullvad-split-tunnel");
        AppendCommand(sb, "sc qc AmneziaVPNSplitTunnel", "sc.exe", "qc", "AmneziaVPNSplitTunnel");
        AppendCommand(sb, "sc query AmneziaVPNSplitTunnel", "sc.exe", "query", "AmneziaVPNSplitTunnel");
        AppendCommand(sb, "sc qc AmneziaVPN-service", "sc.exe", "qc", "AmneziaVPN-service");
        AppendCommand(sb, "sc query AmneziaVPN-service", "sc.exe", "query", "AmneziaVPN-service");

        AppendCommand(sb, "matching Win32_SystemDriver rows", "powershell.exe",
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
            "Get-CimInstance Win32_SystemDriver | " +
            "? { $_.Name -match 'mullvad|split|vpnrouter' -or $_.PathName -match 'mullvad|split|vpnrouter' } | " +
            "Select Name,State,Started,StartMode,PathName | Format-Table -AutoSize | Out-String -Width 4096");

        AppendCommand(sb, "matching processes", "powershell.exe",
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
            "Get-Process *mullvad*,*vpnrouter*,sing-box -ErrorAction SilentlyContinue | " +
            "Select Id,ProcessName,Path | Format-Table -AutoSize | Out-String -Width 4096");

        AppendCommand(sb, "recent System driver/service events", "powershell.exe",
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
            "Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=(Get-Date).AddHours(-12)} -ErrorAction SilentlyContinue | " +
            "? { $_.ProviderName -match 'Service Control Manager|BugCheck|WER-SystemErrorReporting' -or $_.Message -match 'mullvad|Amnezia|split|BugCheck|bugcheck' } | " +
            "Select -First 80 TimeCreated,Id,ProviderName,LevelDisplayName,Message | Format-List | Out-String -Width 4096");

        return sb.ToString();

        void AppendCommand(StringBuilder dest, string title, string fileName, params string[] args)
        {
            dest.AppendLine();
            dest.AppendLine("---- " + title + " ----");
            try
            {
                dest.AppendLine(RunCapture(fileName, args));
            }
            catch (Exception ex)
            {
                warnings.Add($"{title} failed: {ex.GetType().Name}");
                dest.AppendLine($"(failed: {ex.GetType().Name}: {ex.Message})");
            }
        }
    }

    /// <summary>
    /// Antivirus + install-integrity snapshot (Windows). Read-only — status queries,
    /// event-log reads, file existence + Authenticode. No mutation of AV settings.
    /// </summary>
    private static string BuildAntivirusSnapshot(List<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Antivirus and install integrity");
        sb.AppendLine("===============================");
        sb.AppendLine("Why this file exists: several users report VPNRouter 'disappearing'");
        sb.AppendLine("after a reboot. The binaries are UNSIGNED and do TUN + process-scan +");
        sb.AppendLine("firewall work, so an antivirus can quarantine them on a boot scan. The");
        sb.AppendLine("installer adds a Defender exclusion, but Tamper Protection silently");
        sb.AppendLine("no-ops that. If our EXEs are missing below, or the AV event log shows a");
        sb.AppendLine("quarantine of them, that is the deletion — add VPNRouter to your AV's");
        sb.AppendLine("exclusions (and the install dir), or use a signed build once available.");

        // Install integrity works cross-platform; the rest is Windows-only.
        sb.AppendLine();
        sb.AppendLine("---- install files present ----");
        var appDir = AppContext.BaseDirectory;
        foreach (var name in new[] { "VPNRouter.App.exe", "VPNRouter.CLI.exe",
                     "VPNRouter.Service.exe", "sing-box.exe", "sing-box-lx.exe" })
        {
            var p = Path.Combine(appDir, name);
            var exists = File.Exists(p);
            sb.AppendLine($"{name,-26} {(exists ? "present" : "MISSING")}");
            if (!exists && (name == "VPNRouter.App.exe" || name.StartsWith("sing-box")))
                warnings.Add($"{name} MISSING from the install dir — likely AV-quarantined (see antivirus-integrity.txt)");
        }
        var installBin = AppPaths.SingBoxExePath;   // %ProgramData%\VPNRouter\bin\sing-box.exe
        sb.AppendLine($"{"bin/sing-box.exe (ProgramData)",-26} {(File.Exists(installBin) ? "present" : "MISSING")}");
        if (!File.Exists(installBin))
            warnings.Add("bin/sing-box.exe MISSING from ProgramData — likely AV-quarantined");

        if (!OperatingSystem.IsWindows())
        {
            sb.AppendLine();
            sb.AppendLine("(not Windows — AV queries skipped)");
            return sb.ToString();
        }

        AppendCommand(sb, "Defender status (RTP / Tamper Protection / mode / version)", "powershell.exe",
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
            "try { Get-MpComputerStatus | Select AMRunningMode,RealTimeProtectionEnabled,IsTamperProtected," +
            "AntivirusEnabled,AMProductVersion,AntivirusSignatureLastUpdated | Format-List | Out-String -Width 4096 } " +
            "catch { 'Get-MpComputerStatus unavailable: ' + $_.Exception.Message }");

        AppendCommand(sb, "registered antivirus products (catches 3rd-party)", "powershell.exe",
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
            "try { Get-CimInstance -Namespace 'root/SecurityCenter2' -ClassName AntiVirusProduct -ErrorAction Stop | " +
            "Select displayName,productState,pathToSignedProductExe | Format-List | Out-String -Width 4096 } " +
            "catch { 'SecurityCenter2 query failed: ' + $_.Exception.Message }");

        AppendCommand(sb, "our Defender exclusion present?", "powershell.exe",
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
            "try { (Get-MpPreference).ExclusionPath | Out-String -Width 4096 } catch { 'n/a' }");

        AppendCommand(sb, "past threat detections (Defender)", "powershell.exe",
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
            "try { Get-MpThreatDetection -ErrorAction SilentlyContinue | " +
            "Select InitialDetectionTime,@{n='Threat';e={$_.ThreatID}},@{n='Files';e={($_.Resources -join '; ')}} | " +
            "Sort InitialDetectionTime -Descending | Select -First 30 | Format-List | Out-String -Width 4096 } " +
            "catch { 'Get-MpThreatDetection unavailable' }");

        // The definitive signal: Defender Operational quarantine/remove/block events
        // (1116 detected, 1117 action taken, 1118/1119 remediation, 5001/5007) that
        // NAME a VPNRouter/sing-box path, over the last 30 days.
        AppendCommand(sb, "Defender quarantine/remove events naming our binaries (30d)", "powershell.exe",
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
            "try { Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Windows Defender/Operational';" +
            "StartTime=(Get-Date).AddDays(-30)} -ErrorAction SilentlyContinue | " +
            "? { $_.Message -match 'VPNRouter|sing-box|singbox' } | " +
            "Select -First 40 TimeCreated,Id,LevelDisplayName,Message | Format-List | Out-String -Width 4096 } " +
            "catch { 'Defender event log unavailable' }");

        AppendCommand(sb, "Authenticode status of our binaries (unsigned = AV-prone)", "powershell.exe",
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
            "@('VPNRouter.App.exe','sing-box.exe','VPNRouter.Service.exe') | % { " +
            "$p = Join-Path '" + appDir.Replace("'", "''").TrimEnd('\\') + "' $_; " +
            "if (Test-Path $p) { $s = Get-AuthenticodeSignature $p; \"$_ -> $($s.Status)\" } else { \"$_ -> MISSING\" } } | " +
            "Out-String -Width 4096");

        return sb.ToString();

        void AppendCommand(StringBuilder dest, string title, string fileName, params string[] args)
        {
            dest.AppendLine();
            dest.AppendLine("---- " + title + " ----");
            try { dest.AppendLine(RunCapture(fileName, args)); }
            catch (Exception ex)
            {
                warnings.Add($"{title} failed: {ex.GetType().Name}");
                dest.AppendLine($"(failed: {ex.GetType().Name}: {ex.Message})");
            }
        }
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

    private static string RunCapture(string fileName, IReadOnlyList<string> args)
    {
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo.FileName = fileName;
        proc.StartInfo.UseShellExecute = false;
        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.RedirectStandardOutput = true;
        proc.StartInfo.RedirectStandardError = true;
        foreach (var arg in args) proc.StartInfo.ArgumentList.Add(arg);

        if (!proc.Start()) return "(failed to start)";
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(5000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return "(timed out after 5s)";
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        var sb = new StringBuilder();
        sb.AppendLine($"exit={proc.ExitCode}");
        if (!string.IsNullOrWhiteSpace(stdout)) sb.AppendLine(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            sb.AppendLine("[stderr]");
            sb.AppendLine(stderr.TrimEnd());
        }
        return sb.ToString().TrimEnd();
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static void AddText(string staging, string name, string content, List<string> entries)
    {
        File.WriteAllText(Path.Combine(staging, name), content);
        entries.Add(name);
    }

    private static void AddConfigBackups(string staging, List<string> entries, List<string> warnings)
    {
        try
        {
            if (!Directory.Exists(AppPaths.DataDir)) return;
            var backups = Directory.GetFiles(AppPaths.DataDir, "config.yaml.*")
                .Where(f => Path.GetFileName(f).StartsWith("config.yaml.unloadable-", StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileName(f).StartsWith("config.yaml.invalid-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(5) // SettingsLoader does not prune backups; keep diagnostics bundles bounded.
                .ToList();

            foreach (var backup in backups)
            {
                var fileName = Path.GetFileName(backup);
                var outName = fileName.Replace("config.yaml.", "config.");
                if (!outName.EndsWith(".redacted.yaml", StringComparison.OrdinalIgnoreCase))
                {
                    outName += ".redacted.yaml";
                }
                AddRedactedFile(staging, backup, outName, DiagnosticsRedactor.RedactConfigYaml, entries, warnings);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"failed to search config backups ({ex.GetType().Name}) — skipped");
        }
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

    /// <summary>
    /// Daily-rolled app logs (<c>vpnrouter{date}.log</c>) modified within the
    /// last <see cref="LogWindowDays"/> days, oldest→newest. Falls back to the
    /// single newest file if none fall inside the window (e.g. the app was idle
    /// for days), so the bundle is never empty. Excludes the tiny
    /// <c>vpnrouter-launch-error.log</c> crash stub (different concern).
    /// </summary>
    internal static List<string> FindRecentAppLogs()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogsDir)) return new List<string>();
            var all = Directory.GetFiles(AppPaths.LogsDir, "vpnrouter*.log")
                .Where(f => !Path.GetFileName(f).Contains("launch-error", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (all.Count == 0) return new List<string>();

            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(LogWindowDays);
            var recent = all
                .Where(f => File.GetLastWriteTimeUtc(f) >= cutoff)
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToList();

            // If nothing is recent (idle for > window), still include the newest
            // one so support has something to look at.
            if (recent.Count == 0)
                recent.Add(all.OrderByDescending(File.GetLastWriteTimeUtc).First());

            return recent;
        }
        catch { return new List<string>(); }
    }

    /// <summary>Read a file even if another process holds it open for writing.</summary>
    private static string ReadAllTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>
    /// Return the last <paramref name="maxLines"/> lines of a file (share-read),
    /// bounded to the last <see cref="MaxTailReadBytes"/> so a huge/corrupt log
    /// can't OOM the bundle (audit MEDIUM, 2026-06-02). When the file exceeds the
    /// cap we seek to EOF − cap and drop the (likely partial) first line.
    /// <c>internal</c> for direct unit testing.
    /// </summary>
    internal static string TailLines(string path, int maxLines)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        bool seeked = fs.Length > MaxTailReadBytes;
        if (seeked) fs.Seek(-MaxTailReadBytes, SeekOrigin.End);
        using var sr = new StreamReader(fs);
        var text = sr.ReadToEnd();
        var all = text.Replace("\r\n", "\n").Split('\n');
        // If we seeked into the middle of the file, the first element is a
        // partial line — drop it so the tail starts on a clean boundary.
        if (seeked && all.Length > 1) all = all.Skip(1).ToArray();
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
