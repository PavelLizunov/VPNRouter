using System.Net.Http;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Manages Windows hosts file entries for Discord voice servers.
/// Discord voice servers (finland*.discord.media) may be blocked by IP;
/// redirecting them to a working Cloudflare IP fixes voice connectivity.
/// Source: Flowseal/zapret-discord-youtube .service/hosts
/// </summary>
public static class HostsManager
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

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Check if Discord hosts entries are currently installed.
    /// </summary>
    public static bool IsInstalled()
    {
        try
        {
            if (!File.Exists(HostsPath)) return false;
            var content = File.ReadAllText(HostsPath);
            return content.Contains(MarkerStart);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Add Discord voice server entries to the hosts file.
    /// Maps finland10000-10199.discord.media to Cloudflare IP.
    /// Requires administrator privileges.
    /// </summary>
    public static (bool success, string message) Install(ILogger? logger = null)
    {
        try
        {
            if (IsInstalled())
            {
                logger?.Information("[Hosts] Discord entries already installed");
                return (true, "Already installed");
            }

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

            File.AppendAllLines(HostsPath, lines);
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

    /// <summary>
    /// Remove Discord voice server entries from the hosts file.
    /// Removes everything between the VPNRouter markers.
    /// </summary>
    public static (bool success, string message) Uninstall(ILogger? logger = null)
    {
        try
        {
            if (!IsInstalled())
            {
                logger?.Information("[Hosts] Discord entries not found, nothing to remove");
                return (true, "Not installed");
            }

            var allLines = File.ReadAllLines(HostsPath).ToList();
            var newLines = new List<string>();
            bool skipping = false;

            foreach (var line in allLines)
            {
                if (line.TrimEnd() == MarkerStart)
                {
                    skipping = true;
                    continue;
                }
                if (line.TrimEnd() == MarkerEnd)
                {
                    skipping = false;
                    continue;
                }
                if (!skipping)
                    newLines.Add(line);
            }

            // Remove trailing empty lines left by our block
            while (newLines.Count > 0 && string.IsNullOrWhiteSpace(newLines[^1]))
                newLines.RemoveAt(newLines.Count - 1);

            File.WriteAllLines(HostsPath, newLines);
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

    // ── Flowseal hosts (from zapret-discord-youtube .service/hosts) ──

    public static bool IsFlowsealInstalled()
    {
        try
        {
            if (!File.Exists(HostsPath)) return false;
            return File.ReadAllText(HostsPath).Contains(FlowsealMarkerStart);
        }
        catch { return false; }
    }

    public static async Task<(bool success, string message)> InstallFlowsealAsync(ILogger? logger = null)
    {
        try
        {
            if (IsFlowsealInstalled())
                return (true, "Already installed");

            var raw = await _http.GetStringAsync(FlowsealHostsUrl);
            if (string.IsNullOrWhiteSpace(raw))
                return (false, "Empty response from Flowseal hosts URL");

            // Strip comments and blank lines from downloaded content
            var hostLines = raw.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))
                .ToList();

            var block = new List<string> { "", FlowsealMarkerStart };
            block.AddRange(hostLines);
            block.Add(FlowsealMarkerEnd);

            File.AppendAllLines(HostsPath, block);
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

    public static (bool success, string message) UninstallFlowseal(ILogger? logger = null)
    {
        try
        {
            if (!IsFlowsealInstalled())
                return (true, "Not installed");

            var allLines = File.ReadAllLines(HostsPath).ToList();
            var newLines = new List<string>();
            bool skipping = false;

            foreach (var line in allLines)
            {
                if (line.TrimEnd() == FlowsealMarkerStart) { skipping = true; continue; }
                if (line.TrimEnd() == FlowsealMarkerEnd) { skipping = false; continue; }
                if (!skipping) newLines.Add(line);
            }
            while (newLines.Count > 0 && string.IsNullOrWhiteSpace(newLines[^1]))
                newLines.RemoveAt(newLines.Count - 1);

            File.WriteAllLines(HostsPath, newLines);
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

    private static void FlushDns(ILogger? logger)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/flushdns",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            // v2.20.2: wrap in using — without it, if WaitForExit times out
            // (5 s) the native Process handle leaks. Over a long-running
            // session (especially on Windows where ipconfig is called on
            // every hosts mutation) the handle table grows unbounded.
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
            logger?.Debug("[Hosts] DNS cache flushed");
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[Hosts] Failed to flush DNS");
        }
    }
}
