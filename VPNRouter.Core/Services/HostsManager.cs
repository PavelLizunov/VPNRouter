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

    // The native Discord block maps every finland*.discord.media host. The
    // upstream Flowseal hosts file bundles those SAME entries, so with both
    // features enabled the file gains ~200 redundant lines. We treat the
    // native Discord block as the canonical owner of *.discord.media and keep
    // the Flowseal block free of those hosts (see InstallInstance /
    // InstallFlowsealInstanceAsync / ReconcileDiscordDuplicatesInstance).
    private const string DiscordMediaSuffix = ".discord.media";

    /// <summary>
    /// GitHub hosts that VPNRouter's own auto-updater resolves on the
    /// release-download path. The upstream Flowseal hosts blob pins
    /// <c>release-assets.githubusercontent.com</c> — the 302 target of every
    /// <c>github.com/.../releases/download/...</c> asset URL — to a single
    /// hardcoded GitHub-Pages Fastly anycast IP (185.199.x). We MUST NOT carry
    /// that pin into our own block: it buys the update flow nothing (the
    /// release list is fetched from the un-pinned <c>api.github.com</c>, so
    /// whenever an update is even visible natural GitHub DNS already resolves)
    /// while adding a SILENT failure path. A hardcoded IP that later rotates,
    /// gets censor-null-routed (the Flowseal audience is on DPI/censored
    /// networks by definition), or lands on a Fastly POP that doesn't front
    /// release assets would strand the user on an old build with no visible
    /// error and no future fixes. The Discord/Telegram DPI-bypass entries (the
    /// actual reason we install Flowseal hosts) and <c>raw.githubusercontent.com</c>
    /// (recoverable failure + genuine DPI-bypass value for geo/free-config
    /// fetches) are deliberately kept.
    /// Found from a real problem-user's hosts file 2026-06-11.
    /// </summary>
    private static readonly HashSet<string> UpdatePathGitHubHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "release-assets.githubusercontent.com", // current release-asset 302 target (pinned by Flowseal today)
            "objects.githubusercontent.com",        // historical / alternate release-asset 302 target
            "objects-origin.githubusercontent.com", // alternate origin host (GitHub release CSP)
            "github.com",                           // initial /releases/download/... 302 source
            "api.github.com",                       // release-list endpoint (UpdateChecker / GitHubReleaseSource)
            "codeload.github.com",                  // git archive / tarball downloads
        };

    // 3G-2 (v3.0 refactor): replaced the per-class `static readonly HttpClient`
    // with the shared IHttpClient seam — consolidated retry policy, shared
    // DNS-refresh pool (PolicyHttpClient.Shared), test-injectable.
    private readonly IFileSystem _fs;
    private readonly string _hostsPath;
    private readonly IHttpClient _http;
    private readonly IProcessRunner _runner;

    // Phase 3+ (2026-05-21) IProcessRunner adoption: shared seam for the
    // FlushDns helper. Instance methods route through _runner so tests can
    // inject a per-instance fake via the ctor without touching global
    // state, but the default still flows from here. Mirrors the pattern in
    // FirewallManager / ZapretActions.
    /// <summary>Test-only seam: swap in a fake. Production paths use the
    /// default <see cref="ProcessRunner"/>. Not thread-safe — assumes serial
    /// xUnit execution within the fixture; tests reset in try/finally.</summary>
    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

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
    /// <param name="runner">Phase 3+ (2026-05-21): IProcessRunner seam for the
    /// FlushDns helper. Defaults to a new <see cref="ProcessRunner"/>; tests
    /// inject <c>FakeProcessRunner</c> to stub the ipconfig invocation.</param>
    public HostsManager(IFileSystem? fileSystem = null, string? hostsPath = null, IHttpClient? http = null, IProcessRunner? runner = null)
    {
        _fs = fileSystem ?? new RealFileSystem();
        _hostsPath = hostsPath ?? HostsPath;
        _http = http ?? PolicyHttpClient.Shared;
        _runner = runner ?? Runner;
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

            // Dedup (reverse install order — Flowseal added first): if a
            // Flowseal block already bundles the finland*.discord.media
            // entries, strip them now so the native block we're about to
            // append becomes the single owner instead of a 200-line dup. The
            // forward order (Discord first) is handled symmetrically in
            // InstallFlowsealInstanceAsync.
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

            // Dedup: the upstream Flowseal hosts bundle the same
            // finland*.discord.media voice entries our native Discord block
            // writes. If that block is already installed it's the canonical
            // owner — drop those lines here so enabling both features doesn't
            // append ~200 duplicate lines. Discord-only / Flowseal-only keep
            // their entries untouched.
            if (IsInstalledInstance())
            {
                var before = hostLines.Count;
                hostLines = hostLines.Where(l => !IsDiscordMediaHostLine(l)).ToList();
                var skipped = before - hostLines.Count;
                if (skipped > 0)
                    logger?.Information("[Hosts] Flowseal: skipped {Count} *.discord.media line(s) already provided by the Discord block", skipped);
            }

            // Never carry GitHub release-download host pins into our own block —
            // a hardcoded Fastly IP can silently break VPNRouter's own auto-updater.
            // See UpdatePathGitHubHosts.
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

    /// <summary>
    /// True when <paramref name="line"/> is a hosts mapping
    /// ("IP host [host…]") whose hostname is under <c>discord.media</c>
    /// (e.g. <c>finland10000.discord.media</c>). Comments, blank lines, and
    /// IP-only lines return false. Drives the Discord/Flowseal dedup: the
    /// native Discord block is the canonical owner of these hosts, so any
    /// copy in the Flowseal block is redundant.
    /// </summary>
    internal static bool IsDiscordMediaHostLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("#", StringComparison.Ordinal)) return false;

        // tokens[0] = IP address; tokens[1..] = one or more hostnames.
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

    /// <summary>
    /// Drop any hosts-file line that pins a host on VPNRouter's own
    /// auto-update / release-download path (see <see cref="UpdatePathGitHubHosts"/>).
    /// Input is the comment-/blank-stripped Flowseal host lines
    /// (<c>"IP host [host…]"</c>). A line that pins ONLY update-path hosts is
    /// removed entirely; a line that also carries other hostnames keeps the
    /// survivors (defensive — Flowseal ships one host per line today, but
    /// multi-host lines are valid hosts syntax). Lines we can't parse as
    /// <c>IP host</c> pass through untouched. Matching is exact hostname
    /// (trailing-dot tolerant), case-insensitive — so the kept
    /// <c>raw.githubusercontent.com</c> is never caught by a substring slip.
    /// </summary>
    internal static List<string> StripUpdatePathGitHubPins(IEnumerable<string> hostLines)
    {
        var result = new List<string>();
        foreach (var line in hostLines)
        {
            // tokens[0] = IP address; tokens[1..] = one or more hostnames.
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                result.Add(line); // not an "IP host" pin — leave exactly as-is
                continue;
            }

            var keptHosts = tokens.Skip(1)
                .Where(h => !UpdatePathGitHubHosts.Contains(h.TrimEnd('.')))
                .ToList();

            if (keptHosts.Count == tokens.Length - 1)
                result.Add(line);                                          // nothing stripped — preserve verbatim
            else if (keptHosts.Count > 0)
                result.Add($"{tokens[0]} {string.Join(' ', keptHosts)}");  // survivors only
            // else: every hostname was update-path-critical → drop the line
        }
        return result;
    }

    /// <summary>
    /// Return a copy of <paramref name="allLines"/> with every line INSIDE the
    /// block delimited by <paramref name="markerStart"/>/<paramref name="markerEnd"/>
    /// that matches <paramref name="predicate"/> removed. The marker lines and
    /// everything outside the block are preserved verbatim so the START/END
    /// markers still round-trip for a later <see cref="StripBlock"/> uninstall.
    /// Returns the new list plus the count of removed lines.
    /// </summary>
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

    /// <summary>
    /// Strip *.discord.media lines from an already-present Flowseal block so
    /// the native Discord block stays the single owner. No-op when no Flowseal
    /// block exists or it carries no discord.media hosts. Does not flush DNS —
    /// callers append/flush afterwards.
    /// </summary>
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

    /// <summary>Instance variant of <see cref="ReconcileDiscordDuplicates"/>.</summary>
    public (bool changed, string message) ReconcileDiscordDuplicatesInstance(ILogger? logger = null)
    {
        try
        {
            // Duplication is only possible when BOTH blocks are present.
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

    /// <summary>
    /// Flush the Windows DNS cache via <c>ipconfig /flushdns</c>. Called after
    /// every hosts-file mutation so the new entries become effective without
    /// requiring a reboot.
    ///
    /// <para>Phase 3+ (2026-05-21) IProcessRunner adoption: routed through
    /// <see cref="_runner"/>. Wire shape preserved — same single
    /// <c>/flushdns</c> argument, same 5s timeout. Pre-Phase-3+ the
    /// <c>using var proc = Process.Start(psi); proc?.WaitForExit(5000)</c>
    /// pattern was used to avoid leaking native process handles on timeout
    /// (v2.20.2). The IProcessRunner implementation owns its <c>Process</c>
    /// via <c>using</c> inside <c>ProcessRunner.RunAsync</c>, so the leak
    /// mitigation transfers automatically.</para>
    /// </summary>
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

    /// <summary>
    /// Self-heal existing hosts files that were written by older builds, where
    /// the native Discord block and the Flowseal block both carried the same
    /// finland*.discord.media entries (~200 duplicate lines). Strips the
    /// duplicates out of the Flowseal block, keeping the native Discord block
    /// as the canonical owner. No-op once deduped or when either block is
    /// absent. Requires administrator (writes the hosts file) — fails soft.
    /// </summary>
    public static (bool changed, string message) ReconcileDiscordDuplicates(ILogger? logger = null)
        => DefaultInstance.ReconcileDiscordDuplicatesInstance(logger);
}
