using System.Text;
using System.Text.RegularExpressions;
using VPNRouter.Core.Services.Diagnostics;

namespace VPNRouter.Core.Services;

/// <summary>
/// Writes crash reports to <c>%DataDir%\crashes\</c> on unhandled
/// exceptions. Report includes version, OS, timestamp, the exception
/// chain, and the tail of the current app log. Intended for support:
/// user can attach the file to a bug report without hunting for the
/// right log lines themselves.
///
/// Automatic opt-in at app startup via <see cref="Install"/>. No data
/// leaves the machine — future versions may add an optional upload
/// toggle.
///
/// v2.24.0 Level 3 of plans/vpnrouter-self-healing.md.
/// </summary>
public static class CrashReporter
{
    /// <summary>
    /// Hook <c>AppDomain.UnhandledException</c> (and the task scheduler's
    /// unobserved-task-exception event) so any crash dumps a report.
    /// Call once at app startup, before other code that might throw.
    /// </summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            WriteReport(ex, fatal: e.IsTerminating);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteReport(e.Exception, fatal: false);
            e.SetObserved();  // otherwise the process may terminate
        };

        // v2.36.0-r5 (brat 2026-05-24 silent-death diagnostic): write a
        // shutdown marker on every process exit — graceful OR ungraceful.
        //
        // Brat reported a silent death where BOTH app + sing-box stopped
        // logging at 12:18:18, no "Stopping" / "crashed" / exception entries
        // anywhere. App stayed alive in process list but VPN appeared
        // stopped + logger was silent. Hard to diagnose without
        // distinguishing "logger stopped writing but process still alive"
        // vs "process exited via some unlogged path".
        //
        // This marker resolves the ambiguity: if a shutdown-<stamp>.txt
        // appears in crashes/ after a silent-death incident, the process
        // DID exit (just without normal Stop logging). If no marker,
        // logger died but process kept running (different bug class).
        //
        // ProcessExit handlers in .NET Core have NO 2-second time limit
        // (unlike Framework), so we can safely do FileIO here. Catch-all
        // defensive try/catch — marker writing must never throw and
        // delay shutdown.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                var crashesDir = Path.Combine(AppPaths.DataDir, "crashes");
                Directory.CreateDirectory(crashesDir);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                var path = Path.Combine(crashesDir, $"shutdown-{stamp}.txt");
                var content =
                    $"VPNRouter shutdown marker{Environment.NewLine}" +
                    $"Version:   {VPNRouter.Core.AppVersion.Version}{Environment.NewLine}" +
                    $"Time:      {DateTime.UtcNow:o}{Environment.NewLine}" +
                    $"WorkingSet: {Environment.WorkingSet / (1024 * 1024)} MB{Environment.NewLine}" +
                    $"ExitCode:   {Environment.ExitCode}{Environment.NewLine}" +
                    $"Note: graceful shutdown — ProcessExit ApplyDomain handler fired.{Environment.NewLine}" +
                    $"      For ungraceful (Kill/OOM) shutdowns this file is absent.{Environment.NewLine}";
                File.WriteAllText(path, content);
            }
            catch { /* never throw from ProcessExit */ }
        };
    }

    /// <summary>
    /// Write a crash report for the given exception. Swallows all errors
    /// — the crash reporter itself must never throw. Returns the path
    /// written (or null if it couldn't write at all).
    /// </summary>
    public static string? WriteReport(Exception? ex, bool fatal = false)
    {
        try
        {
            var crashesDir = Path.Combine(AppPaths.DataDir, "crashes");
            Directory.CreateDirectory(crashesDir);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var path = Path.Combine(crashesDir, $"crash-{stamp}.txt");

            var sb = new StringBuilder();
            sb.AppendLine($"VPNRouter crash report");
            sb.AppendLine($"Version:   {VPNRouter.Core.AppVersion.Version}");
            sb.AppendLine($"Fatal:     {fatal}");
            sb.AppendLine($"Time:      {DateTime.UtcNow:o}");
            sb.AppendLine($"OS:        {Environment.OSVersion}");
            sb.AppendLine($"Platform:  {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "other")}");
            sb.AppendLine($"64-bit:    {Environment.Is64BitProcess}");
            sb.AppendLine($"CLR:       {Environment.Version}");
            sb.AppendLine();

            if (ex != null)
            {
                sb.AppendLine("──── Exception ────");
                sb.AppendLine(ScrubSecrets(ex.ToString()));
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("── (no exception object — crash source unknown) ──");
                sb.AppendLine();
            }

            // Tail of the current app log for context.
            try
            {
                var logsDir = AppPaths.LogsDir;
                if (Directory.Exists(logsDir))
                {
                    var logs = Directory.GetFiles(logsDir, "vpnrouter*.log")
                        .OrderByDescending(File.GetLastWriteTime)
                        .FirstOrDefault();
                    if (!string.IsNullOrEmpty(logs) && File.Exists(logs))
                    {
                        sb.AppendLine($"──── Tail of {Path.GetFileName(logs)} (last 200 lines) ────");
                        // OBS-2 (audit R06): bounded tail (12 MB cap) — File.ReadAllLines
                        // would OOM on a runaway multi-GB log.
                        foreach (var line in DiagnosticsExporter.TailLines(logs, 200).Split(Environment.NewLine))
                            sb.AppendLine(ScrubSecrets(line));
                    }
                }
            }
            catch { /* best-effort */ }

            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch
        {
            return null;
        }
    }

    // ── PII scrubbing ───────────────────────────────────────────────────
    //
    // Crash reports go on disk in user-readable form and may be shared
    // with support; the user-pasted vless URI, subscription URL, UUIDs
    // and Reality public keys must not appear verbatim. The scrubber is
    // best-effort — it strips the cases we know we leak (URIs in the
    // exception message, log lines containing them) but won't catch a
    // payload encoded with a custom format. Callers should not rely on
    // it for compliance-grade redaction.
    //
    // Patterns:
    //   • vless://… / vmess://… / trojan://… / ss://… / hysteria…://… —
    //     full URI replaced with "<scheme>://[redacted]".
    //   • Plain http(s):// URLs longer than ~16 chars get path/query
    //     replaced ("https://example.com/[redacted]"). Domain is kept
    //     so log lines stay diagnostic ("could not reach foo.bar").
    //   • UUIDs replaced with "<uuid>".
    //   • Long base64-ish runs (≥40 chars of A-Za-z0-9+/=_-) replaced
    //     with "<key>" — covers Reality pbk, sid, and similar.

    private static readonly Regex _proxyUriPattern = new(
        @"\b(vless|vmess|trojan|ss|shadowsocks|hysteria2?|hy2|tuic|naive(\+(https|quic))?|amneziawg|awg|wireguard|wgturn|socks5?h?|dns-tunnel)://\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _httpUrlPattern = new(
        @"(https?://)(?:[^@/\s]+@)?([^\s/?#]+)(/\S*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _uuidPattern = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled);

    private static readonly Regex _longBase64Pattern = new(
        @"\b[A-Za-z0-9+/_\-]{40,}={0,2}\b",
        RegexOptions.Compiled);

    // clash_api secret (32 hex) is too short for _longBase64Pattern (>=40)
    // and ws/wss is not in _proxyUriPattern; match the token param directly.
    private static readonly Regex _tokenParamPattern = new(
        @"([?&])token=[^&\s]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Best-effort secret scrubbing on a single line of text. Public so
    /// callers serialising their own context (e.g. an Android Java
    /// uncaught-handler bridging via a JSON file) can apply the same
    /// rules before writing the report.
    /// </summary>
    public static string ScrubSecrets(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;

        var s = _proxyUriPattern.Replace(input, m => $"{m.Groups[1].Value}://[redacted]");
        s = _httpUrlPattern.Replace(s, m =>
            m.Groups[3].Success ? $"{m.Groups[1].Value}{m.Groups[2].Value}/[redacted]" : $"{m.Groups[1].Value}{m.Groups[2].Value}");
        s = _uuidPattern.Replace(s, "<uuid>");
        s = _longBase64Pattern.Replace(s, "<key>");
        s = _tokenParamPattern.Replace(s, m => $"{m.Groups[1].Value}token=[REDACTED]");
        return s;
    }
}
