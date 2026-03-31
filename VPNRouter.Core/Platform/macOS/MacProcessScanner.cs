#if !PLATFORM_WINDOWS
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Platform.macOS;

/// <summary>
/// macOS process scanner. Uses Process.GetProcesses() for enumeration,
/// ps(1) for child process detection.
///
/// Key difference from Windows: process names on macOS have no .exe suffix.
/// sing-box process_name matching on macOS expects bare names (e.g. "Discord", not "Discord.exe").
/// This scanner strips .exe from all names before returning.
/// </summary>
public class MacProcessScanner : IProcessScanner
{
    private readonly ILogger _logger;

    private static readonly ConcurrentDictionary<string, Regex> _regexCache =
        new(StringComparer.OrdinalIgnoreCase);

    public MacProcessScanner(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    public ScanResult ScanForProfile(Profile profile)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runningNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var allProcesses = Process.GetProcesses();
        try
        {
            foreach (var p in allProcesses)
            {
                try { runningNow.Add(StripExe(p.ProcessName)); }
                catch { /* process may have exited */ }
            }

            foreach (var rule in profile.Processes)
            {
                // 1. Primary process name — strip .exe for macOS
                found.Add(StripExe(rule.Name));

                // 2. Scan patterns
                foreach (var pattern in rule.ScanPatterns)
                {
                    var strippedPattern = StripExe(pattern);
                    found.Add(strippedPattern);

                    var regex = BuildPatternRegex(strippedPattern);
                    foreach (var name in runningNow)
                    {
                        if (regex.IsMatch(name))
                            found.Add(name);
                    }
                }

                // 3. Include children via ps(1)
                if (rule.IncludeChildren)
                {
                    var mainProcs = allProcesses
                        .Where(p => string.Equals(
                            StripExe(p.ProcessName),
                            StripExe(rule.Name),
                            StringComparison.OrdinalIgnoreCase));

                    foreach (var proc in mainProcs)
                    {
                        var children = GetChildProcessNames(proc.Id);
                        foreach (var child in children)
                            found.Add(StripExe(child));
                    }
                }
            }
        }
        finally
        {
            foreach (var p in allProcesses)
            {
                try { p.Dispose(); } catch { }
            }
        }

        var result = new ScanResult
        {
            ProcessNames = found.ToList(),
            ScannedAt = DateTime.Now
        };

        _logger.Information("[MacProcessScanner] Resolved {Count} process names for profile '{Profile}'",
            result.ProcessNames.Count, profile.Name);

        foreach (var name in result.ProcessNames)
            _logger.Debug("[MacProcessScanner]   → {Name}", name);

        return result;
    }

    // ─── Private ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Enumerate child processes using `ps -eo pid,ppid,comm`.
    /// This is a portable macOS approach (no P/Invoke or WMI needed).
    /// </summary>
    private List<string> GetChildProcessNames(int parentId)
    {
        var names = new List<string>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/ps",
                Arguments = "-eo pid,ppid,comm",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return names;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            // Parse: "  PID  PPID COMM"
            foreach (var line in output.Split('\n').Skip(1)) // skip header
            {
                var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                if (!int.TryParse(parts[0], out var pid)) continue;
                if (!int.TryParse(parts[1], out var ppid)) continue;

                if (ppid == parentId)
                {
                    var comm = parts[2]; // just the exe name, no full path
                    names.Add(comm);
                    // Recurse
                    names.AddRange(GetChildProcessNames(pid));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[MacProcessScanner] ps query failed for parent PID {Id}", parentId);
        }

        return names;
    }

    private static Regex BuildPatternRegex(string pattern)
    {
        return _regexCache.GetOrAdd(pattern, p =>
        {
            var regexPattern = "^" + Regex.Escape(p)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".") + "$";

            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        });
    }

    /// <summary>
    /// Strip .exe suffix — macOS process names don't use it.
    /// Wildcards are passed through unchanged.
    /// </summary>
    private static string StripExe(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }
}
#endif
