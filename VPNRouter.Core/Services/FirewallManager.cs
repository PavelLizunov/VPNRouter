using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Serilog;
using VPNRouter.Core.Interfaces;

namespace VPNRouter.Core.Services;

/// <summary>
/// Manages Windows Firewall rules for block_on_vpn_fail.
///
/// Lifecycle:
/// 1. CreateBlockRules() — creates DISABLED outbound block rules at VPN start
/// 2. Rules stay DISABLED while VPN is running (sing-box TUN handles routing)
/// 3. EnableBlockRules() — called by HealthMonitor when sing-box crashes
///    (prevents traffic from leaking direct while VPN is down)
/// 4. DisableBlockRules() — called when sing-box successfully restarts
/// 5. DeleteAllRules() — called on clean shutdown
///
/// Key insight: while sing-box is running, TUN captures all targeted traffic
/// and routes it through proxy. Firewall rules are NOT needed during normal
/// operation. They are a safety net for the brief window when sing-box dies
/// and TUN is gone — without them, traffic would go direct.
/// </summary>
public class FirewallManager : IFirewallManager
{
    private const string RulePrefix = "VPNRouter_Block_";

    // v2.37.0-r7 — extracted timeout magic number per Phase 1 quality pass.
    // Named constant makes the policy intent (how long before assuming hung)
    // reviewable in one place. (#7 cleanup 2026-07-10: the where.exe timeout
    // moved to ProcessImagePath alongside the shared resolver.)
    //
    // Rationale: `netsh.exe` interacts with the Windows Filtering Platform and
    // can take longer on first invocation after boot; 5 s covers the
    // advfirewall service cold-start without making rule edits feel unresponsive.
    private const int NetshTimeoutMs = 5000;

    // ─── Wave 39 (2026-05-19): DNS leak lockdown rule names ──────────────────
    //
    // 4 firewall rules that block outbound DNS-port traffic on every adapter
    // EXCEPT loopback. Sing-box's DNS path goes via VLESS outbound (DoH on
    // port 443) so these blocks don't affect VPN-side DNS; they only kill
    // queries the Windows DNS Client tries to race in parallel via the
    // ethernet adapter directly to ISP resolvers.
    //
    // The Allow rule is prefixed with `0_` so it sorts FIRST lexically. While
    // Windows Firewall evaluation is action+specificity-based rather than
    // pure-name-order, the `0_` prefix is a defensive belt-and-braces against
    // future netsh internals changes and stays readable in the Windows
    // Firewall UI (the user sees the allow rule above the blocks).
    internal const string DnsLockdownAllowRule = "0_VPNRouter-DnsLockdown-LoopbackAllow";
    // BR-8 (brat 2026-05-20) — second allow rule whitelisting the TUN
    // adapter's own DNS endpoint (sing-box listens on 172.19.0.2:53 by
    // default). Pre-r12 the unscoped block rule below banned ALL UDP/53
    // outbound including TUN-bound queries, so DNS through sing-box was
    // broken whenever the user had Wave 39 enabled. The leading "0_"
    // prefix sorts it to the top of the Windows Firewall UI list so the
    // ordering matches the install order (allows first, blocks after).
    internal const string DnsLockdownTunAllowRule = "0_VPNRouter-DnsLockdown-TunAllow";
    internal const string DnsLockdownUdp53Rule = "VPNRouter-DnsLockdown-UDP53";
    internal const string DnsLockdownTcp53Rule = "VPNRouter-DnsLockdown-TCP53";
    internal const string DnsLockdownTcp853Rule = "VPNRouter-DnsLockdown-TCP853";

    // v2.40.0-r10 #6 (core-audit IPv6 leak): the four rules above are
    // IPv4-only (their remoteip math operates on 0.0.0.0-255.255.255.255).
    // On a dual-stack machine the Windows DNS Client races queries over IPv6
    // to public resolvers (Cloudflare 2606:4700:4700::1111, Google
    // 2001:4860:4860::8888, the ISP's IPv6 resolver) — those leak straight
    // past the v4 blocks. These parallel rules close that path.
    //
    // Why this is SAFE despite DnsLeakLockdown's internet-breakage history
    // (brat r10/r16): that breakage was the UNSCOPED IPv4 block overriding
    // the TUN allow ("Block wins over Allow"). That failure mode cannot
    // recur on IPv6 because (a) our shipping TUN is IPv4-only — there is no
    // IPv6 TUN range to accidentally clobber — and (b) sing-box's own DNS
    // goes out as DoH on 443 via the VLESS outbound, never raw 53/853.
    //
    // Scope = 2000::/3 (global-unicast / public IPv6) ONLY. This is where
    // every public/ISP resolver lives, so it closes the dominant leak while
    // deliberately leaving ::1 (loopback), fe80::/10 (link-local — router-
    // advertised DNS), fc00::/7 (ULA) and ff00::/8 (mDNS) untouched. Blocking
    // those is the actual internet-breaking risk and buys little leak
    // protection, so we don't.
    internal const string DnsLockdownUdp53Ipv6Rule = "VPNRouter-DnsLockdown-UDP53-v6";
    internal const string DnsLockdownTcp53Ipv6Rule = "VPNRouter-DnsLockdown-TCP53-v6";
    internal const string DnsLockdownTcp853Ipv6Rule = "VPNRouter-DnsLockdown-TCP853-v6";

    // Public global-unicast IPv6 range — every routable public/ISP DNS
    // resolver address is inside 2000::/3. netsh accepts IPv6 CIDR for
    // remoteip directly. See the v6 rule-name comment above for why we scope
    // to this rather than ::/0.
    private const string Ipv6PublicDnsScope = "2000::/3";

    // Prefixes covered by CleanupOrphanedRules — anything we ever add to the
    // firewall must be enumerable from here so a previous abnormal exit
    // doesn't leave the user's network locked down on next boot.
    private static readonly string[] AllPrefixes =
    {
        RulePrefix,
        "VPNRouter-DnsLockdown-",
        "0_VPNRouter-DnsLockdown-",
    };

    private readonly ILogger _logger;
    private readonly IProcessRunner _runner;
    private readonly List<string> _managedRules = new();
    // v2.40.0-r10 (#1 core-audit HIGH): the FULL set of exact process names requested for
    // block_on_vpn_fail, INCLUDING ones whose exe path couldn't be resolved at connect time
    // (not running / off-PATH — e.g. Discord/Telegram launched AFTER connecting). On crash,
    // EnableBlockRules re-resolves + late-creates their rules so the kill-switch doesn't
    // fail OPEN for exactly the default privacy profiles.
    private List<string> _requestedNames = new();
    private bool _disposed;

    // v2.31.6-r19: netsh.exe writes its output in the OEM code page (CP-866
    // on RU Windows, CP-850 on DE/FR/etc.), but .NET's default redirect
    // assumes the system ANSI page (CP-1251 on RU). The mismatch produced
    // mojibake like "РќРё РѕРґРЅРѕ РїСЂР°РІРёР»Рѕ" in vpnrouter.log every time a
    // firewall rule operation hit a localized warning. Resolve once at type
    // init so each PSI we spawn can pin the right encoding.
    //
    // Phase 3+ (2026-05-21) IProcessRunner adoption: ConsoleEncoding is no
    // longer applied per-call (ProcessRequest has no StandardOutputEncoding
    // surface). The structural block-aware parser in FindRulesByPrefix is
    // already locale-tolerant — the rule-name token is ASCII regardless of
    // display language, and label rows like `Описание:` are filtered out by
    // the blank-line block boundary, not by string match. The OEM encoding
    // remains resolved here so future ProcessRequest extension can pick it
    // back up without re-introducing the kernel32 P/Invoke.
    private static readonly Encoding ConsoleEncoding = ResolveConsoleEncoding();

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static Encoding ResolveConsoleEncoding()
    {
        if (!OperatingSystem.IsWindows()) return Encoding.UTF8;
        try
        {
            // .NET Core / 8 ships only UTF-8/16/32 + ASCII out of the box.
            // CodePagesEncodingProvider unlocks legacy single-byte pages.
            // Idempotent — safe even if another component already registered.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    // Phase 3+ (2026-05-21) IProcessRunner adoption: shared seam for both
    // instance and static helpers. Static helpers
    // (EnableDnsLockdownAsync / DisableDnsLockdownAsync / RunNetshStatic)
    // use this directly. Instance methods route through _runner so tests
    // can inject per-instance fakes via the ctor without touching global
    // state, but the default still flows from here. Mirrors the pattern in
    // ZapretActions / WindowsDnsHardening._runnerOverride.
    /// <summary>Test-only seam: swap in a fake for the static
    /// netsh helpers. Production paths use the default
    /// <see cref="ProcessRunner"/>. Not thread-safe — assumes serial
    /// xUnit execution within the fixture; tests reset in try/finally.</summary>
    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

    public FirewallManager(ILogger? logger = null, IProcessRunner? runner = null)
    {
        _logger = logger ?? Log.Logger;
        _runner = runner ?? Runner;
    }

    /// <summary>
    /// v2.40.0-r10 (#2 + #4 core-audit): best-effort, Windows-guarded sweep of
    /// any orphaned VPNRouter firewall rules left by a previous abnormal exit
    /// (crash, kill, failed update). Constructs a throwaway manager, runs the
    /// prefix-based <see cref="CleanupOrphanedRules"/> (clears both the
    /// block_on_vpn_fail rules AND the DNS-lockdown rules), and swallows every
    /// exception so it is safe to wire into both startup AND
    /// <see cref="AppDomain.ProcessExit"/> without ever throwing into those
    /// paths.
    ///
    /// <para>Before r10 only the GUI front-end swept on startup
    /// (App/Program.cs); the CLI and Windows Service did not, and nothing
    /// swept on exit. A crash on the CLI/Service path — or any front-end
    /// killed before its clean <c>Stop()</c>/<see cref="DeleteAllRules"/> —
    /// could strand the user with kill-switch block rules still enabled and
    /// the internet blocked until the GUI happened to run. This helper closes
    /// both the missing-front-end gap (#4) and the no-exit-cleanup gap (#2).</para>
    /// </summary>
    public static void TryCleanupOrphanedRulesSafe(ILogger? logger = null)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            using var fw = new FirewallManager(logger ?? Log.Logger);
            fw.CleanupOrphanedRules();
        }
        catch
        {
            // Never throw from a startup / process-exit hook.
        }
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Create DISABLED block rules for all processes with block_on_vpn_fail=true.
    /// Rules stay disabled while VPN is running normally.
    /// </summary>
    public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true)
    {
        // isFullTunnel is a Linux/macOS-only signal (their kill-switch is a global
        // egress block that must arm on routing INTENT, not list emptiness).
        // Windows blocks per-process image via netsh, so it is (correctly)
        // ignored here — full-tunnel on Windows still routes everything through
        // the TUN and per-app rules are moot, but we never GLOBAL-block.
        _ = isFullTunnel;
        // v2.31.6-r20: CleanupOrphanedRules deletes ALL prefix-matching
        // rules in Windows Firewall (including ones we previously added to
        // _managedRules). The pre-r20 code never reset _managedRules, so on
        // a second CreateBlockRules call (e.g. after VpnEngine.Apply with a
        // changed profile) we accumulated stale names that no longer
        // existed in netsh. Subsequent EnableBlockRules / DisableBlockRules
        // tried to flip those phantom rules and got "No rules match the
        // specified criteria" warnings — F-LOG-4 in the 2026-05-04 audit.
        CleanupOrphanedRules();
        _managedRules.Clear();

        // netsh does not support wildcards in program paths or rule names —
        // skip patterns; only create rules for exact .exe names
        var exact = processNames
            .Where(n => !n.Contains('*') && !n.Contains('?'))
            .ToList();
        // Remember every requested name (even ones unresolvable right now) so the
        // kill-switch can lazily create their rules on crash — see EnableBlockRules.
        _requestedNames = exact;

        foreach (var name in exact)
        {
            var ruleName = RulePrefix + name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);

            // Resolve full path — skip this process if path not found
            var exePath = ResolveProcessPath(name);
            if (exePath == null)
            {
                _logger.Warning("[Firewall] Skipping rule for {Process} — exe path not found (process not running?)", name);
                continue;
            }

            if (CreateBlockRule(ruleName, exePath, enabled: false))
            {
                _managedRules.Add(ruleName);
            }
            else
            {
                _logger.Warning("[Firewall] Failed to create rule for {Process} — netsh error", name);
            }
        }

        _logger.Information("[Firewall] Created {Count} block rules (disabled — will enable on VPN crash)", _managedRules.Count);
    }

    /// <summary>
    /// Enable block rules — call ONLY when sing-box crashes.
    /// This blocks all direct outbound traffic for targeted processes,
    /// preventing data leaks while VPN is down.
    /// </summary>
    public void EnableBlockRules()
    {
        // v2.40.0-r10 (#1 core-audit HIGH): the kill-switch was failing OPEN for the
        // default privacy profiles. CreateBlockRules (at connect time) skips any process
        // whose exe path can't be resolved — and Discord (%LocalAppData%) / Telegram are
        // off-PATH and launched AFTER connecting, so their block rule was NEVER created
        // and there was nothing to enable on crash → the routed app egressed direct with
        // the real IP. We're now in the VPN-down window and the app is almost certainly
        // running, so re-resolve + late-create (enabled) any requested rule still missing.
        foreach (var name in _requestedNames)
        {
            var ruleName = RulePrefix + name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
            if (_managedRules.Contains(ruleName)) continue; // already created at connect time
            var exePath = ResolveProcessPath(name);
            if (exePath == null)
            {
                _logger.Warning("[Firewall] kill-switch: still cannot resolve {Process} — cannot block its direct egress", name);
                continue;
            }
            if (CreateBlockRule(ruleName, exePath, enabled: true))
            {
                _managedRules.Add(ruleName);
                _logger.Information("[Firewall] kill-switch: late-created + enabled block rule for {Process} (was unresolved at connect time)", name);
            }
        }

        // v2.31.6-r20: count actual successes so the summary log isn't a
        // lie when some rules vanished between create and enable (Group
        // Policy sweep, AV cleanup, manual deletion, etc.)
        var ok = 0;
        foreach (var rule in _managedRules)
        {
            if (RunNetsh($"advfirewall firewall set rule name=\"{rule}\" new enable=yes"))
                ok++;
        }
        if (ok == _managedRules.Count)
            _logger.Information("[Firewall] ENABLED {Count} block rules (VPN down — leak protection active)", ok);
        else
            _logger.Warning("[Firewall] ENABLED {Ok}/{Total} block rules (VPN down — {Missing} missing in firewall)",
                ok, _managedRules.Count, _managedRules.Count - ok);
    }

    /// <summary>
    /// Disable block rules — call when sing-box successfully starts/restarts
    /// or during clean shutdown.
    /// </summary>
    public void DisableBlockRules()
    {
        var ok = 0;
        foreach (var rule in _managedRules)
        {
            if (RunNetsh($"advfirewall firewall set rule name=\"{rule}\" new enable=no"))
                ok++;
        }
        if (ok == _managedRules.Count)
            _logger.Information("[Firewall] Disabled {Count} block rules (VPN up — TUN handles routing)", ok);
        else
            _logger.Warning("[Firewall] Disabled {Ok}/{Total} block rules (VPN up — {Missing} missing in firewall)",
                ok, _managedRules.Count, _managedRules.Count - ok);
    }

    /// <summary>
    /// Delete all managed rules — call on clean shutdown.
    /// </summary>
    public void DeleteAllRules()
    {
        foreach (var rule in _managedRules)
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{rule}\"");
            _logger.Debug("[Firewall] Deleted rule: {Rule}", rule);
        }
        _managedRules.Clear();
        _logger.Information("[Firewall] All VPNRouter firewall rules deleted");
    }

    // ─── Private ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Create a single block rule. Returns true if netsh succeeded.
    /// </summary>
    private bool CreateBlockRule(string ruleName, string programPath, bool enabled)
    {
        var enabledStr = enabled ? "yes" : "no";

        // Outbound block — blocks all direct internet access for this process.
        // When sing-box TUN is up, traffic goes through TUN (not affected by this rule).
        // When sing-box is down, TUN is gone, traffic would go direct — this rule blocks it.
        var success = RunNetsh($"advfirewall firewall add rule " +
                 $"name=\"{ruleName}\" " +
                 $"dir=out " +
                 $"action=block " +
                 $"program=\"{programPath}\" " +
                 $"enable={enabledStr} " +
                 $"profile=any " +
                 $"description=\"VPNRouter block_on_vpn_fail\"");

        if (success)
        {
            _logger.Debug("[Firewall] Created rule '{Rule}' for {Program} (enabled: {Enabled})",
                ruleName, programPath, enabled);
        }

        return success;
    }

    /// <summary>
    /// Resolve the full path to an executable.
    /// 1) Running process image path via <see cref="ProcessImagePath"/>
    ///    (QueryFullProcessImageName / PROCESS_QUERY_LIMITED_INFORMATION) — most
    ///    reliable, gives the real filesystem path + casing, AND works from
    ///    session 0 / a SYSTEM Windows Service to a user-session target. That
    ///    cross-session case is the whole reason this changed: the previous
    ///    <c>Process.MainModule.FileName</c> returned null for EVERY routed
    ///    process under the autostart Service (session-0 isolation / WOW64), so
    ///    <see cref="CreateBlockRules"/> created zero rules and the kill-switch
    ///    failed OPEN. See <see cref="ProcessImagePath"/> for the full rationale.
    /// 2) Fall back to where.exe (finds exe on PATH, e.g. for system processes)
    /// 3) Return null if not found — caller should skip this rule
    /// </summary>
    private string? ResolveProcessPath(string processName)
    {
        // 1. Running process → image path. Cross-session safe (unlike
        //    MainModule), so the Service/SYSTEM autostart path resolves the
        //    user-session apps it previously could not — the fail-OPEN fix.
        if (OperatingSystem.IsWindows())
        {
            var running = ProcessImagePath.ResolveRunningPath(processName);
            if (!string.IsNullOrEmpty(running))
                return running;
        }

        // 2. Try where.exe — finds executables on PATH. #7 (cleanup 2026-07-10):
        // shared with the true-split path resolver via ProcessImagePath, passing
        // OUR injected runner so the rule-creation tests keep mocking where.exe.
        if (OperatingSystem.IsWindows())
        {
            var onPath = ProcessImagePath.ResolveNameToPath(processName, _runner);
            if (!string.IsNullOrEmpty(onPath))
                return onPath;
        }

        _logger.Debug("[Firewall] Could not resolve path for {Process}", processName);
        return null;
    }

    /// <summary>
    /// Remove any VPNRouter firewall rules left from a previous crash.
    /// netsh does NOT support wildcards in rule names, so we enumerate
    /// all rules and delete those matching one of our managed prefixes
    /// by exact name.
    ///
    /// <para>Wave 39 (2026-05-19) extension: in addition to the legacy
    /// <c>VPNRouter_Block_*</c> prefix used for block_on_vpn_fail rules,
    /// we now also sweep the <c>VPNRouter-DnsLockdown-*</c> and
    /// <c>0_VPNRouter-DnsLockdown-*</c> prefixes used by
    /// <see cref="EnableDnsLockdownAsync"/>. Without this extension, an
    /// abnormal exit while the DNS lockdown is active would leave the
    /// user unable to resolve any DNS on next boot until they manually
    /// flushed Windows Firewall.</para>
    /// </summary>
    public void CleanupOrphanedRules()
    {
        var orphaned = new List<string>();
        foreach (var prefix in AllPrefixes)
            orphaned.AddRange(FindRulesByPrefix(prefix));

        if (orphaned.Count == 0)
        {
            _logger.Debug("[Firewall] No orphaned rules found");
            return;
        }

        foreach (var ruleName in orphaned)
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
            _logger.Debug("[Firewall] Deleted orphaned rule: {Rule}", ruleName);
        }

        _logger.Information("[Firewall] Cleaned up {Count} orphaned rules", orphaned.Count);
    }

    /// <summary>
    /// Enumerate firewall rules whose name starts with the given prefix.
    /// Uses 'netsh advfirewall firewall show rule name=all' and parses output.
    /// </summary>
    private List<string> FindRulesByPrefix(string prefix)
    {
        var result = new List<string>();

        try
        {
            // Phase 3+ (2026-05-21): routed through IProcessRunner. Wire shape
            // preserved — same `advfirewall firewall show rule name=all
            // dir=out` netsh args, same 10s outer cap. The block-aware
            // parser below is locale-tolerant (it uses blank-line boundaries
            // rather than label match), so the lost OEM encoding override
            // does not affect rule-name detection (rule names are ASCII).
            var psiResult = _runner.RunAsync(new ProcessRequest(
                ExecutablePath: "netsh.exe",
                Arguments: new[] { "advfirewall", "firewall", "show", "rule", "name=all", "dir=out" },
                Timeout: TimeSpan.FromMilliseconds(10_000))).GetAwaiter().GetResult();

            if (psiResult.TimedOut) return result;
            var output = psiResult.Stdout;

            // v2.31.0-r1 (CO-5 audit fix): the previous parser matched ANY
            // line where the value-after-`:` started with the prefix. On
            // localized Windows (RU/DE/ES) `netsh` outputs `Description:`
            // / `Описание:` / `Beschreibung:` BESIDE `Rule Name:` / `Имя
            // правила:` / `Regelname:`. If a user happened to have any
            // firewall rule whose Description began with `VPNRouter_Block_`
            // — including descriptions of UNRELATED rules — they'd get
            // silently deleted by FlushOrphanRules at startup. Real risk
            // of clobbering user firewall config on non-EN locales.
            //
            // Fix: structurally rely on the BLANK-LINE-separated rule
            // blocks. The first field of each block is always the rule
            // name (regardless of locale label). Track block boundaries
            // and only inspect the first colon-line per block.
            var inNewBlock = true;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    inNewBlock = true;
                    continue;
                }
                if (!inNewBlock) continue;
                inNewBlock = false; // consume this block's first field

                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx < 0) continue;

                var value = trimmed[(colonIdx + 1)..].Trim();
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(value);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Firewall] Failed to enumerate firewall rules");
        }

        return result;
    }

    /// <summary>
    /// Execute a netsh command. Returns true if exit code is 0.
    ///
    /// <para>Phase 3+ (2026-05-21): routed through IProcessRunner. Wire shape
    /// preserved — callers pass shell-style argument strings (e.g.
    /// <c>advfirewall firewall add rule name="..." program="..."</c>) and we
    /// split on whitespace into argv tokens that ProcessRunner's
    /// ArgumentList passes verbatim to netsh. The split is whitespace-only
    /// (no quote awareness) because every existing call site uses
    /// <c>name="..."</c> / <c>program="..."</c> kvpairs whose VALUES may
    /// contain spaces — those go through unmolested because the split
    /// happens BEFORE the quotes are interpreted. Callers that previously
    /// emitted <c>name="VPNRouter_Block_App"</c> still emit the literal
    /// quoted argument; netsh receives the whole token including the
    /// embedded double-quotes, which it strips per its own arg parser.</para>
    /// </summary>
    private bool RunNetsh(string arguments)
    {
        try
        {
            var argv = SplitShellArgs(arguments);
            var result = _runner.RunAsync(new ProcessRequest(
                ExecutablePath: "netsh.exe",
                Arguments: argv,
                Timeout: TimeSpan.FromMilliseconds(NetshTimeoutMs))).GetAwaiter().GetResult();

            if (result.TimedOut)
            {
                _logger.Warning("[Firewall] netsh timed out for: {Args}", arguments);
                return false;
            }

            if (result.ExitCode != 0)
            {
                _logger.Warning("[Firewall] netsh returned {Code} for: {Args} | stdout: {Out} | stderr: {Err}",
                    result.ExitCode, arguments, result.Stdout.Trim(), result.Stderr.Trim());
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Firewall] netsh failed: {Args}", arguments);
            return false;
        }
    }

    /// <summary>
    /// Split a shell-style argument string into argv tokens for
    /// <see cref="ProcessRequest.Arguments"/>. <b>Quote-aware</b> — whitespace
    /// inside <c>"..."</c> stays in one token, and the surrounding double
    /// quotes are stripped (matching <c>CommandLineToArgvW</c> semantics).
    ///
    /// <para><b>Why we strip quotes</b>: pre-Phase 3+ the arg string was
    /// passed via <see cref="ProcessStartInfo.Arguments"/> verbatim and
    /// Windows did its own quote-aware split inside CreateProcess —
    /// netsh.exe ultimately received bare values like
    /// <c>description=VPNRouter block_on_vpn_fail</c> (no quotes around the
    /// value). Post-Phase 3+ we use
    /// <see cref="ProcessStartInfo.ArgumentList"/> which re-serializes each
    /// token using <c>PasteArguments.AppendArgument</c>: if the token
    /// contains a space, .NET surrounds it with quotes; if it ALREADY
    /// contains quote characters, .NET back-slash-escapes them. To preserve
    /// the original wire shape (bare value reaching netsh), we strip the
    /// surrounding double-quotes during the split. .NET then re-quotes for
    /// us based on whether the bare value contains whitespace — yielding
    /// the byte-equivalent command line netsh used to receive.</para>
    ///
    /// <para>Inputs handled:
    /// <list type="bullet">
    /// <item><c>key=value</c> (no quotes, no spaces) — passed through.</item>
    /// <item><c>key="value with spaces"</c> — token = <c>key=value with spaces</c>;
    /// .NET re-quotes for the kernel.</item>
    /// <item><c>key=val,val2</c> (commas, no spaces, no quotes — BR-9
    /// <c>remoteip=...</c>) — passed through as a single token.</item>
    /// </list></para>
    ///
    /// <para>This is NOT a general shell parser — it deliberately does NOT
    /// support escape sequences (<c>\"</c>), single quotes, or nested
    /// quoting. The class never emits those shapes. Marked
    /// <c>internal</c> so the wire-shape tests can pin the split behaviour
    /// directly without round-tripping through netsh.</para>
    /// </summary>
    internal static string[] SplitShellArgs(string arguments)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];
            if (c == '"')
            {
                // Drop the quote character itself — toggle the in-quotes
                // flag so the next space is treated as content, not a
                // separator.
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DeleteAllRules();
    }

    // ─── Wave 39 (2026-05-19): DNS leak lockdown helpers ─────────────────────
    //
    // Static helpers (no per-instance state) because they're called from the
    // also-static <see cref="WindowsDnsHardening.Apply"/> /
    // <see cref="WindowsDnsHardening.Restore"/> entry points. The 4 rules
    // they manage are independent of the per-process block_on_vpn_fail
    // rules — they live in their own namespace, get their own
    // cleanup path, and are enumerable by <see cref="CleanupOrphanedRules"/>
    // via the dual-prefix sweep.

    /// <summary>
    /// Wave 39 (2026-05-19) — install 4 outbound-firewall rules that prevent
    /// the Windows DNS Client from leaking queries to non-VPN resolvers:
    ///
    /// <list type="number">
    /// <item><c>0_VPNRouter-DnsLockdown-LoopbackAllow</c> — allow UDP/53 to
    /// 127.0.0.1 (lets local DNS proxies on loopback keep working, e.g.
    /// dnscrypt-proxy on 127.0.0.1:53). Prefixed with <c>0_</c> so the rule
    /// sorts above the block rules in the Firewall UI; Windows Firewall
    /// evaluation is action+specificity-based but more-specific Allow on a
    /// remoteip=127.0.0.1 still wins for loopback traffic.</item>
    /// <item><c>VPNRouter-DnsLockdown-UDP53</c> — block UDP/53 outbound on
    /// all interfaces (covers standard DNS queries).</item>
    /// <item><c>VPNRouter-DnsLockdown-TCP53</c> — block TCP/53 outbound
    /// (DNS over TCP, used when responses exceed UDP MTU).</item>
    /// <item><c>VPNRouter-DnsLockdown-TCP853</c> — block TCP/853 outbound
    /// (DNS over TLS endpoint port — both standalone DoT clients and the
    /// Windows DNS Client's DoH-fallback-to-DoT path use this).</item>
    /// </list>
    ///
    /// <para>Sing-box's DNS flow goes via VLESS outbound on port 443 (DoH to
    /// AdGuard/Cloudflare), NOT via 53/853 — these blocks do NOT affect the
    /// legitimate VPN-side DNS path; they only kill queries the Windows DNS
    /// Client races in parallel via ethernet directly to ISP resolvers.</para>
    ///
    /// <para>Idempotent: netsh returns a non-zero exit code with "Rule already
    /// exists" when you re-add an existing rule. We catch that and log debug
    /// rather than failing the start. The asymmetric error handling
    /// (swallow + log) ensures a netsh hiccup during VPN start never blocks
    /// the user from connecting.</para>
    ///
    /// <para>No-op on non-Windows (the helper returns immediately on
    /// macOS/Linux). The <c>[SupportedOSPlatform]</c> attribute documents
    /// the platform contract; the runtime guard makes a cross-platform
    /// build link cleanly without #ifdef.</para>
    /// </summary>
    /// <param name="logger">Serilog logger for status/error output.</param>
    /// <param name="ct">Cancellation token (currently advisory — netsh
    /// calls run synchronously with bounded per-call timeouts).</param>
    [SupportedOSPlatform("windows")]
    public static async Task EnableDnsLockdownAsync(
        ILogger? logger = null,
        string? tunCidr = null,
        CancellationToken ct = default)
    {
        var log = logger ?? Log.Logger;
        if (!OperatingSystem.IsWindows())
        {
            log.Debug("[FirewallManager] DNS lockdown skipped — non-Windows platform");
            return;
        }

        // BR-8 (brat 2026-05-20) — derive the TUN allow IP from the
        // settings-provided CIDR. Caller passes settings.Tun.Ipv4Address
        // (e.g. "172.19.0.1/30"). Strip the prefix length to get the
        // network for the netsh rule. Falls back to the bundled-default
        // /30 range when caller didn't supply.
        var tunAllowIp = NormalizeTunAllowIp(tunCidr) ?? "172.19.0.0/30";

        // BR-9 (brat 2026-05-20, r17) — for the BLOCK rules below we
        // need the COMPLEMENT of the TUN range, because Windows Defender
        // Firewall's documented outbound semantics are
        // "Block always wins over Allow" (verified by user-report
        // 2026-05-20: r16's separate TUN allow rule had no effect — the
        // unscoped block rule kept overriding it).
        //
        // The fix is to NARROW the block rule's `remoteip` so it never
        // matches TUN-bound DNS traffic in the first place. The block
        // applies to "every IP except the TUN /30 + loopback". netsh
        // accepts comma-separated IP lists with explicit ranges, so we
        // emit two ranges: 0.0.0.0..(tun-1) and (tun-end+1)..255.255.255.255.
        var blockExclusionRange = ComputeBlockExclusionRange(tunAllowIp) ?? "0.0.0.0-172.18.255.255,172.19.0.4-255.255.255.255";

        // 5s outer timeout so a stuck netsh doesn't block VPN startup.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await Task.Run(() =>
            {
                // 1. Allow UDP/53 to 127.0.0.1 — must be added FIRST so any
                //    local DNS proxy on loopback (dnscrypt-proxy etc.) keeps
                //    working before the blocks below activate. Idempotent —
                //    if the rule already exists, netsh logs "already exists"
                //    and exits non-zero, which we treat as success.
                RunNetshStatic(log,
                    $"advfirewall firewall add rule " +
                    $"name=\"{DnsLockdownAllowRule}\" " +
                    $"dir=out action=allow " +
                    $"protocol=UDP remoteip=127.0.0.1 remoteport=53 " +
                    $"enable=yes profile=any " +
                    $"description=\"VPNRouter Wave 39: allow loopback DNS for local proxies\"");

                // r12 BR-8 attempt installed a separate allow rule for the
                // TUN range — but that didn't work in practice because
                // Windows Defender Firewall outbound semantics ("Block
                // wins over Allow") gave precedence to the block rule
                // anyway. brat 2026-05-20 user report confirmed: r16 (with
                // allow rule) still broke internet whenever the checkbox
                // was on. r17 (BR-9) removes the separate allow and
                // narrows the block rule's `remoteip` instead so it
                // never matches the TUN range to begin with. See
                // ComputeBlockExclusionRange below for the math.

                // 2. Block UDP/53 outbound EVERYWHERE EXCEPT TUN range.
                // BR-9: `remoteip` scoped to complement-of-TUN range so the
                // block rule never matches TUN-bound DNS traffic. This is
                // the only reliable way given Windows Firewall outbound's
                // "Block wins over Allow" semantics — a separate allow rule
                // (BR-8 r12 attempt) doesn't override the block.
                RunNetshStatic(log,
                    $"advfirewall firewall add rule " +
                    $"name=\"{DnsLockdownUdp53Rule}\" " +
                    $"dir=out action=block " +
                    $"protocol=UDP remoteport=53 " +
                    $"remoteip={blockExclusionRange} " +
                    $"enable=yes profile=any " +
                    $"description=\"VPNRouter Wave 39 BR-9: block UDP/53 to prevent DNS leak (TUN range excluded)\"");

                // 3. Block TCP/53 outbound (DNS-over-TCP fallback), same scope.
                RunNetshStatic(log,
                    $"advfirewall firewall add rule " +
                    $"name=\"{DnsLockdownTcp53Rule}\" " +
                    $"dir=out action=block " +
                    $"protocol=TCP remoteport=53 " +
                    $"remoteip={blockExclusionRange} " +
                    $"enable=yes profile=any " +
                    $"description=\"VPNRouter Wave 39 BR-9: block TCP/53 to prevent DNS leak (TUN range excluded)\"");

                // 4. Block TCP/853 outbound (DNS-over-TLS), same scope.
                RunNetshStatic(log,
                    $"advfirewall firewall add rule " +
                    $"name=\"{DnsLockdownTcp853Rule}\" " +
                    $"dir=out action=block " +
                    $"protocol=TCP remoteport=853 " +
                    $"remoteip={blockExclusionRange} " +
                    $"enable=yes profile=any " +
                    $"description=\"VPNRouter Wave 39 BR-9: block TCP/853 to prevent DNS leak (TUN range excluded)\"");

                // ── v2.40.0-r10 #6: IPv6 parallel blocks ────────────────────
                // The four blocks above are IPv4-only. Without these, a dual-
                // stack host leaks DNS over IPv6 to public resolvers (the
                // Windows DNS Client races AAAA/A queries on both families).
                // Scope = 2000::/3 (public global-unicast) so loopback / link-
                // local / ULA / multicast DNS stay intact. No TUN exclusion is
                // needed because the shipping TUN is IPv4-only and sing-box's
                // own DNS is DoH/443, never 53/853. See the rule-name comment
                // block for the full safety rationale.

                // 5. Block UDP/53 over public IPv6.
                RunNetshStatic(log,
                    $"advfirewall firewall add rule " +
                    $"name=\"{DnsLockdownUdp53Ipv6Rule}\" " +
                    $"dir=out action=block " +
                    $"protocol=UDP remoteport=53 " +
                    $"remoteip={Ipv6PublicDnsScope} " +
                    $"enable=yes profile=any " +
                    $"description=\"VPNRouter r10 #6: block UDP/53 over public IPv6 (2000::/3) to prevent DNS leak\"");

                // 6. Block TCP/53 over public IPv6 (DNS-over-TCP fallback).
                RunNetshStatic(log,
                    $"advfirewall firewall add rule " +
                    $"name=\"{DnsLockdownTcp53Ipv6Rule}\" " +
                    $"dir=out action=block " +
                    $"protocol=TCP remoteport=53 " +
                    $"remoteip={Ipv6PublicDnsScope} " +
                    $"enable=yes profile=any " +
                    $"description=\"VPNRouter r10 #6: block TCP/53 over public IPv6 (2000::/3) to prevent DNS leak\"");

                // 7. Block TCP/853 over public IPv6 (DNS-over-TLS).
                RunNetshStatic(log,
                    $"advfirewall firewall add rule " +
                    $"name=\"{DnsLockdownTcp853Ipv6Rule}\" " +
                    $"dir=out action=block " +
                    $"protocol=TCP remoteport=853 " +
                    $"remoteip={Ipv6PublicDnsScope} " +
                    $"enable=yes profile=any " +
                    $"description=\"VPNRouter r10 #6: block TCP/853 over public IPv6 (2000::/3) to prevent DNS leak\"");
            }, timeoutCts.Token).ConfigureAwait(false);

            // BR-9 r18: log message reflects current architecture. r17
            // dropped the separate allow rule and narrowed the block rule's
            // remoteip to the COMPLEMENT of {Tun}, so {Tun} is the *excluded*
            // range — not an allow scope. Wording matters for log readers.
            log.Information(
                "[FirewallManager] DNS leak lockdown enabled — UDP/53, TCP/53, " +
                "TCP/853 blocked on non-loopback IPv4 interfaces (TUN block-exclusion={Tun}, " +
                "BR-9) + public-IPv6 ({Ipv6Scope}) DNS blocked (r10 #6)", tunAllowIp, Ipv6PublicDnsScope);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            log.Warning("[FirewallManager] DNS leak lockdown setup timed out after 5s — partial rule set may be active");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[FirewallManager] DNS leak lockdown setup failed (non-fatal — VPN start continues)");
        }
    }

    /// <summary>
    /// Wave 39 (2026-05-19) — remove the 4 DNS-lockdown rules installed by
    /// <see cref="EnableDnsLockdownAsync"/>. Tolerates rule-not-found
    /// (netsh exit code 1 when the rule doesn't exist) so re-running the
    /// disable on an already-clean state is a no-op.
    ///
    /// <para>Called from <see cref="WindowsDnsHardening.Restore"/> as part
    /// of the symmetric VPN-stop path. Also indirectly via
    /// <see cref="CleanupOrphanedRules"/> on app boot if the previous run
    /// crashed without a clean Restore.</para>
    /// </summary>
    /// <param name="logger">Serilog logger for status/error output.</param>
    /// <param name="ct">Cancellation token (currently advisory).</param>
    [SupportedOSPlatform("windows")]
    public static async Task DisableDnsLockdownAsync(ILogger? logger = null, CancellationToken ct = default)
    {
        var log = logger ?? Log.Logger;
        if (!OperatingSystem.IsWindows())
        {
            log.Debug("[FirewallManager] DNS lockdown disable skipped — non-Windows platform");
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await Task.Run(() =>
            {
                // netsh returns exit code 1 with "No rules match" when the
                // rule isn't there. RunNetshStatic logs the warning but
                // doesn't throw — sufficient for our idempotency need.
                RunNetshStatic(log, $"advfirewall firewall delete rule name=\"{DnsLockdownAllowRule}\"");
                // BR-9 r17: still delete r12's TUN-allow rules in case a
                // user is upgrading from r12..r16 — those rules wouldn't
                // help but they're harmless cruft we should clean up.
                RunNetshStatic(log, $"advfirewall firewall delete rule name=\"{DnsLockdownTunAllowRule}\"");
                RunNetshStatic(log, $"advfirewall firewall delete rule name=\"{DnsLockdownTunAllowRule}-TCP\"");
                RunNetshStatic(log, $"advfirewall firewall delete rule name=\"{DnsLockdownUdp53Rule}\"");
                RunNetshStatic(log, $"advfirewall firewall delete rule name=\"{DnsLockdownTcp53Rule}\"");
                RunNetshStatic(log, $"advfirewall firewall delete rule name=\"{DnsLockdownTcp853Rule}\"");
                // v2.40.0-r10 #6: tear down the parallel IPv6 blocks.
                RunNetshStatic(log, $"advfirewall firewall delete rule name=\"{DnsLockdownUdp53Ipv6Rule}\"");
                RunNetshStatic(log, $"advfirewall firewall delete rule name=\"{DnsLockdownTcp53Ipv6Rule}\"");
                RunNetshStatic(log, $"advfirewall firewall delete rule name=\"{DnsLockdownTcp853Ipv6Rule}\"");
            }, timeoutCts.Token).ConfigureAwait(false);

            log.Information(
                "[FirewallManager] DNS leak lockdown disabled — IPv4 + IPv6 firewall rules deleted (BR-9 r17 + r10 #6)");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            log.Warning("[FirewallManager] DNS leak lockdown teardown timed out after 5s — orphan rules may remain (CleanupOrphanedRules will sweep on next boot)");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[FirewallManager] DNS leak lockdown teardown failed (non-fatal)");
        }
    }

    /// <summary>
    /// BR-8 (brat 2026-05-20) — translate the settings-provided TUN CIDR
    /// (e.g. <c>"172.19.0.1/30"</c>) into a network address suitable for
    /// the netsh <c>remoteip</c> parameter.
    ///
    /// <para>netsh accepts CIDR notation directly (<c>172.19.0.0/30</c>)
    /// so we just normalise the network portion. The default sing-box
    /// TUN config uses /30 with .1 as the host (Windows side) and .2 as
    /// sing-box's DNS endpoint; we want both inside the allow scope.
    /// Returns null for invalid input — caller falls back to the bundled
    /// default range.</para>
    /// </summary>
    internal static string? NormalizeTunAllowIp(string? tunCidr)
    {
        if (string.IsNullOrWhiteSpace(tunCidr)) return null;
        try
        {
            // Strip the host octet → network. Accepts "172.19.0.1/30" or
            // "172.19.0.0/30" or even bare "172.19.0.0".
            var trimmed = tunCidr.Trim();
            var slash = trimmed.IndexOf('/');
            var ip = slash >= 0 ? trimmed[..slash] : trimmed;
            var prefix = slash >= 0 && int.TryParse(trimmed[(slash + 1)..], out var p) ? p : 30;

            if (!System.Net.IPAddress.TryParse(ip, out var parsed))
                return null;
            if (parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return null; // IPv4 only — sing-box TUN config is IPv4 in our shipping defaults.

            // Compute network address by masking host bits.
            var bytes = parsed.GetAddressBytes();
            var hostBits = 32 - prefix;
            uint mask = hostBits >= 32 ? 0u : (0xFFFFFFFFu << hostBits);
            uint addr = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            uint network = addr & mask;
            var netBytes = new byte[]
            {
                (byte)(network >> 24),
                (byte)(network >> 16),
                (byte)(network >> 8),
                (byte)network,
            };
            var networkAddr = new System.Net.IPAddress(netBytes);
            return $"{networkAddr}/{prefix}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// BR-9 (brat 2026-05-20, r17) — compute the COMPLEMENT of the TUN
    /// CIDR for use as the BLOCK rule's <c>remoteip</c> scope. Windows
    /// Defender Firewall outbound semantics make "Block wins over Allow"
    /// (Microsoft docs + r16 user verification: a separate TUN allow
    /// rule has no effect). The only reliable way to let TUN-bound DNS
    /// through while still blocking ISP/public-resolver leaks is to
    /// scope the block rule itself so it never matches TUN destinations.
    ///
    /// <para>Returns two ranges: <c>0.0.0.0..(tun-1), (tun-end+1)..255.255.255.255</c>.
    /// netsh accepts comma-separated ranges in <c>remoteip</c>. Returns
    /// null for invalid input; caller falls back to a hard-coded default
    /// for the bundled <c>172.19.0.0/30</c> TUN.</para>
    /// </summary>
    internal static string? ComputeBlockExclusionRange(string? tunCidr)
    {
        if (string.IsNullOrWhiteSpace(tunCidr)) return null;
        try
        {
            var trimmed = tunCidr.Trim();
            var slash = trimmed.IndexOf('/');
            var ipPart = slash >= 0 ? trimmed[..slash] : trimmed;
            var prefix = slash >= 0 && int.TryParse(trimmed[(slash + 1)..], out var p) ? p : 30;

            if (!System.Net.IPAddress.TryParse(ipPart, out var parsed))
                return null;
            if (parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return null;

            var bytes = parsed.GetAddressBytes();
            var hostBits = 32 - prefix;
            uint mask = hostBits >= 32 ? 0u : (0xFFFFFFFFu << hostBits);
            uint addr = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            uint network = addr & mask;
            uint broadcast = network | (~mask & 0xFFFFFFFFu);

            // Guard against pathological inputs.
            if (network == 0u) return $"{Format(broadcast + 1u)}-255.255.255.255";
            if (broadcast == 0xFFFFFFFFu) return $"0.0.0.0-{Format(network - 1u)}";

            return $"0.0.0.0-{Format(network - 1u)},{Format(broadcast + 1u)}-255.255.255.255";

            static string Format(uint a) =>
                $"{(a >> 24) & 0xFF}.{(a >> 16) & 0xFF}.{(a >> 8) & 0xFF}.{a & 0xFF}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Static-friendly netsh invoker for the Wave 39 lockdown helpers.
    /// Mirrors the per-instance <see cref="RunNetsh"/> shape but emits to
    /// the supplied logger directly so the static helpers don't need an
    /// instance. Returns true on exit code 0; logs the failure for any
    /// non-zero exit but doesn't throw — both EnableDnsLockdownAsync and
    /// DisableDnsLockdownAsync intentionally tolerate per-rule failures
    /// (idempotency: "rule already exists" / "no rules match" come back
    /// with non-zero exits).
    ///
    /// <para>Phase 3+ (2026-05-21) IProcessRunner adoption: routed through
    /// the class-static <see cref="Runner"/> seam. Wire shape preserved —
    /// same 3 s per-call timeout, same exit-code semantics, same arg
    /// tokenisation via <see cref="SplitShellArgs"/>. Tests may swap
    /// <see cref="Runner"/> for a <c>FakeProcessRunner</c> and assert the
    /// captured <c>RunCalls</c> shape including the BR-9 <c>remoteip=...</c>
    /// argument.</para>
    /// </summary>
    private static bool RunNetshStatic(ILogger log, string arguments)
    {
        try
        {
            var argv = SplitShellArgs(arguments);
            var result = Runner.RunAsync(new ProcessRequest(
                ExecutablePath: "netsh.exe",
                Arguments: argv,
                Timeout: TimeSpan.FromMilliseconds(3000))).GetAwaiter().GetResult();

            if (result.TimedOut)
            {
                log.Warning("[FirewallManager] netsh timed out after 3s for: {Args}", arguments);
                return false;
            }

            if (result.ExitCode != 0)
            {
                // Common idempotency cases: "rule already exists" on add,
                // "no rules match the specified criteria" on delete. Both
                // map to non-zero exit but are expected — log at Debug.
                log.Debug("[FirewallManager] netsh returned {Code} for: {Args} | stdout: {Out} | stderr: {Err}",
                    result.ExitCode, arguments, result.Stdout.Trim(), result.Stderr.Trim());
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[FirewallManager] netsh failed: {Args}", arguments);
            return false;
        }
    }
}
