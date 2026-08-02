using System.Collections.Concurrent;
using System.Diagnostics;
#if PLATFORM_WINDOWS
using System.Management;
#endif
using System.Text.RegularExpressions;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Scans running processes by name and wildcard patterns.
/// Child process detection uses WMI on Windows, skipped on other platforms.
/// </summary>
public class ProcessScanner : IProcessScanner
{
    private readonly ILogger _logger;

    // Cache compiled regexes — avoids re-JITting on every scan
    private static readonly ConcurrentDictionary<string, Regex> _regexCache = new(StringComparer.OrdinalIgnoreCase);

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

        // 1. Get all currently running process names (dispose handles after use)
        var allProcesses = Process.GetProcesses();
        try
        {
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
                    try
                    {
                        foreach (var name in runningNow)
                        {
                            if (regex.IsMatch(name))
                                found.Add(NormalizeName(name));
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // B3-1: a pathological scan_pattern blew the 250ms match
                        // cap. Skip it (fail-safe — the pattern matches nothing)
                        // rather than wedge the scan. Explicit-name rules above
                        // are unaffected.
                        _logger.Warning("[ProcessScanner] scan_pattern '{Pattern}' exceeded the {Ms}ms match timeout — skipping it (check the profile for a catastrophic wildcard)", pattern, PatternMatchTimeoutMs);
                    }
                }

                // 4. include_children handled in batch after the per-rule
                //    loop — see below. Each per-rule WMI query used to fire
                //    N recursive calls and could take 20-60 s for a split
                //    profile with 20 browsers running. Now all of
                //    include_children is one WMI snapshot + in-memory
                //    tree walk.
            }

            // 5. Batch include_children: one WMI snapshot of the whole
            //    process table, then walk the parent→children tree in
            //    memory. v2.22.4 self-healing — replaces the per-rule
            //    recursive GetChildProcessNamesWmi which blocked
            //    ProcessScanner for minutes on overloaded systems.
            var childRules = profile.Processes
                .Where(r => r.IncludeChildren)
                .ToList();

            if (childRules.Count > 0)
            {
                var rootPids = new HashSet<int>();
                foreach (var rule in childRules)
                {
                    var ruleName = rule.Name;
                    foreach (var p in allProcesses)
                    {
                        if (string.Equals(p.ProcessName + ".exe", ruleName, StringComparison.OrdinalIgnoreCase))
                        {
                            try { rootPids.Add(p.Id); } catch { /* process exited */ }
                        }
                    }
                }
                if (rootPids.Count > 0)
                {
                    foreach (var childName in CollectDescendantNames(rootPids))
                        found.Add(NormalizeName(childName));
                }
            }
        }
        finally
        {
            // Dispose all Process handles to prevent resource leak
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
        try
        {
            return BuildPatternRegex(pattern).IsMatch(processName);
        }
        catch (RegexMatchTimeoutException)
        {
            // B3-1: fail-safe. This runs on the process-launch hot path
            // (StartupPipeline), so a pattern that can't decide within 250ms is
            // treated as no-match rather than stalling every process launch.
            return false;
        }
    }

    // ─── Private ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Given a set of root PIDs, return all descendant process names
    /// (direct and transitive children). One WMI snapshot of the whole
    /// process table, then tree walk in memory — O(1) WMI queries per
    /// scan regardless of how many rules or how deep the tree.
    /// </summary>
    private IEnumerable<string> CollectDescendantNames(HashSet<int> rootPids)
    {
#if PLATFORM_WINDOWS
        // Snapshot (pid → name) and (parentPid → list of child pids)
        var nameByPid = new Dictionary<int, string>();
        var childrenByParent = new Dictionary<int, List<int>>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name FROM Win32_Process");
            foreach (ManagementObject obj in searcher.Get())
            {
                int pid, parent;
                string name;
                try
                {
                    pid = Convert.ToInt32(obj["ProcessId"]);
                    parent = Convert.ToInt32(obj["ParentProcessId"]);
                    name = obj["Name"]?.ToString() ?? string.Empty;
                }
                catch { continue; }

                if (pid <= 0) continue;
                nameByPid[pid] = name;
                if (!childrenByParent.TryGetValue(parent, out var list))
                {
                    list = new List<int>();
                    childrenByParent[parent] = list;
                }
                list.Add(pid);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[ProcessScanner] WMI snapshot failed — returning empty descendant list");
            yield break;
        }

        // BFS from each root, emitting process names. visited set caps
        // runtime for any pathological tree (shouldn't happen, but cheap
        // insurance against cycles if WMI returns inconsistent data).
        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (var root in rootPids)
            if (visited.Add(root))
                queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            if (childrenByParent.TryGetValue(pid, out var kids))
            {
                foreach (var k in kids)
                {
                    if (!visited.Add(k)) continue;
                    if (nameByPid.TryGetValue(k, out var kname) && !string.IsNullOrEmpty(kname))
                        yield return kname;
                    queue.Enqueue(k);
                }
            }
        }
#else
        // Non-Windows: child process detection not implemented yet
        // (TODO: macOS ps/sysctl). Return empty — Linux scanner uses
        // pattern-based matching which is enough for typical cases.
        yield break;
#endif
    }

    // v2.40.0 Phase B (B3-1): hard ceiling on regex match time. scan_patterns
    // come from untrusted profile JSON (GitHub > Local source priority) and the
    // Apps UI; a pattern like "a*a*a*...b.exe" compiles to "^a.*a.*...b\.exe$",
    // which catastrophically backtracks (measured ~8.5s on a single non-matching
    // long process name). The match runs on hot paths — the process-launch
    // handler (per launch, system-wide) and every debounced rescan — so an
    // unbounded match wedges the routing engine and INTENDED apps leak by
    // starvation. 250ms is >1000x a legitimate match (process names are short),
    // so it never trips on real input but turns a pathological pattern into a
    // fast RegexMatchTimeoutException the callers treat as "no match" (fail-safe).
    private const int PatternMatchTimeoutMs = 250;

    private static Regex BuildPatternRegex(string pattern)
    {
        return _regexCache.GetOrAdd(pattern, p =>
        {
            var regexPattern = "^" + Regex.Escape(p)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".") + "$";

            return new Regex(regexPattern,
                RegexOptions.IgnoreCase | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(PatternMatchTimeoutMs));
        });
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
