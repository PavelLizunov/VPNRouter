using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Best-effort diagnostic snapshot of the Windows TUN interface state.
///
/// <para>v2.27.2: introduced as a passive data-gathering step so we can
/// see — in production logs — whether our "orphan sing-box kill" path
/// actually leaves dangling <c>VPNRouter-TUN</c> adapters behind.</para>
///
/// <para>v2.30.1-r5: hypothesis confirmed by user reports
/// ("periodically the network interface doesn't die and Windows reboot
/// is required"). Added active cleanup via
/// <see cref="DisableOrphanedAdapter"/> — disables the wintun adapter
/// in the device manager when sing-box exits without releasing it,
/// freeing the OS network stack from the dangling routes / DNS that
/// were keeping the user's network state stuck.</para>
///
/// <para>Phase 3+ (2026-05-21) IProcessRunner adoption: every netsh /
/// powershell shell-out routes through the static <see cref="Runner"/>
/// seam so tests can swap in a <c>FakeProcessRunner</c> to drive canned
/// stdout / exit codes. Wire shape (executable + args + timeouts)
/// preserved byte-for-byte vs the pre-migration direct-Process code.</para>
/// </summary>
public static class TunAdapterDiagnostics
{
    private const int PnpRemovalPollIntervalMs = 300;
    private const int PnpRemovalMaxPolls = 40;
    private const int PnpRemovalAbsentSamples = 3;
    private const int PnpRemovalQuietPeriodMs = 2_000;
    private const int PnpRemovalBudgetMs = 12_000;

    /// <summary>
    /// Phase 3+ (2026-05-21) IProcessRunner seam. Tests assign a
    /// <c>FakeProcessRunner</c> before exercising the static helpers and
    /// reset back to the default in a try/finally. Not thread-safe —
    /// assumes serial xUnit execution within the fixture (single test
    /// class), matching the existing
    /// <c>WindowsDnsHardening._runnerOverride</c> + <c>FirewallManager.Runner</c>
    /// patterns.
    /// </summary>
    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

    /// <summary>Delay seam for deterministic PnP-settle tests.</summary>
    internal static Func<TimeSpan, CancellationToken, Task> RemovalDelayAsync { get; set; } =
        static (delay, ct) => Task.Delay(delay, ct);

    // Windows 10 before build 19041 (including LTSC 2019 / build 17763)
    // predates pnputil /remove-device. Keep the newer
    // pnputil path intact and use SetupAPI/ConfigMgr only on those builds.
    internal static Func<bool> RequiresNativePnpApi { get; set; } =
        static () => RequiresNativePnpForWindowsBuild(Environment.OSVersion.Version.Build);
    internal static bool RequiresNativePnpForWindowsBuild(int build) => build < 19041;
    internal static Func<string, NativePnpRemovalResult> RemoveNativePnpDevice { get; set; } =
        WindowsPnpDeviceManager.RemoveDevice;
    internal static Func<string, NativePnpPresenceResult> QueryNativePnpPresence { get; set; } =
        WindowsPnpDeviceManager.QueryPresence;
    internal static Func<string, NativePnpLookupResult> ResolveNativePnpDeviceIds { get; set; } =
        WindowsPnpDeviceManager.FindNetworkAdapterInstanceIds;

    /// <summary>
    /// Log current TUN adapter inventory via <c>netsh interface show interface</c>.
    /// Windows-only; returns silently on other platforms. Errors are swallowed
    /// — diagnostics must never block startup.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void LogAdapterState(ILogger? logger, string context)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            // Phase 3+ (2026-05-21): routed through IProcessRunner. Same
            // 3 s timeout + `netsh interface show interface` args as the
            // pre-migration direct-Process call.
            var psiResult = Runner.RunAsync(new ProcessRequest(
                ExecutablePath: "netsh",
                Arguments: new[] { "interface", "show", "interface" },
                Timeout: TimeSpan.FromMilliseconds(3000))).GetAwaiter().GetResult();

            if (psiResult.TimedOut) return;
            var output = psiResult.Stdout;

            // Parse out lines referencing our interface name. The netsh
            // output is verbose and English-locale-dependent; we only
            // want the rows that mention VPNRouter-TUN or any
            // "sing-box-tun-" adapter (sing-box's fallback when a custom
            // InterfaceName is unavailable) so log noise stays minimal.
            var hits = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(l =>
                    l.IndexOf("VPNRouter-TUN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    l.IndexOf("sing-box-tun", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (hits.Count == 0)
            {
                logger?.Information("[TunDiag] {Ctx}: no VPNRouter-TUN or sing-box-tun adapters found", context);
                return;
            }

            logger?.Information(
                "[TunDiag] {Ctx}: found {Count} TUN adapter row(s) in netsh:",
                context, hits.Count);
            foreach (var line in hits)
            {
                logger?.Information("[TunDiag]   {Line}", line.Trim());
            }
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[TunDiag] {Ctx}: inventory query failed (non-fatal)", context);
        }
    }

    /// <summary>
    /// v2.30.1-r5: aggressive cleanup for orphaned wintun adapters.
    /// Called when sing-box exits unexpectedly (crash, silent kill on
    /// Windows wake, etc.) to disable the dangling network interface
    /// so the OS releases the cached routes / DNS / TUN handle.
    ///
    /// <para>Without this, users hit "the network interface doesn't
    /// die and I have to reboot Windows" after sing-box silent-kill —
    /// the wintun adapter stays in the netsh inventory in a half-alive
    /// state, holding TUN-routed default routes that the network stack
    /// can't easily flush. Disabling the adapter via netsh forces
    /// Windows to drop those routes immediately.</para>
    ///
    /// <para>Non-fatal: any error is swallowed and logged at Warning
    /// level. Cleanup is idempotent — disabling an already-disabled or
    /// already-deleted adapter is a no-op (with a "not found" stderr
    /// from netsh that we ignore).</para>
    ///
    /// <para>Intentionally uses <c>netsh interface set interface ...
    /// admin=disabled</c> instead of <c>Remove-NetAdapter</c> because:
    /// (a) PowerShell isn't always on PATH inside our service-managed
    /// process tree, (b) wintun adapters refuse Remove-NetAdapter when
    /// the underlying handle is still open by sing-box's GC-pending
    /// cleanup, but disable always succeeds. After disable, sing-box's
    /// next start will re-enable the adapter automatically.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void DisableOrphanedAdapter(ILogger? logger, string interfaceName, string context)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (string.IsNullOrWhiteSpace(interfaceName)) return;

        try
        {
            // Phase 3+ (2026-05-21): routed through IProcessRunner. The
            // `name=` token carries the interface name as a single argv
            // entry (.NET's ArgumentList re-quotes it for the kernel when
            // the value contains spaces), matching the pre-migration
            // command-line shape `netsh interface set interface
            // name="VPNRouter-TUN" admin=disabled`.
            var psiResult = Runner.RunAsync(new ProcessRequest(
                ExecutablePath: "netsh",
                Arguments: new[]
                {
                    "interface", "set", "interface",
                    $"name={interfaceName}",
                    "admin=disabled",
                },
                Timeout: TimeSpan.FromMilliseconds(3000))).GetAwaiter().GetResult();

            if (psiResult.TimedOut)
            {
                logger?.Warning(
                    "[TunDiag] {Ctx}: netsh disable for '{Iface}' timed out after 3s",
                    context, interfaceName);
                return;
            }

            var stdout = psiResult.Stdout;
            var stderr = psiResult.Stderr;
            var exitCode = psiResult.ExitCode;

            // netsh exit codes: 0 = success, 1 = "element not found"
            // (adapter already gone — fine), other = real failure.
            if (exitCode == 0)
            {
                logger?.Information(
                    "[TunDiag] {Ctx}: disabled orphaned adapter '{Iface}' (network stack should release routes)",
                    context, interfaceName);
            }
            else if (exitCode == 1
                     || stdout.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                     || stderr.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                logger?.Debug(
                    "[TunDiag] {Ctx}: adapter '{Iface}' already gone — nothing to clean up",
                    context, interfaceName);
            }
            else
            {
                logger?.Warning(
                    "[TunDiag] {Ctx}: netsh disable for '{Iface}' returned exit {Code}: stdout='{Out}' stderr='{Err}'",
                    context, interfaceName, exitCode, stdout.Trim(), stderr.Trim());
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[TunDiag] {Ctx}: disable orphaned adapter '{Iface}' failed (non-fatal)", context, interfaceName);
        }
    }

    /// <summary>
    /// v2.32.x Bug-r9-H: pre-start sweep for stale wintun adapters left
    /// behind by a previous sing-box <i>crash</i> (not a graceful Stop —
    /// graceful path is already covered by <see cref="DisableOrphanedAdapter"/>).
    ///
    /// <para>Symptom: stas's log showed sing-box dying with FATAL
    /// <c>configure tun interface: Cannot create a file when that file
    /// already exists</c>, and every subsequent start hitting the same
    /// error. The wintun driver's internal state still believes a
    /// <c>VPNRouter-TUN</c> adapter exists, so a fresh
    /// <c>WintunCreateAdapter</c> call refuses with ERROR_FILE_EXISTS.
    /// Disabling alone (the path <see cref="DisableOrphanedAdapter"/>
    /// takes after a clean Stop) doesn't fix this — the device record
    /// has to actually go away.</para>
    ///
    /// <para>Implementation: enumerate adapters via <c>netsh interface
    /// show interface</c>, filter by a <b>strict</b> whitelist
    /// (<c>VPNRouter-TUN</c> exactly + <c>sing-box-tun-*</c> fallback names),
    /// and for each match: disable via netsh (frees the kernel handle),
    /// resolve its PnP InstanceId with <c>Get-NetAdapter</c> or an in-process
    /// <c>Win32_NetworkAdapter</c> WMI query, then delete the exact device
    /// record with <c>pnputil /remove-device</c> or Windows SetupAPI.</para>
    ///
    /// <para><b>Defensive name whitelist.</b> We deliberately do NOT
    /// match <c>Wintun*</c> wildcards. WireGuard, AmneziaWG, OpenVPN TAP
    /// and other coexisting VPN tools all create wintun-class adapters
    /// with their own names — touching them would be cross-tool damage,
    /// and Bug-r9-E (separate chip) is the place for "another VPN
    /// detected" UX, not silent destruction here. Only the two names we
    /// own (<c>VPNRouter-TUN</c> and sing-box's <c>sing-box-tun-*</c>
    /// auto-name fallback) are removable.</para>
    ///
    /// <para>Returns the count of adapters successfully removed.
    /// Linux/macOS: returns 0 immediately. Inventory and tooling errors remain
    /// best-effort, but a located VPNRouter adapter that cannot be removed and
    /// verified throws <see cref="TunAdapterNotReadyException"/> so callers do
    /// not launch sing-box into a known stale PnP state.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<int> PreStartCleanupAsync(
        ILogger? logger,
        string context = "VpnEngine.PreStart")
    {
        if (!OperatingSystem.IsWindows()) return 0;

        int removed = 0;
        var enumerationFoundDefault = false;

        try
        {
            var (_, netshOut, _) = await RunAndCaptureAsync(
                "netsh", new[] { "interface", "show", "interface" },
                timeoutMs: 5000, logger: logger);

            var staleAdapters = ExtractStaleAdapterNames(netshOut);
            if (staleAdapters.Count == 0)
            {
                logger?.Information(
                    "[TunDiag] {Ctx}: pre-start cleanup: no stale TUN adapters found via netsh enumeration",
                    context);
            }
            else
            {
                logger?.Information(
                    "[TunDiag] {Ctx}: pre-start cleanup: found {Count} stale TUN adapter(s) via enumeration: {Names}",
                    context, staleAdapters.Count, string.Join(", ", staleAdapters));

                foreach (var adapter in staleAdapters)
                {
                    if (string.Equals(adapter, DefaultTunInterfaceName, StringComparison.OrdinalIgnoreCase))
                        enumerationFoundDefault = true;

                    // Resolve the exact PnP record before disabling the network
                    // interface, then remove it through the fail-closed gate.
                    if (await TryRemoveAdapterAsync(
                            logger, adapter, context, requireInstanceId: true))
                        removed++;
                    else
                        throw new TunAdapterNotReadyException(
                            $"Could not remove stale VPNRouter TUN adapter '{adapter}'.");
                }
            }

            // Defence-in-depth (hotfix 2026-05-19 / v2.35.0): the netsh
            // enumeration above has been observed to miss the default
            // VPNRouter-TUN adapter in field logs while
            // <see cref="DisableOrphanedAdapter"/> — which targets a known
            // name directly — DID find and act on it. Likely root cause is
            // locale-dependent netsh formatting or a transient state where
            // the adapter row is reported via a different column the parser
            // skips. Either way: an unconditional direct-by-name pass on
            // the well-known VPNRouter-TUN name is cheap (one netsh + one
            // PowerShell, both bounded and idempotent) and guarantees the
            // adapter we own is gone even if enumeration missed it. Skip
            // the redundant call only when the enumeration already
            // processed that exact name.
            if (!enumerationFoundDefault)
            {
                logger?.Debug(
                    "[TunDiag] {Ctx}: pre-start cleanup: direct-by-name fallback for '{Iface}' (enumeration didn't list it)",
                    context, DefaultTunInterfaceName);
                if (await TryRemoveAdapterAsync(logger, DefaultTunInterfaceName, context))
                    removed++;
                else
                    throw new TunAdapterNotReadyException(
                        $"Could not verify removal of '{DefaultTunInterfaceName}'.");
            }

            logger?.Information(
                "[TunDiag] {Ctx}: pre-start cleanup: removed {Removed} TUN adapter(s) total (enumeration + direct fallback)",
                context, removed);

            return removed;
        }
        catch (TunAdapterNotReadyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[TunDiag] {Ctx}: pre-start cleanup diagnostics failed (continuing)", context);
            return removed;
        }
    }

    /// <summary>Default TUN adapter name we own (matches
    /// <c>SingBoxManager.DefaultTunInterfaceName</c>). Kept private to
    /// avoid spreading the literal across cleanup paths.</summary>
    private const string DefaultTunInterfaceName = "VPNRouter-TUN";

    /// <summary>
    /// Parse the output of <c>netsh interface show interface</c> and
    /// return the names of adapters owned by VPNRouter (or by sing-box's
    /// fallback auto-naming when our InterfaceName isn't honoured).
    ///
    /// <para>Whitelist only — <c>VPNRouter-TUN</c> exactly, plus
    /// <c>sing-box-tun</c> with optional alphanumeric suffix. Any other
    /// adapter that happens to use the wintun driver (WireGuard,
    /// AmneziaWG, OpenVPN TAP-Wintun, third-party tools) is intentionally
    /// excluded so pre-start cleanup never destroys a coexisting VPN's
    /// adapter. See <see cref="PreStartCleanupAsync"/> doc-comment for
    /// rationale.</para>
    ///
    /// <para>Internal so unit tests can pin the parser/filter behaviour
    /// without needing a real netsh; production callers get the same
    /// guarantees through <see cref="PreStartCleanupAsync"/>.</para>
    /// </summary>
    internal static List<string> ExtractStaleAdapterNames(string netshOutput)
    {
        if (string.IsNullOrEmpty(netshOutput)) return new List<string>();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Match VPNRouter-TUN as a whole token, or sing-box-tun with an
        // optional `-XXXX` suffix (sing-box's auto-name format when
        // InterfaceName is missing or already taken). \b enforces
        // whole-token boundaries so we don't false-positive on substrings
        // embedded in other names.
        var pattern = new Regex(
            @"\b(VPNRouter-TUN|sing-box-tun(?:-[A-Za-z0-9_-]+)?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        foreach (var rawLine in netshOutput.Split(new[] { '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var match = pattern.Match(line);
            if (match.Success)
                names.Add(match.Value);
        }

        return names.ToList();
    }

    /// <summary>
    /// Best-effort device removal via exact PnP discovery and
    /// <c>pnputil /remove-device</c>. Uses <c>Get-NetAdapter</c> when available
    /// and an in-process <c>Win32_NetworkAdapter</c> WMI query otherwise. Returns
    /// true when the adapter is already absent or its exact PnP InstanceId has
    /// remained absent through the bounded settle gate.
    ///
    /// <para>Uses a single inline command rather than a temp .ps1 so we
    /// don't have to manage a script-on-disk lifecycle. <c>-NoProfile
    /// -NonInteractive</c> keeps PowerShell startup tight (~150 ms).</para>
    ///
    /// <para><b>Direct-call sibling to <see cref="PreStartCleanupAsync"/>.</b>
    /// PreStartCleanupAsync enumerates adapters via <c>netsh interface
    /// show interface</c> first and then calls this helper per match.
    /// SingBoxManager's <c>OnProcessExited</c>, <c>StopInternal</c>, and
    /// <c>LaunchProcess</c> paths call it <b>directly by known name</b>
    /// (the well-known <c>VPNRouter-TUN</c> default) so they don't pay
    /// the enumeration cost and don't depend on
    /// <see cref="ExtractStaleAdapterNames"/> parsing the localized
    /// netsh output correctly — important because field logs have shown
    /// the enumeration occasionally missing the adapter while the
    /// direct-by-name path always works. The "not found" exit code 1
    /// path is handled gracefully (no-op), so the direct call is a
    /// cheap belt-and-suspenders complement to enumeration.</para>
    /// </summary>
    /// <summary>
    /// Wave 39 follow-up (BR-2, brat 2026-05-19): per-process cache flag
    /// — set to 1 the first time we observe the
    /// <c>Remove-NetAdapter</c> cmdlet missing from the user's PowerShell
    /// environment. Subsequent <see cref="TryRemoveAdapterAsync"/> calls
    /// short-circuit on this flag and skip the 600-1000ms PowerShell
    /// spin-up entirely.
    ///
    /// <para>brat's machine (Windows Server / Win11 LTSC / language-pack
    /// install variant — exact cause unknown) ships PowerShell without
    /// the <c>NetAdapter</c> module installed. Every cleanup-site call
    /// (StartupPipeline.ExecuteAsync, SingBoxManager.LaunchProcess,
    /// SingBoxManager.StopInternal.killed.async) blew ~600ms on the
    /// failed cmdlet probe — 3 calls per connect cycle multiplied by
    /// HealthMonitor restart retries surfaced as a 33-second TUN
    /// warm-up vs v2.32.2's 2 seconds.</para>
    ///
    /// <para>Detection signal: stderr containing "is not recognized as
    /// the name of a cmdlet". Once latched, the flag stays set for the
    /// process lifetime — the user's PowerShell modules aren't going
    /// to materialise mid-session. Restart picks up changes.</para>
    ///
    /// <para><b>PinkuDani follow-up (Fix #1, 2026-05-21):</b> BR-2's
    /// reactive latch fails on Russian Windows where CP-866 OEM stderr
    /// mangles "не распознано" into "­Ґ а бЇ®§­ ­®" — the literal-string
    /// search never matches, latch stays at 0, every callsite still
    /// fires PowerShell. Solution: <see cref="s_netAdapterModuleAvailable"/>
    /// is a Lazy proactive probe (`Get-Module NetAdapter -ListAvailable`)
    /// that latches BEFORE the first Remove-NetAdapter attempt, locale-
    /// independent. BR-2 stays as belt-and-suspenders for the rare case
    /// where the module is "available" but its cmdlets somehow fail
    /// later in the process lifetime.</para>
    /// </summary>
    private static int s_removeNetAdapterMissing; // 0 = unknown, 1 = confirmed missing

    /// <summary>
    /// PinkuDani Fix #1 (2026-05-21): Lazy proactive check for the
    /// PowerShell <c>NetAdapter</c> module's availability. Triggered on
    /// the first <see cref="TryRemoveAdapterAsync"/> call. Spawns one
    /// <c>powershell.exe -NoProfile -NonInteractive -Command
    /// "Get-Module NetAdapter -ListAvailable | Measure-Object |
    /// Select -ExpandProperty Count"</c> probe (~340 ms on our test
    /// environment).
    ///
    /// <para>When the probe returns "0", the module is missing
    /// — subsequent <see cref="TryRemoveAdapterAsync"/> calls return
    /// false immediately without spawning PowerShell. When it returns
    /// "1"+ the cmdlet is expected to work, but BR-2's reactive latch
    /// stays armed as a second line of defence.</para>
    ///
    /// <para><b>Test override:</b> tests pre-set the lazy via
    /// <see cref="SetNetAdapterModuleAvailableForTests"/> to skip the
    /// probe and dictate the cached value. Production code never assigns
    /// the field.</para>
    /// </summary>
    private static Lazy<bool> s_netAdapterModuleAvailable =
        new Lazy<bool>(ProbeNetAdapterModuleAvailable, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Phase 1 of PinkuDani Fix #1: the actual probe. Runs synchronously
    /// from inside the Lazy initialiser. Returns true when Get-Module
    /// reports the module count >= 1, false otherwise (missing module,
    /// timeout, parse failure — all fail-closed).
    ///
    /// <para>No <c>[SupportedOSPlatform("windows")]</c> attribute on this
    /// helper so the Lazy field initialiser (which can't be platform-gated
    /// directly) doesn't trigger CA1416. The first line guards with
    /// <c>OperatingSystem.IsWindows()</c> instead so non-Windows callers
    /// always see "false" — same as the attribute would enforce.</para>
    /// </summary>
    private static bool ProbeNetAdapterModuleAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            // Repointed 2026-06-08: probe for Get-NetAdapter — the REAL cmdlet
            // we now use to resolve the orphan's PnP InstanceId — instead of the
            // old `Get-Module NetAdapter -ListAvailable`. That old probe checked
            // module PRESENCE, which was irrelevant: the module is present on
            // every supported SKU, but the removal cmdlet we used to call
            // (Remove-NetAdapter) never existed, so removal always failed. Using
            // Get-Command resolves whether the cmdlet we actually invoke is
            // usable in this spawned context. One-shot via the Runner seam so
            // tests intercept it with FakeProcessRunner.
            var result = Runner.RunAsync(new ProcessRequest(
                ExecutablePath: "powershell.exe",
                Arguments: new[]
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    // scout #1 #3 (2026-06-08): Import-Module explicitly so the probe
                    // doesn't rely on command auto-loading — on GPO-hardened desktops
                    // ($PSModuleAutoLoadingPreference=None) a bare `Get-Command
                    // Get-NetAdapter` can false-negative, which would skip the orphan
                    // removal and silently neuter the fix on the very machine class
                    // this targets. Explicit import is locale- and policy-independent.
                    "Import-Module NetAdapter -ErrorAction SilentlyContinue; " +
                    "if (Get-Command Get-NetAdapter -ErrorAction SilentlyContinue) { 1 } else { 0 }",
                },
                Timeout: TimeSpan.FromMilliseconds(5000))).GetAwaiter().GetResult();

            if (result.TimedOut) return false;
            if (result.ExitCode != 0) return false;

            // stdout is just a number on its own line. Trim and parse.
            // ">= 1" = module installed; "0" or empty = missing.
            var trimmed = (result.Stdout ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(trimmed)) return false;

            return int.TryParse(trimmed, out var count) && count >= 1;
        }
        catch
        {
            // Any exception fails closed (treat as missing) — safer to
            // assume unavailable than to spawn dozens of PowerShell calls
            // looking for a cmdlet that throws on probe.
            return false;
        }
    }

    /// <summary>
    /// Public-internal accessor for the cached resolver selection. A false
    /// value means exact PnP discovery uses in-process Win32_NetworkAdapter WMI instead
    /// of the optional NetAdapter module; it no longer bypasses removal.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static bool IsNetAdapterModuleAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return s_netAdapterModuleAvailable.Value;
    }

    /// <summary>
    /// Once latched at the first INF log, this flag prevents subsequent
    /// "module unavailable" log lines from spamming the file. We only
    /// want the actionable message once per process.
    /// </summary>
    private static int s_actionableModuleMissingLogged; // 0 = not yet, 1 = logged

    /// <summary>
    /// Test-only reset of the BR-2 cmdlet-missing latch. Production code
    /// never resets the flag (the user's PowerShell modules aren't going
    /// to materialise mid-session). Tests that swap in a
    /// <c>FakeProcessRunner</c> need to clear the latch so they aren't
    /// short-circuited by a previous real-runner test that observed the
    /// cmdlet missing. Mirrors the
    /// <c>WindowsDnsHardening._runnerOverride</c> test-reset pattern.
    /// </summary>
    internal static void ResetRemoveNetAdapterLatchForTests()
    {
        Volatile.Write(ref s_removeNetAdapterMissing, 0);
        Volatile.Write(ref s_actionableModuleMissingLogged, 0);
        // Reset the Lazy too — a previous test against the real runner
        // may have resolved it to true/false using the host machine's
        // actual PowerShell. Subsequent fake-runner tests need a fresh
        // probe routed through their stub.
        s_netAdapterModuleAvailable = new Lazy<bool>(
            ProbeNetAdapterModuleAvailable,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Test-only: pre-set the cached availability state without
    /// triggering the real PowerShell probe. Tests use this when they
    /// want to dictate the outcome without writing a Get-Module fake
    /// matcher.
    /// </summary>
    internal static void SetNetAdapterModuleAvailableForTests(bool available)
    {
        s_netAdapterModuleAvailable = new Lazy<bool>(
            () => available, LazyThreadSafetyMode.ExecutionAndPublication);
        // Force the value so tests observing IsNetAdapterModuleAvailable
        // get a deterministic answer without re-entering the probe.
        _ = s_netAdapterModuleAvailable.Value;
    }

    internal static void SetNetAdapterModuleProbeForTests(Func<bool> probe)
    {
        s_netAdapterModuleAvailable = new Lazy<bool>(
            probe, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Netsh-based orphan disable primitive used before exact PnP removal and
    /// by targeted crash recovery. It disables the adapter via
    /// <c>netsh interface set interface name=&lt;NAME&gt; admin=disabled</c>.
    /// This releases the kernel handle so the next sing-box launch's
    /// <c>WintunCreateAdapter</c> doesn't hit ERROR_FILE_EXISTS on the
    /// orphan record.
    ///
    /// <para>This is a thin awaitable wrapper around the existing
    /// <see cref="DisableOrphanedAdapter"/>'s wire shape — same netsh
    /// argv, same exit-code interpretation, returns true on success or
    /// "not found" idempotent path, false on real failure.</para>
    ///
    /// <para>SingBoxManager's recovery path calls this directly before its
    /// next launch; the launch chokepoint then performs exact removal.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static async Task<bool> TryDisableAdapterViaNetshAsync(
        ILogger? logger, string adapterName, string context)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (string.IsNullOrWhiteSpace(adapterName)) return false;

        try
        {
            var (exitCode, stdout, stderr) = await RunAndCaptureAsync(
                "netsh",
                new[]
                {
                    "interface", "set", "interface",
                    $"name={adapterName}",
                    "admin=disabled",
                },
                timeoutMs: 3000, logger: logger);

            if (exitCode == 0)
            {
                logger?.Information(
                    "[TunDiag] {Ctx}: netsh-disabled orphaned adapter '{Name}' (kernel handle released; exact removal follows at launch gate)",
                    context, adapterName);
                return true;
            }

            // netsh exit 1 / "not found" = adapter already gone (idempotent
            // success). Anything else is a real failure.
            if (exitCode == 1
                || (stdout ?? "").IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                || (stderr ?? "").IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                logger?.Debug(
                    "[TunDiag] {Ctx}: netsh-disable for '{Name}' reported not-found (already gone — counts as success)",
                    context, adapterName);
                return true;
            }

            logger?.Warning(
                "[TunDiag] {Ctx}: netsh-disable for '{Name}' returned exit {Exit}: stdout='{Out}' stderr='{Err}'",
                context, adapterName, exitCode, (stdout ?? "").Trim(), (stderr ?? "").Trim());
            return false;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex,
                "[TunDiag] {Ctx}: netsh-disable for '{Name}' threw (non-fatal)",
                context, adapterName);
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static async Task<bool> TryRemoveAdapterAsync(
        ILogger? logger, string adapterName, string context,
        bool requireInstanceId = false)
    {
        // LTSC builds go straight to the in-process lookup. Do not launch even
        // the Get-NetAdapter availability probe on a platform where the modern
        // pnputil verbs are absent and the PowerShell surface is known to vary.
        var useNativePnpApi = RequiresNativePnpApi();
        var useNetAdapterModule = !useNativePnpApi && s_netAdapterModuleAvailable.Value;
        try
        {
            List<string> instanceIds;
            if (!useNetAdapterModule)
            {
                if (Interlocked.CompareExchange(ref s_actionableModuleMissingLogged, 1, 0) == 0)
                {
                    logger?.Information(
                        "[TunDiag] {Ctx}: Get-NetAdapter unavailable; resolving the exact " +
                        "TUN PnP InstanceId in-process through Win32_NetworkAdapter.",
                        context);
                }

                var lookup = ResolveNativePnpDeviceIds(adapterName);
                if (!lookup.Success)
                {
                    logger?.Warning(
                        "[TunDiag] {Ctx}: native PnP InstanceId lookup for '{Name}' failed: {Error}",
                        context, adapterName, lookup.Error ?? "unknown error");
                    return false;
                }

                instanceIds = lookup.InstanceIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                // A previous call proved that the PowerShell resolver is broken.
                if (Volatile.Read(ref s_removeNetAdapterMissing) == 1)
                    return false;

                var resolveScript =
                    $"Get-NetAdapter -Name '{adapterName}' -ErrorAction SilentlyContinue | " +
                    "Select-Object -ExpandProperty PnPDeviceID";
                var (rExit, rOut, rErr) = await RunAndCaptureAsync(
                    "powershell.exe",
                    new[] { "-NoProfile", "-NonInteractive", "-Command", resolveScript },
                    timeoutMs: 10000, logger: logger);

                var rErrText = rErr ?? string.Empty;
                if (rErrText.IndexOf("CommandNotFoundException", StringComparison.OrdinalIgnoreCase) >= 0
                    || rErrText.IndexOf("is not recognized", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Interlocked.Exchange(ref s_removeNetAdapterMissing, 1);
                    logger?.Warning(
                        "[TunDiag] {Ctx}: Get-NetAdapter cannot resolve the PnP InstanceId " +
                        "for '{Name}'.",
                        context, adapterName);
                    return false;
                }

                if (rExit != 0)
                {
                    logger?.Warning(
                        "[TunDiag] {Ctx}: PnP InstanceId query for '{Name}' failed with exit " +
                        "{Exit}: '{Err}'",
                        context, adapterName, rExit, rErrText.Trim());
                    return false;
                }

                instanceIds = (rOut ?? string.Empty)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (instanceIds.Count == 0)
            {
                // No matching adapter — already gone. Idempotent success.
                logger?.Debug(
                    "[TunDiag] {Ctx}: adapter '{Name}' not present (no InstanceId) — nothing to remove",
                    context, adapterName);
                return !requireInstanceId;
            }

            // Step 2: delete every exact device record. No wildcard PnP
            // enumeration and no removal by friendly name.
            // Resolve first, then disable. A disabled adapter can disappear
            // from the network-adapter view while its PnP node remains alive.
            DisableOrphanedAdapter(logger, adapterName, context);

            // LTSC 2019 predates the required pnputil verbs. DiUninstallDevice
            // and CM_Locate_DevNode keep the same exact InstanceId boundary.
            foreach (var id in instanceIds)
            {
                var removed = useNativePnpApi
                    ? await RunNativePnpRemoveAsync(logger, id, adapterName, context)
                    : await RunPnpUtilRemoveAsync(logger, id, adapterName, context);
                if (!removed)
                {
                    throw new TunAdapterNotReadyException(
                        $"Windows could not remove '{adapterName}' ({id}).",
                        id);
                }
            }
            return true;
        }
        catch (TunAdapterNotReadyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex,
                "[TunDiag] {Ctx}: pnputil removal for '{Name}' threw (non-fatal)",
                context, adapterName);
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static Task WaitForExactPnpRemovalSettledAsync(
        ILogger? logger, string instanceId, string adapterName, string context) =>
        WaitForNativePnpRemovalSettledAsync(logger, instanceId, adapterName, context);

    [SupportedOSPlatform("windows")]
    private static async Task<bool> RunNativePnpRemoveAsync(
        ILogger? logger, string instanceId, string adapterName, string context)
    {
        var result = RemoveNativePnpDevice(instanceId);
        if (!result.Success || result.RestartRequired)
        {
            logger?.Warning(
                "[TunDiag] {Ctx}: native PnP removal for '{Name}' ({Id}) failed: " +
                "error={Error}, restartRequired={RestartRequired}",
                context, adapterName, instanceId, result.ErrorCode, result.RestartRequired);
            return false;
        }

        logger?.Information(
            "[TunDiag] {Ctx}: removed stale adapter '{Name}' through Windows SetupAPI ({Id})",
            context, adapterName, instanceId);
        await WaitForNativePnpRemovalSettledAsync(logger, instanceId, adapterName, context)
            .ConfigureAwait(false);
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static async Task WaitForNativePnpRemovalSettledAsync(
        ILogger? logger, string instanceId, string adapterName, string context)
    {
        var absentSamples = 0;
        var elapsed = Stopwatch.StartNew();
        for (var poll = 0; poll < PnpRemovalMaxPolls; poll++)
        {
            if (elapsed.ElapsedMilliseconds >= PnpRemovalBudgetMs)
                break;

            var presence = QueryNativePnpPresence(instanceId);
            if (presence.Presence == NativePnpPresence.Error)
            {
                throw new TunAdapterNotReadyException(
                    $"Windows could not query '{adapterName}' ({instanceId}); ConfigMgr result 0x{presence.ConfigManagerResult:X8}.",
                    instanceId);
            }

            if (presence.Presence == NativePnpPresence.Absent)
            {
                absentSamples++;
                if (absentSamples >= PnpRemovalAbsentSamples)
                {
                    await RemovalDelayAsync(
                            TimeSpan.FromMilliseconds(PnpRemovalQuietPeriodMs),
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    var finalPresence = QueryNativePnpPresence(instanceId);
                    if (finalPresence.Presence == NativePnpPresence.Absent)
                    {
                        logger?.Information(
                            "[TunDiag] {Ctx}: native PnP removal settled for '{Name}' ({Id})",
                            context, adapterName, instanceId);
                        return;
                    }

                    if (finalPresence.Presence == NativePnpPresence.Error)
                    {
                        throw new TunAdapterNotReadyException(
                            $"Windows could not verify removal of '{adapterName}' ({instanceId}); ConfigMgr result 0x{finalPresence.ConfigManagerResult:X8}.",
                            instanceId);
                    }

                    absentSamples = 0;
                }
            }
            else
            {
                absentSamples = 0;
            }

            await RemovalDelayAsync(
                    TimeSpan.FromMilliseconds(PnpRemovalPollIntervalMs),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        throw new TunAdapterNotReadyException(
            $"Windows did not finish removing '{adapterName}' ({instanceId}) before the bounded native PnP settle gate expired.",
            instanceId);
    }

    /// <summary>
    /// 2026-06-08: delete a single device record by PnP InstanceId via the
    /// built-in <c>pnputil /remove-device</c>. If the command refuses removal,
    /// retry through SetupAPI instead of relying on the version-specific
    /// <c>/force</c> flag. Returns true on a successful removal. InstanceId
    /// comes from the whitelisted adapter-name lookup output, so it is bound to
    /// an adapter we own (VPNRouter-TUN / sing-box-tun-*).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task<bool> RunPnpUtilRemoveAsync(
        ILogger? logger, string instanceId, string adapterName, string context)
    {
        var (exit, _, err) = await RunAndCaptureAsync(
            "pnputil.exe",
            new[] { "/remove-device", instanceId },
            timeoutMs: 10000, logger: logger);

        if (exit == 0)
        {
            logger?.Information(
                "[TunDiag] {Ctx}: removed stale adapter '{Name}' device record via pnputil ({Id})",
                context, adapterName, instanceId);
            await WaitForNativePnpRemovalSettledAsync(logger, instanceId, adapterName, context)
                .ConfigureAwait(false);
            return true;
        }

        // Fall back to SetupAPI because /force is unavailable before Windows 11 22H2.
        logger?.Information(
            "[TunDiag] {Ctx}: pnputil /remove-device for '{Name}' ({Id}) failed " +
            "with exit {Exit}; retrying through Windows SetupAPI: '{Err}'",
            context, adapterName, instanceId, exit, (err ?? string.Empty).Trim());
        return await RunNativePnpRemoveAsync(logger, instanceId, adapterName, context)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Spawn a child process, capture stdout/stderr, return exit code.
    /// Bounded by <paramref name="timeoutMs"/> — kills the process on
    /// timeout so a hung netsh / PowerShell can't stall pre-start.
    /// Errors swallowed and surfaced as exit code -1.
    ///
    /// <para>Phase 3+ (2026-05-21): routed through the static
    /// <see cref="Runner"/> seam. Pre-migration this method accepted a
    /// single shell-style <c>arguments</c> string; post-migration we
    /// accept a pre-split argv array so the call sites (which already
    /// know their token boundaries) skip the round-trip through a shell
    /// arg parser. <see cref="ProcessRunner"/>'s
    /// <see cref="ProcessStartInfo.ArgumentList"/> path re-serializes each
    /// argv token for the kernel, yielding byte-equivalent command lines
    /// to what CreateProcess used to see.</para>
    /// </summary>
    private static async Task<(int exitCode, string stdout, string stderr)> RunAndCaptureAsync(
        string fileName, IReadOnlyList<string> arguments, int timeoutMs, ILogger? logger)
    {
        try
        {
            var result = await Runner.RunAsync(new ProcessRequest(
                ExecutablePath: fileName,
                Arguments: arguments,
                Timeout: TimeSpan.FromMilliseconds(timeoutMs))).ConfigureAwait(false);

            if (result.TimedOut)
            {
                // Pre-migration: on timeout we issued Kill(entireProcessTree:true)
                // and returned (-1, captured stdout, captured stderr).
                // IProcessRunner already kills on timeout (entireProcessTree:true
                // — see ProcessRunner.TryKill); we just surface -1 to keep the
                // exit-code contract identical for downstream callers.
                return (-1, result.Stdout, result.Stderr);
            }

            return (result.ExitCode, result.Stdout, result.Stderr);
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[TunDiag] command '{Cmd} {Args}' threw", fileName, string.Join(' ', arguments));
            return (-1, string.Empty, string.Empty);
        }
    }
}

internal sealed class TunAdapterNotReadyException : Exception
{
    public TunAdapterNotReadyException(string message, string? instanceId = null) : base(message)
    {
        InstanceId = instanceId;
    }

    public string? InstanceId { get; }
}
