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
/// macOS process scanner. Uses a single `ps -eo pid,ppid,comm` call for all data.
/// No Process.GetProcesses() — it's too slow on macOS (sysctl per-process).
///
/// Key difference from Windows: process names on macOS have no .exe suffix.
/// This scanner strips .exe from all profile names before matching.
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

        // Single ps call — gets PID, PPID, and command name for all processes
        var tree = BuildProcessTree();

        var runningNames = new HashSet<string>(
            tree.Names.Values, StringComparer.OrdinalIgnoreCase);

        foreach (var rule in profile.Processes)
        {
            // 1. Primary process name — strip .exe for macOS
            found.Add(StripExe(rule.Name));

            // 2. Scan patterns — match against running process names
            foreach (var pattern in rule.ScanPatterns)
            {
                var strippedPattern = StripExe(pattern);
                found.Add(strippedPattern);

                var regex = BuildPatternRegex(strippedPattern);
                try
                {
                    foreach (var name in runningNames)
                    {
                        if (regex.IsMatch(name))
                            found.Add(name);
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // B3-1: skip a pathological pattern (fail-safe) instead of
                    // wedging the scan. See ProcessScanner for the rationale.
                }
            }

            // 3. Include children — walk pre-built tree
            if (rule.IncludeChildren)
            {
                var strippedName = StripExe(rule.Name);
                var parentPids = tree.Names
                    .Where(kv => string.Equals(kv.Value, strippedName, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key);

                foreach (var pid in parentPids)
                {
                    foreach (var child in GetDescendantNames(pid, tree))
                        found.Add(StripExe(child));
                }
            }
        }

        var result = new ScanResult
        {
            ProcessNames = found.ToList(),
            ScannedAt = DateTime.Now
        };

        _logger.Information("[MacProcessScanner] Resolved {Count} process names for profile '{Profile}'",
            result.ProcessNames.Count, profile.Name);

        return result;
    }

    // ─── Private ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Run `ps -eo pid,ppid,comm` ONCE and build pid→name + parent→children maps.
    /// </summary>
    private ProcessTree BuildProcessTree()
    {
        var tree = new ProcessTree();
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
            if (proc == null) return tree;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            foreach (var line in output.Split('\n').Skip(1))
            {
                var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                if (!int.TryParse(parts[0], out var pid)) continue;
                if (!int.TryParse(parts[1], out var ppid)) continue;

                var comm = Path.GetFileName(parts[2]);
                tree.Names[pid] = comm;

                if (!tree.Children.TryGetValue(ppid, out var list))
                {
                    list = new List<int>();
                    tree.Children[ppid] = list;
                }
                list.Add(pid);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[MacProcessScanner] Failed to build process tree");
        }

        return tree;
    }

    private static List<string> GetDescendantNames(int parentId, ProcessTree tree)
    {
        var names = new List<string>();
        if (!tree.Children.TryGetValue(parentId, out var children))
            return names;

        foreach (var childPid in children)
        {
            if (tree.Names.TryGetValue(childPid, out var name))
                names.Add(name);
            names.AddRange(GetDescendantNames(childPid, tree));
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

            // B3-1: bounded match time — see ProcessScanner.PatternMatchTimeoutMs
            // for the full rationale (untrusted scan_patterns + catastrophic
            // backtracking on hot paths). Mirror the 250ms ceiling here.
            return new Regex(regexPattern,
                RegexOptions.IgnoreCase | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(250));
        });
    }

    private static string StripExe(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }

    private class ProcessTree
    {
        public Dictionary<int, List<int>> Children { get; } = new();
        public Dictionary<int, string> Names { get; } = new();
    }
}
#endif
