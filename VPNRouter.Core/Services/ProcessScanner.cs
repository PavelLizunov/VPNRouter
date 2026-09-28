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

public class ProcessScanner : IProcessScanner
{
    private readonly ILogger _logger;

    private static readonly ConcurrentDictionary<string, Regex> _regexCache = new(StringComparer.OrdinalIgnoreCase);

    public ProcessScanner(ILogger? logger = null)
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
                try { runningNow.Add(p.ProcessName + ".exe"); }
                catch {  }
            }

            foreach (var rule in profile.Processes)
            {
                found.Add(NormalizeName(rule.Name));

                foreach (var pattern in rule.ScanPatterns)
                {
                    found.Add(NormalizeName(pattern.Contains('*') || pattern.Contains('?')
                        ? pattern
                        : pattern));

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
                        _logger.Warning("[ProcessScanner] scan_pattern '{Pattern}' exceeded the {Ms}ms match timeout — skipping it (check the profile for a catastrophic wildcard)", pattern, PatternMatchTimeoutMs);
                    }
                }

            }

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
                            try { rootPids.Add(p.Id); } catch {  }
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

    public static bool MatchesPattern(string processName, string pattern)
    {
        try
        {
            return BuildPatternRegex(pattern).IsMatch(processName);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private IEnumerable<string> CollectDescendantNames(HashSet<int> rootPids)
    {
#if PLATFORM_WINDOWS
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
        yield break;
#endif
    }

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
