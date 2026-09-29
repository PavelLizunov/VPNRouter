using System.Text;
using System.Text.RegularExpressions;
using VPNRouter.Core.Services.Diagnostics;

namespace VPNRouter.Core.Services;

public static class CrashReporter
{
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
            e.SetObserved();
        };

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
            catch { }
        };
    }

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
                sb.AppendLine(DiagnosticsRedactor.RedactLogText(ex.ToString()));
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("── (no exception object — crash source unknown) ──");
                sb.AppendLine();
            }

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
                        foreach (var line in DiagnosticsExporter.TailLines(logs, 200).Split(Environment.NewLine))
                            sb.AppendLine(DiagnosticsRedactor.RedactLogText(line));
                    }
                }
            }
            catch { }

            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch
        {
            return null;
        }
    }

    // Crash reports may be shared: proxy URIs, subscription URLs, UUIDs and keys must never appear verbatim.
    private static readonly Regex _proxyUriPattern = new(
        @"\b(vless|vmess|trojan|tg|(?:ss|shadowsocks)(?:\+[a-z0-9_-]+)?|hysteria2?|hy2|tuic|naive(?:\+(?:https|quic))?|amneziawg|awg|wireguard|wgturn|socks[45]?a?h?|snell|shadowtls|ssh|dns-tunnel)://\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _httpUrlPattern = new(
        @"(https?://)(?:[^@\s/?#]+@)?([^\s/?#]+)([/?#]\S*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _uuidPattern = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled);

    private static readonly Regex _longBase64Pattern = new(
        @"\b[A-Za-z0-9+/_\-]{40,}={0,2}\b",
        RegexOptions.Compiled);

    private static readonly Regex _tokenParamPattern = new(
        @"([?&])((?:[a-z0-9_]*[_-])?(?:token|secret|api[_-]?key|auth|pass(?:word|wd)?|private[_-]?key|psk|session|session[_-]?id|sig|signature|credential|sid|short[_-]?id|key)|client[_-]?secret|client[_-]?pass(?:word|wd)?|refresh[_-]?token|access[_-]?token)=[^&\s]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ScrubSecrets(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;

        var s = _proxyUriPattern.Replace(input, m => $"{m.Groups[1].Value}://[redacted]");
        s = _httpUrlPattern.Replace(s, m =>
            m.Groups[3].Success && m.Groups[3].Length > 0
                ? $"{m.Groups[1].Value}{m.Groups[2].Value}/[redacted]"
                : $"{m.Groups[1].Value}{m.Groups[2].Value}");
        s = _uuidPattern.Replace(s, "<uuid>");
        s = _longBase64Pattern.Replace(s, "<key>");
        s = _tokenParamPattern.Replace(s, m => $"{m.Groups[1].Value}{m.Groups[2].Value}=[REDACTED]");
        return s;
    }
}
