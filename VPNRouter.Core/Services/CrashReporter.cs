using System.Text;

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
                sb.AppendLine(ex.ToString());
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
                        var lines = File.ReadAllLines(logs);
                        var startIndex = Math.Max(0, lines.Length - 200);
                        for (int i = startIndex; i < lines.Length; i++)
                            sb.AppendLine(lines[i]);
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
}
