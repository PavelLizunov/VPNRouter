#nullable enable
using System.Net.Http;
using Serilog;

namespace VPNRouter.Core.Services;


public sealed class HostsManager
{
    private const string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";
    private const string MarkerStart = "# === VPNRouter Discord hosts START ===";
    private const string MarkerEnd = "# === VPNRouter Discord hosts END ===";
    private const string DiscordIp = "104.25.158.178";
    private const string DiscordDomain = "discord.media";
    private const int FinlandStart = 10000;
    private const int FinlandEnd = 10199;

    private const string FlowsealMarkerStart = "# === VPNRouter Flowseal hosts START ===";
    private const string FlowsealMarkerEnd = "# === VPNRouter Flowseal hosts END ===";
    private const string FlowsealHostsUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/hosts";

    private const string DiscordMediaSuffix = ".discord.media";

    private static readonly HashSet<string> UpdatePathGitHubHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "release-assets.githubusercontent.com",
            "objects.githubusercontent.com",
            "objects-origin.githubusercontent.com",
            "github.com",
            "api.github.com",
            "codeload.github.com",
        };

    private readonly IFileSystem _fs;
    private readonly string _hostsPath;
    private readonly IHttpClient _http;
    private readonly IProcessRunner _runner;

    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

    private static readonly HostsManager DefaultInstance = new(new RealFileSystem(), HostsPath);

    public HostsManager(IFileSystem? fileSystem = null, string? hostsPath = null, IHttpClient? http = null, IProcessRunner? runner = null)
    {
        _fs = fileSystem ?? new RealFileSystem();
        _hostsPath = hostsPath ?? HostsPath;
        _http = http ?? PolicyHttpClient.Shared;
        _runner = runner ?? Runner;
    }

    public bool IsInstalledInstance()
    {
        try
        {
            if (!_fs.FileExists(_hostsPath)) return false;
            var content = _fs.ReadAllText(_hostsPath);
            return content.Contains(MarkerStart);
        }
        catch
        {
            return false;
        }
    }

    public (bool success, string message) InstallInstance(ILogger? logger = null)
    {
        try
        {
            if (IsInstalledInstance())
            {
                logger?.Information("[Hosts] Discord entries already installed");
                return (true, "Already installed");
            }

            PruneFlowsealDiscordMediaDuplicates(logger);

            var lines = new List<string>
            {
                "",
                MarkerStart
            };

            for (int i = FinlandStart; i <= FinlandEnd; i++)
            {
                lines.Add($"{DiscordIp} finland{i}.{DiscordDomain}");
            }
            lines.Add(MarkerEnd);

            _fs.AppendAllLines(_hostsPath, lines);
            FlushDns(logger);

            logger?.Information("[Hosts] Installed {Count} Discord voice entries", FinlandEnd - FinlandStart + 1);
            return (true, $"Added {FinlandEnd - FinlandStart + 1} Discord voice entries");
        }
        catch (UnauthorizedAccessException)
        {
            logger?.Error("[Hosts] Access denied — run as administrator");
            return (false, "Access denied — run as administrator");
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "[Hosts] Failed to install entries");
            return (false, $"Error: {ex.Message}");
        }
    }

    public (bool success, string message) UninstallInstance(ILogger? logger = null)
    {
        try
        {
            if (!IsInstalledInstance())
            {
                logger?.Information("[Hosts] Discord entries not found, nothing to remove");
                return (true, "Not installed");
            }

            var allLines = _fs.ReadAllLines(_hostsPath).ToList();
            var newLines = StripBlock(allLines, MarkerStart, MarkerEnd);

            _fs.WriteAllLines(_hostsPath, newLines);
            FlushDns(logger);

            logger?.Information("[Hosts] Removed Discord voice entries");
            return (true, "Removed Discord voice entries");
        }
        catch (UnauthorizedAccessException)
        {
            logger?.Error("[Hosts] Access denied — run as administrator");
            return (false, "Access denied — run as administrator");
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "[Hosts] Failed to remove entries");
            return (false, $"Error: {ex.Message}");
        }
    }

    public bool IsFlowsealInstalledInstance()
    {
        try
        {
            if (!_fs.FileExists(_hostsPath)) return false;
            return _fs.ReadAllText(_hostsPath).Contains(FlowsealMarkerStart);
        }
        catch { return false; }
    }

    public async Task<(bool success, string message)> InstallFlowsealInstanceAsync(ILogger? logger = null)
    {
        try
        {
            if (IsFlowsealInstalledInstance())
                return (true, "Already installed");

            var rawResponse = await _http.SendAsync(
                new HttpRequest(HttpMethod.Get, new Uri(FlowsealHostsUrl)));
            if (!rawResponse.IsSuccess())
                return (false, $"Failed to fetch Flowseal hosts: HTTP {rawResponse.StatusCode}");
            var raw = rawResponse.AsString();
            if (string.IsNullOrWhiteSpace(raw))
                return (false, "Empty response from Flowseal hosts URL");

            var hostLines = raw.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))
                .ToList();

            if (IsInstalledInstance())
            {
                var before = hostLines.Count;
                hostLines = hostLines.Where(l => !IsDiscordMediaHostLine(l)).ToList();
                var skipped = before - hostLines.Count;
                if (skipped > 0)
                    logger?.Information("[Hosts] Flowseal: skipped {Count} *.discord.media line(s) already provided by the Discord block", skipped);
            }

            var beforePinStrip = hostLines.Count;
            hostLines = StripUpdatePathGitHubPins(hostLines);
            var pinsStripped = beforePinStrip - hostLines.Count;
            if (pinsStripped > 0)
                logger?.Information(
                    "[Hosts] Flowseal: dropped {Count} GitHub update-path host pin(s) so they can't break auto-update (kept Discord/Telegram + raw.githubusercontent)",
                    pinsStripped);

            var block = new List<string> { "", FlowsealMarkerStart };
            block.AddRange(hostLines);
            block.Add(FlowsealMarkerEnd);

            _fs.AppendAllLines(_hostsPath, block);
            FlushDns(logger);
            logger?.Information("[Hosts] Installed {Count} Flowseal entries", hostLines.Count);
            return (true, $"Added {hostLines.Count} Flowseal entries");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Access denied — run as administrator");
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "[Hosts] InstallFlowseal failed");
            return (false, $"Error: {ex.Message}");
        }
    }

    public (bool success, string message) UninstallFlowsealInstance(ILogger? logger = null)
    {
        try
        {
            if (!IsFlowsealInstalledInstance())
                return (true, "Not installed");

            var allLines = _fs.ReadAllLines(_hostsPath).ToList();
            var newLines = StripBlock(allLines, FlowsealMarkerStart, FlowsealMarkerEnd);

            _fs.WriteAllLines(_hostsPath, newLines);
            FlushDns(logger);
            logger?.Information("[Hosts] Removed Flowseal entries");
            return (true, "Removed Flowseal entries");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Access denied — run as administrator");
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "[Hosts] UninstallFlowseal failed");
            return (false, $"Error: {ex.Message}");
        }
    }

    internal static List<string> StripBlock(List<string> allLines, string markerStart, string markerEnd)
    {
        var newLines = new List<string>();
        bool skipping = false;

        foreach (var line in allLines)
        {
            if (line.TrimEnd() == markerStart)
            {
                skipping = true;
                continue;
            }
            if (line.TrimEnd() == markerEnd)
            {
                skipping = false;
                continue;
            }
            if (!skipping)
                newLines.Add(line);
        }

        while (newLines.Count > 0 && string.IsNullOrWhiteSpace(newLines[^1]))
            newLines.RemoveAt(newLines.Count - 1);

        return newLines;
    }

    internal static bool IsDiscordMediaHostLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("#", StringComparison.Ordinal)) return false;

        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < tokens.Length; i++)
        {
            var host = tokens[i].TrimEnd('.');
            if (host.Equals("discord.media", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(DiscordMediaSuffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static List<string> StripUpdatePathGitHubPins(IEnumerable<string> hostLines)
    {
        var result = new List<string>();
        foreach (var line in hostLines)
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                result.Add(line);
                continue;
            }

            var keptHosts = tokens.Skip(1)
                .Where(h => !UpdatePathGitHubHosts.Contains(h.TrimEnd('.')))
                .ToList();

            if (keptHosts.Count == tokens.Length - 1)
                result.Add(line);
            else if (keptHosts.Count > 0)
                result.Add($"{tokens[0]} {string.Join(' ', keptHosts)}");
        }
        return result;
    }

    internal static (List<string> lines, int removed) PruneBlockLines(
        List<string> allLines, string markerStart, string markerEnd, Func<string, bool> predicate)
    {
        var result = new List<string>(allLines.Count);
        bool inBlock = false;
        int removed = 0;

        foreach (var line in allLines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed == markerStart) { inBlock = true; result.Add(line); continue; }
            if (trimmed == markerEnd) { inBlock = false; result.Add(line); continue; }
            if (inBlock && predicate(line)) { removed++; continue; }
            result.Add(line);
        }

        return (result, removed);
    }

    private void PruneFlowsealDiscordMediaDuplicates(ILogger? logger)
    {
        if (!IsFlowsealInstalledInstance()) return;

        var allLines = _fs.ReadAllLines(_hostsPath).ToList();
        var (pruned, removed) = PruneBlockLines(
            allLines, FlowsealMarkerStart, FlowsealMarkerEnd, IsDiscordMediaHostLine);
        if (removed > 0)
        {
            _fs.WriteAllLines(_hostsPath, pruned);
            logger?.Information(
                "[Hosts] Stripped {Count} *.discord.media line(s) from existing Flowseal block (Discord block is canonical owner)",
                removed);
        }
    }

    public (bool changed, string message) ReconcileDiscordDuplicatesInstance(ILogger? logger = null)
    {
        try
        {
            if (!IsInstalledInstance() || !IsFlowsealInstalledInstance())
                return (false, "Nothing to reconcile");

            var allLines = _fs.ReadAllLines(_hostsPath).ToList();
            var (pruned, removed) = PruneBlockLines(
                allLines, FlowsealMarkerStart, FlowsealMarkerEnd, IsDiscordMediaHostLine);
            if (removed == 0)
                return (false, "No duplicates");

            _fs.WriteAllLines(_hostsPath, pruned);
            FlushDns(logger);
            logger?.Information(
                "[Hosts] Reconciled {Count} duplicate *.discord.media line(s) out of the Flowseal block",
                removed);
            return (true, $"Removed {removed} duplicate Discord entries");
        }
        catch (UnauthorizedAccessException)
        {
            logger?.Warning("[Hosts] Reconcile: access denied — run as administrator");
            return (false, "Access denied — run as administrator");
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[Hosts] ReconcileDiscordDuplicates failed");
            return (false, $"Error: {ex.Message}");
        }
    }

    private void FlushDns(ILogger? logger)
    {
        try
        {
            var result = _runner.RunAsync(new ProcessRequest(
                ExecutablePath: "ipconfig",
                Arguments: new[] { "/flushdns" },
                Timeout: TimeSpan.FromMilliseconds(5000))).GetAwaiter().GetResult();

            if (result.TimedOut)
            {
                logger?.Warning("[Hosts] ipconfig /flushdns timed out after 5s");
                return;
            }
            logger?.Debug("[Hosts] DNS cache flushed");
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[Hosts] Failed to flush DNS");
        }
    }

    public static bool IsInstalled() => DefaultInstance.IsInstalledInstance();

    public static (bool success, string message) Install(ILogger? logger = null)
        => DefaultInstance.InstallInstance(logger);

    public static (bool success, string message) Uninstall(ILogger? logger = null)
        => DefaultInstance.UninstallInstance(logger);

    public static bool IsFlowsealInstalled() => DefaultInstance.IsFlowsealInstalledInstance();

    public static Task<(bool success, string message)> InstallFlowsealAsync(ILogger? logger = null)
        => DefaultInstance.InstallFlowsealInstanceAsync(logger);

    public static (bool success, string message) UninstallFlowseal(ILogger? logger = null)
        => DefaultInstance.UninstallFlowsealInstance(logger);

    public static (bool changed, string message) ReconcileDiscordDuplicates(ILogger? logger = null)
        => DefaultInstance.ReconcileDiscordDuplicatesInstance(logger);
}
