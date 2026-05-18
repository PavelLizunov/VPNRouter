#nullable enable
using System.Net.Http;
using Serilog;

namespace VPNRouter.Core.Services;


/// <summary>
/// Manages Windows hosts file entries for Discord voice servers.
/// Discord voice servers (finland*.discord.media) may be blocked by IP;
/// redirecting them to a working Cloudflare IP fixes voice connectivity.
/// Source: Flowseal/zapret-discord-youtube .service/hosts
///
/// <para>
/// Phase 2D (v3.0) refactored to take <see cref="IFileSystem"/> via ctor
/// for testability while keeping the existing static call sites
/// untouched via the <see cref="DefaultInstance"/> singleton.
/// </para>
/// </summary>
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

    // 3G-2 (v3.0 refactor): replaced the per-class `static readonly HttpClient`
    // with the shared IHttpClient seam — consolidated retry policy, shared
    // DNS-refresh pool (PolicyHttpClient.Shared), test-injectable.
    private readonly IFileSystem _fs;
    private readonly string _hostsPath;
    private readonly IHttpClient _http;

    /// <summary>
    /// Default singleton wired to <see cref="RealFileSystem"/> and the
    /// production hosts path. Used by the static facade methods so that
    /// existing call sites continue to work without modification.
    /// </summary>
    private static readonly HostsManager DefaultInstance = new(new RealFileSystem(), HostsPath);

    /// <summary>
    /// Construct a <see cref="HostsManager"/> backed by the supplied
    /// <see cref="IFileSystem"/>. Tests use this with
    /// <c>InMemoryFileSystem</c>; production code typically uses the
    /// static facade methods which dispatch to <see cref="DefaultInstance"/>.
    /// </summary>
    /// <param name="http">3G-2: HTTP seam. Defaults to <see cref="PolicyHttpClient.Shared"/>;
    /// tests inject <c>FakeHttpClient</c> to stub the Flowseal fetch.</param>
    public HostsManager(IFileSystem? fileSystem = null, string? hostsPath = null, IHttpClient? http = null)
    {
        _fs = fileSystem ?? new RealFileSystem();
        _hostsPath = hostsPath ?? HostsPath;
        _http = http ?? PolicyHttpClient.Shared;
    }

    /// <summary>Instance variant of <see cref="IsInstalled"/>.</summary>
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

    /// <summary>Instance variant of <see cref="Install"/>.</summary>
    public (bool success, string message) InstallInstance(ILogger? logger = null)
    {
        try
        {
            if (IsInstalledInstance())
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

    /// <summary>Instance variant of <see cref="Uninstall"/>.</summary>
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

    /// <summary>Instance variant of <see cref="IsFlowsealInstalled"/>.</summary>
    public bool IsFlowsealInstalledInstance()
    {
        try
        {
            if (!_fs.FileExists(_hostsPath)) return false;
            return _fs.ReadAllText(_hostsPath).Contains(FlowsealMarkerStart);
        }
        catch { return false; }
    }

    /// <summary>Instance variant of <see cref="InstallFlowsealAsync"/>.</summary>
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

            // Strip comments and blank lines from downloaded content
            var hostLines = raw.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))
                .ToList();

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

    /// <summary>Instance variant of <see cref="UninstallFlowseal"/>.</summary>
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

    /// <summary>
    /// Remove every line between <paramref name="markerStart"/> and
    /// <paramref name="markerEnd"/> (inclusive), plus trailing whitespace
    /// left by the removed block. Public-instance helper extracted from
    /// the duplicate Discord/Flowseal uninstall paths.
    /// </summary>
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

        // Remove trailing empty lines left by our block
        while (newLines.Count > 0 && string.IsNullOrWhiteSpace(newLines[^1]))
            newLines.RemoveAt(newLines.Count - 1);

        return newLines;
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

    // ── Static facade (backwards compatibility) ──

    /// <summary>
    /// Check if Discord hosts entries are currently installed.
    /// </summary>
    public static bool IsInstalled() => DefaultInstance.IsInstalledInstance();

    /// <summary>
    /// Add Discord voice server entries to the hosts file.
    /// Maps finland10000-10199.discord.media to Cloudflare IP.
    /// Requires administrator privileges.
    /// </summary>
    public static (bool success, string message) Install(ILogger? logger = null)
        => DefaultInstance.InstallInstance(logger);

    /// <summary>
    /// Remove Discord voice server entries from the hosts file.
    /// Removes everything between the VPNRouter markers.
    /// </summary>
    public static (bool success, string message) Uninstall(ILogger? logger = null)
        => DefaultInstance.UninstallInstance(logger);

    public static bool IsFlowsealInstalled() => DefaultInstance.IsFlowsealInstalledInstance();

    public static Task<(bool success, string message)> InstallFlowsealAsync(ILogger? logger = null)
        => DefaultInstance.InstallFlowsealInstanceAsync(logger);

    public static (bool success, string message) UninstallFlowseal(ILogger? logger = null)
        => DefaultInstance.UninstallFlowsealInstance(logger);
}
