using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Shared health-check logic. Consumed by the CLI <c>doctor</c> command
/// and the UI "Run Health Check" menu item. Output is a plain-text
/// report suitable for pasting into a bug report.
///
/// v2.24.1 of plans/vpnrouter-self-healing.md.
/// </summary>
public static class HealthCheck
{
    public enum Level { Ok, Warn, Err }

    public readonly record struct Result(Level Severity, string Message);

    /// <summary>
    /// Run all checks, return the ordered result list. No formatting,
    /// no writing to disk — just the facts. Callers render / persist
    /// as they wish.
    /// </summary>
    public static List<Result> RunAll()
    {
        var results = new List<Result>();

        // ── Config ──
        var configPath = AppPaths.ConfigYamlPath;
        if (File.Exists(configPath))
        {
            try
            {
                var yaml = File.ReadAllText(configPath);
                var settings = SettingsLoader.Parse(yaml);
                results.Add(new(Level.Ok, $"config.yaml parses (schema v{settings.SchemaVersion})"));

                if (settings.SchemaVersion < AppSettings.CurrentSchemaVersion)
                    results.Add(new(Level.Warn,
                        $"config.yaml is schema v{settings.SchemaVersion}, current is v{AppSettings.CurrentSchemaVersion} — will migrate on next normal launch"));
                else if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
                    results.Add(new(Level.Err,
                        $"config.yaml is schema v{settings.SchemaVersion} but this VPNRouter only knows up to v{AppSettings.CurrentSchemaVersion} — upgrade VPNRouter or revert config.yaml"));

                // Per-mode config validation. Subscribe mode stores servers under
                // app.subscriptions[].servers (resolved into Vless.Servers at startup
                // by SubscriptionResolver). Custom mode points at an external sing-box
                // JSON file. Generated/legacy mode reads vless.servers directly.
                var mode = settings.App?.ConfigMode?.ToLowerInvariant() ?? "generated";
                var subscriptionServerCount = settings.App?.Subscriptions?
                    .Where(s => s.Enabled)
                    .Sum(s => s.Servers?.Count ?? 0) ?? 0;

                var hasLegacyVless = settings.Vless != null &&
                    (settings.Vless.Servers.Count > 0 || !string.IsNullOrWhiteSpace(settings.Vless.Server));

                switch (mode)
                {
                    case "subscribe":
                        if (subscriptionServerCount > 0)
                            results.Add(new(Level.Ok, $"subscription has {subscriptionServerCount} cached server(s)"));
                        else
                            results.Add(new(Level.Warn, "subscription has no cached servers — refresh via GUI/CLI or check subscription URL"));
                        break;

                    case "custom":
                        var customPath = settings.App?.CustomConfig ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(customPath))
                            results.Add(new(Level.Err, "config_mode=custom but custom_config path is empty"));
                        else if (!File.Exists(customPath))
                            results.Add(new(Level.Err, $"custom_config file not found: {customPath}"));
                        else
                            results.Add(new(Level.Ok, $"custom config at {customPath}"));
                        break;

                    default: // "generated" and anything else falls through
                        if (!hasLegacyVless)
                            results.Add(new(Level.Warn, "VLESS config has no servers — VPN will not start"));
                        break;
                }
            }
            catch (Exception ex)
            {
                results.Add(new(Level.Err, $"config.yaml parse failed: {ex.Message}"));
            }
        }
        else
        {
            results.Add(new(Level.Warn,
                $"config.yaml missing at {configPath} (will be created on first launch)"));
        }

        // ── User profile catalogue ──
        var userCatalogue = Path.Combine(AppPaths.ProfilesDir, "default.json");
        if (File.Exists(userCatalogue))
        {
            try
            {
                var json = File.ReadAllText(userCatalogue);
                var collection = Newtonsoft.Json.JsonConvert
                    .DeserializeObject<ProfileCollection>(json);
                if (collection == null || collection.Profiles == null)
                {
                    results.Add(new(Level.Err,
                        $"user catalogue {userCatalogue} is empty/unparseable — will be quarantined on next launch"));
                }
                else
                {
                    results.Add(new(Level.Ok, $"user catalogue has {collection.Profiles.Count} profiles"));

                    var expected = new[] {
                        "Discord_Privacy", "Messengers", "AI_Tools", "Browsers",
                        "Work_Suite", "Streaming", "Gaming", "Privacy_Shell"
                    };
                    var present = collection.Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var missing = expected.Count(e => !present.Contains(e));
                    if (missing >= 3)
                        results.Add(new(Level.Warn,
                            $"user catalogue missing {missing}/{expected.Length} v2.22 standard groups — will be quarantined on next launch"));
                }
            }
            catch (Exception ex)
            {
                results.Add(new(Level.Err,
                    $"user catalogue {userCatalogue}: {ex.Message} — will be quarantined"));
            }
        }
        else
        {
            results.Add(new(Level.Ok, "no user catalogue override (using bundled — recommended)"));
        }

        // ── sing-box binary ──
        var singboxPath = AppPaths.SingBoxExePath;
        if (File.Exists(singboxPath))
        {
            var size = new FileInfo(singboxPath).Length;
            results.Add(new(Level.Ok, $"sing-box at {singboxPath} ({size / 1024 / 1024} MB)"));
        }
        else
        {
            var bundled = Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");
            if (File.Exists(bundled))
                results.Add(new(Level.Warn,
                    $"sing-box not deployed to {singboxPath} — will be copied from bundle on first start"));
            else
                results.Add(new(Level.Err,
                    $"sing-box not found at {singboxPath} OR bundled at {bundled}"));
        }

        // ── Update receipt ──
        var receipt = UpdateChecker.CheckInstallReceipt(VPNRouter.Core.AppVersion.Version);
        if (!string.IsNullOrEmpty(receipt))
            results.Add(new(Level.Warn, receipt));

        // ── Linux: pkexec / polkit availability (v2.30 #3.3) ──
        // Some minimal distros (Alpine, headless servers) ship without
        // polkit. Without pkexec, the Stop escalation chain falls through
        // to sudo -n (which fails fast unless NOPASSWD sudoers is set up),
        // and the auto-update privilege escalation breaks. Detect at
        // health-check time so the user can install policykit-1 BEFORE
        // they hit the failure mid-Stop.
        if (OperatingSystem.IsLinux())
        {
            if (File.Exists("/usr/bin/pkexec"))
            {
                results.Add(new(Level.Ok, "pkexec / polkit available"));
            }
            else
            {
                results.Add(new(Level.Warn,
                    "pkexec not found at /usr/bin/pkexec — auto-update + " +
                    "elevated Stop will fail unless NOPASSWD sudoers is " +
                    "configured. Install policykit-1 (apt) or polkit (dnf)."));
            }
        }

        // ── State / running indicator ──
        // Parse state.json inline rather than referencing StateFile which
        // lives in the CLI project. Structure: { "sing_box_pid": N, ... }.
        var statePath = AppPaths.StatePath;
        if (File.Exists(statePath))
        {
            try
            {
                var json = File.ReadAllText(statePath);
                var state = Newtonsoft.Json.Linq.JObject.Parse(json);
                int pid = state["sing_box_pid"]?.ToObject<int>()
                       ?? state["SingBoxPid"]?.ToObject<int>()
                       ?? 0;
                if (pid > 0)
                {
                    try
                    {
                        var proc = System.Diagnostics.Process.GetProcessById(pid);
                        if (!proc.HasExited)
                            results.Add(new(Level.Ok, $"sing-box running (PID {pid})"));
                        else
                            results.Add(new(Level.Warn,
                                $"state.json references dead sing-box PID {pid} — orphan state"));
                        try { proc.Dispose(); } catch { }
                    }
                    catch (ArgumentException)
                    {
                        results.Add(new(Level.Warn,
                            $"state.json references dead sing-box PID {pid} — orphan state"));
                    }
                }
                else
                {
                    results.Add(new(Level.Ok, "state.json present but no sing-box PID recorded"));
                }
            }
            catch (Exception ex)
            {
                results.Add(new(Level.Warn, $"state.json parse error: {ex.Message}"));
            }
        }
        else
        {
            results.Add(new(Level.Ok, "no running-state file (app is stopped)"));
        }

        // ── Lock file crash detection ──
        // Note: calling DetectPreviousCrash here would consume (delete)
        // the lock on every health check. For the doctor we want to
        // observe, not consume. So we open the file non-destructively
        // if it exists.
        var lockPath = Path.Combine(AppPaths.DataDir, "running.lock");
        if (File.Exists(lockPath))
        {
            try
            {
                var lines = File.ReadAllLines(lockPath);
                if (lines.Length >= 1 && int.TryParse(lines[0].Trim(), out var pid))
                {
                    try
                    {
                        var p = System.Diagnostics.Process.GetProcessById(pid);
                        try { p.Dispose(); } catch { }
                        results.Add(new(Level.Ok, $"lockfile present, held by live PID {pid}"));
                    }
                    catch
                    {
                        results.Add(new(Level.Warn,
                            $"lockfile references dead PID {pid} — previous run did not shut down cleanly"));
                    }
                }
            }
            catch { /* unreadable lockfile — tolerated */ }
        }

        // ── Process / Service ownership (v2.31.6-r20) ──
        // User-reported pattern (spark-wraith 2026-05-04): "press disconnect,
        // VPN turns back on after a second". Root cause: when the Windows
        // Service is installed and running, it owns its own sing-box. The
        // GUI / CLI sees the running sing-box via process scan and shows
        // IsConnected=true, but its own _engine._singBox is null — so its
        // Stop is a no-op. Service then keeps the tunnel up.
        //
        // Surface this state in doctor so users (and we, when triaging
        // logs) can immediately tell whether a multi-owner conflict is
        // happening before chasing other symptoms.
        try
        {
            var singboxProcs = Process.GetProcessesByName("sing-box");
            var singboxPids = string.Join(", ", singboxProcs.Select(p => p.Id));
            var singboxCount = singboxProcs.Length;
            foreach (var p in singboxProcs) { try { p.Dispose(); } catch { } }

            var appProcs = Process.GetProcessesByName("VPNRouter.App");
            var appCount = appProcs.Length;
            foreach (var p in appProcs) { try { p.Dispose(); } catch { } }

            if (singboxCount == 0)
                results.Add(new(Level.Ok, "no sing-box process running"));
            else if (singboxCount == 1)
                results.Add(new(Level.Ok, $"1 sing-box process running (PID {singboxPids})"));
            else
                results.Add(new(Level.Warn,
                    $"{singboxCount} sing-box processes running (PIDs {singboxPids}) — multiple owners is unusual; one of them is likely orphaned"));

            if (OperatingSystem.IsWindows())
                CheckWindowsServiceOwnership(results, singboxCount, appCount);
        }
        catch (Exception ex)
        {
            results.Add(new(Level.Warn, $"process inventory check failed: {ex.Message}"));
        }

        // ── AppPaths directories ──
        foreach (var dir in new[] { AppPaths.DataDir, AppPaths.LogsDir, AppPaths.CacheDir, AppPaths.BinDir, AppPaths.ProfilesDir })
        {
            if (!Directory.Exists(dir))
                results.Add(new(Level.Warn, $"directory missing: {dir} (will be created on first launch)"));
        }

        return results;
    }

    /// <summary>
    /// Windows-only: report whether the VPNRouter Service is installed and
    /// running, and flag the multi-owner state where Service + GUI both
    /// hold sing-box. Uses sc.exe query so we don't need a hard dependency
    /// on System.ServiceProcess in Core (it's Windows-only and pulls in
    /// extra closure mass on Linux/Mac builds).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void CheckWindowsServiceOwnership(
        List<Result> results, int singboxCount, int appCount)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", "query VPNRouter")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            // sc.exe exits non-zero when the service doesn't exist (1060).
            if (proc.ExitCode != 0)
            {
                results.Add(new(Level.Ok, "Windows Service not installed (running in user mode)"));
                return;
            }

            // STATE line is the canonical signal. Possible values:
            // RUNNING / STOPPED / START_PENDING / STOP_PENDING / PAUSED
            var running = stdout.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
            var stopped = stdout.Contains("STOPPED", StringComparison.OrdinalIgnoreCase);

            if (running)
            {
                results.Add(new(Level.Ok, "Windows Service installed: RUNNING"));
                if (singboxCount > 0 && appCount > 0)
                    results.Add(new(Level.Warn,
                        "multi-owner state: Windows Service + GUI both running — disconnect button in GUI may not stop the tunnel; use 'sc stop VPNRouter' or restart the GUI (r20+ stops the Service automatically)"));
            }
            else if (stopped)
            {
                results.Add(new(Level.Ok, "Windows Service installed: STOPPED"));
            }
            else
            {
                results.Add(new(Level.Warn,
                    "Windows Service installed but state is neither RUNNING nor STOPPED — see 'sc query VPNRouter'"));
            }
        }
        catch (Exception ex)
        {
            results.Add(new(Level.Warn, $"could not query Windows Service: {ex.Message}"));
        }
    }

    /// <summary>
    /// Render results as plain-text suitable for a text file / bug report.
    /// Uses ASCII markers (OK / WARN / ERR) rather than fancy unicode so
    /// it opens cleanly in notepad.exe / gedit / any text viewer.
    /// </summary>
    public static string FormatReport(IReadOnlyList<Result> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VPNRouter Health Check");
        sb.AppendLine($"Version:   {VPNRouter.Core.AppVersion.Version}");
        sb.AppendLine($"Time:      {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Data dir:  {AppPaths.DataDir}");
        sb.AppendLine($"OS:        {Environment.OSVersion}");
        sb.AppendLine();

        int warnings = 0, errors = 0;
        foreach (var r in results)
        {
            var tag = r.Severity switch
            {
                Level.Ok   => "[OK]  ",
                Level.Warn => "[WARN]",
                Level.Err  => "[ERR] ",
                _          => "[?]   "
            };
            if (r.Severity == Level.Warn) warnings++;
            if (r.Severity == Level.Err)  errors++;
            sb.AppendLine($"{tag} {r.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("──────────────────────────────────────────────────────");
        if (errors == 0 && warnings == 0)
            sb.AppendLine("All checks passed.");
        else
            sb.AppendLine($"Summary: {warnings} warning(s), {errors} error(s).");

        return sb.ToString();
    }
}
