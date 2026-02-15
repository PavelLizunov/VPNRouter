using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Phase 1: Scans running processes by name and wildcard patterns.
/// Phase 3 will add ETW real-time monitoring and child process detection.
/// </summary>
public class ProcessScanner
{
    private readonly ILogger _logger;

    public ProcessScanner(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves all process names that should be routed through VPN
    /// based on the given profile. Uses scan_patterns for pre-population
    /// (eliminates most reload triggers at runtime).
    /// </summary>
    public ScanResult ScanForProfile(Profile profile)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runningNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Get all currently running process names
        var allProcesses = Process.GetProcesses();
        foreach (var p in allProcesses)
        {
            try { runningNow.Add(p.ProcessName + ".exe"); }
            catch { /* process may have exited */ }
        }

        foreach (var rule in profile.Processes)
        {
            // 2. Add the primary process name directly
            found.Add(NormalizeName(rule.Name));

            // 3. Expand scan_patterns → add all matching names regardless of running state
            //    This pre-populates the config so runtime reloads are rare
            foreach (var pattern in rule.ScanPatterns)
            {
                found.Add(NormalizeName(pattern.Contains('*') || pattern.Contains('?')
                    ? pattern  // keep as pattern for sing-box regex later
                    : pattern));

                // Also match against currently running processes
                var regex = BuildPatternRegex(pattern);
                foreach (var name in runningNow)
                {
                    if (regex.IsMatch(name))
                        found.Add(NormalizeName(name));
                }
            }

            // 4. If include_children, find currently running children via WMI
            if (rule.IncludeChildren)
            {
                var mainProcs = allProcesses
                    .Where(p => string.Equals(p.ProcessName + ".exe", rule.Name, StringComparison.OrdinalIgnoreCase));

                foreach (var proc in mainProcs)
                {
                    var children = GetChildProcessNames(proc.Id);
                    foreach (var child in children)
                        found.Add(NormalizeName(child));
                }
            }
        }

        var result = new ScanResult
        {
            ProcessNames = found.ToList(),
            ScannedAt = DateTime.Now
        };

        _logger.Information("[ProcessScanner] Resolved {Count} process names for profile '{Profile}'",
            result.ProcessNames.Count, profile.Name);

        foreach (var name in result.ProcessNames)
            _logger.Debug("[ProcessScanner]   → {Name}", name);

        return result;
    }

    /// <summary>
    /// Check if a process name matches a wildcard pattern (e.g., Discord*.exe).
    /// </summary>
    public static bool MatchesPattern(string processName, string pattern)
    {
        return BuildPatternRegex(pattern).IsMatch(processName);
    }

    // ─── Private ──────────────────────────────────────────────────────────────

    private List<string> GetChildProcessNames(int parentId)
    {
        var names = new List<string>();
        try
        {
            var query = $"SELECT ProcessId, Name FROM Win32_Process WHERE ParentProcessId = {parentId}";
            using var searcher = new ManagementObjectSearcher(query);

            foreach (ManagementObject obj in searcher.Get())
            {
                var childId = Convert.ToInt32(obj["ProcessId"]);
                var childName = obj["Name"]?.ToString() ?? string.Empty;

                if (!string.IsNullOrEmpty(childName))
                {
                    names.Add(childName);
                    // Recursively find grandchildren
                    names.AddRange(GetChildProcessNames(childId));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[ProcessScanner] WMI query failed for parent PID {Id}", parentId);
        }

        return names;
    }

    private static Regex BuildPatternRegex(string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";

        return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static string NormalizeName(string name)
    {
        // Preserve original case — sing-box process_name matching is case-sensitive
        // and Windows QueryFullProcessImageName returns filesystem casing
        name = name.Trim();
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !name.Contains('*') && !name.Contains('?'))
            name += ".exe";
        return name;
    }
}

public class ScanResult
{
    public List<string> ProcessNames { get; init; } = new();
    public DateTime ScannedAt { get; init; }
    public bool HasChanges(ScanResult? previous)
    {
        if (previous == null) return true;
        return !new HashSet<string>(ProcessNames, StringComparer.OrdinalIgnoreCase)
            .SetEquals(previous.ProcessNames);
    }
}
