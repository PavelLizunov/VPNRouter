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
/// </summary>
public static class TunAdapterDiagnostics
{
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
            var psi = new ProcessStartInfo("netsh", "interface show interface")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

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
            var psi = new ProcessStartInfo("netsh",
                $"interface set interface name=\"{interfaceName}\" admin=disabled")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                logger?.Warning("[TunDiag] {Ctx}: failed to spawn netsh for adapter disable", context);
                return;
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(3000);

            // netsh exit codes: 0 = success, 1 = "element not found"
            // (adapter already gone — fine), other = real failure.
            if (proc.ExitCode == 0)
            {
                logger?.Information(
                    "[TunDiag] {Ctx}: disabled orphaned adapter '{Iface}' (network stack should release routes)",
                    context, interfaceName);
            }
            else if (proc.ExitCode == 1
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
                    context, interfaceName, proc.ExitCode, stdout.Trim(), stderr.Trim());
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[TunDiag] {Ctx}: disable orphaned adapter '{Iface}' failed (non-fatal)", context, interfaceName);
        }
    }

    /// <summary>
    /// <b>[LEGACY — DO NOT USE IN LAUNCH PATHS.]</b> Pre-enabled the
    /// wintun adapter so sing-box could open it. Worked around a
    /// v2.30.2-r1 FATAL ("The device is not ready for use") that arose
    /// when a prior <see cref="DisableOrphanedAdapter"/> cleanup had
    /// left the adapter in "Admin disabled" state.
    ///
    /// <para><b>Why deprecated (2026-05-19, hotfix shipped in v2.35.0):</b>
    /// sing-box 1.13.x doesn't OPEN existing adapters, it CREATES them
    /// via <c>WintunCreateAdapter</c>. Re-enabling the disabled adapter
    /// just restores its name reservation — the next
    /// <c>WintunCreateAdapter</c> call then refuses with
    /// ERROR_FILE_EXISTS:
    /// <c>configure tun interface: Cannot create a file when that file
    /// already exists</c>. Field log evidence
    /// (<c>Z:/alicemoren1991/vpnrouter20260519.log</c>) showed the
    /// auto-restart loop crashing on every iteration because of this.
    /// The proper fix is <see cref="PreStartCleanupAsync"/>, which
    /// disables + <i>removes</i> the device record so sing-box's create
    /// call hits a clean slate.</para>
    ///
    /// <para>Method body retained so any external references compile,
    /// but no production code in this repo calls it any more. Callers
    /// in worktrees / branches may still reference it — they should
    /// migrate to <see cref="PreStartCleanupAsync"/> (enumerate-and-clean)
    /// or <see cref="TryRemoveAdapterAsync"/> (direct-by-name) on next
    /// rebase.</para>
    /// </summary>
    [Obsolete("Replaced by PreStartCleanupAsync — pre-enable does not solve sing-box's WintunCreateAdapter ERROR_FILE_EXISTS. See v2.35.0 hotfix brief plans/hotfix-tun-adapter-orphan-pre-enable-2026-05-19.md.", error: false)]
    [SupportedOSPlatform("windows")]
    public static void EnsureAdapterEnabledOrAbsent(ILogger? logger, string interfaceName, string context)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (string.IsNullOrWhiteSpace(interfaceName)) return;

        try
        {
            var psi = new ProcessStartInfo("netsh",
                $"interface set interface name=\"{interfaceName}\" admin=enabled")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                logger?.Debug("[TunDiag] {Ctx}: failed to spawn netsh for pre-enable check", context);
                return;
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(3000);

            if (proc.ExitCode == 0)
            {
                logger?.Information(
                    "[TunDiag] {Ctx}: pre-enabled adapter '{Iface}' (was disabled or already enabled)",
                    context, interfaceName);
            }
            else if (proc.ExitCode == 1
                     || stdout.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                     || stderr.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                logger?.Debug(
                    "[TunDiag] {Ctx}: adapter '{Iface}' not present — sing-box will create it",
                    context, interfaceName);
            }
            else
            {
                logger?.Debug(
                    "[TunDiag] {Ctx}: netsh enable for '{Iface}' returned exit {Code}: stdout='{Out}' stderr='{Err}'",
                    context, interfaceName, proc.ExitCode, stdout.Trim(), stderr.Trim());
            }
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[TunDiag] {Ctx}: pre-enable check for '{Iface}' failed (non-fatal)", context, interfaceName);
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
    /// then remove the device via <c>powershell Remove-NetAdapter</c>
    /// (actually deletes the device record). PowerShell is safe here
    /// because we're pre-launch — sing-box isn't holding any handles, so
    /// the "Remove-NetAdapter refuses while open" gotcha cited in
    /// <see cref="DisableOrphanedAdapter"/>'s docs doesn't apply.</para>
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
    /// <para>Returns the count of adapters successfully removed so the
    /// caller can decide whether to insert a settle delay before the next
    /// sing-box launch (Windows network-stack teardown takes a beat).
    /// Linux/macOS: returns 0 immediately. Errors are swallowed —
    /// pre-start cleanup is best-effort and must never block start.</para>
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
                "netsh", "interface show interface", timeoutMs: 5000, logger: logger);

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

                    // Disable first — frees the wintun kernel handle so the
                    // subsequent Remove-NetAdapter actually succeeds. Already
                    // idempotent; "not found" is treated as success.
                    DisableOrphanedAdapter(logger, adapter, context);

                    if (await TryRemoveAdapterAsync(logger, adapter, context))
                        removed++;
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
                DisableOrphanedAdapter(logger, DefaultTunInterfaceName, context);
                if (await TryRemoveAdapterAsync(logger, DefaultTunInterfaceName, context))
                    removed++;
            }

            logger?.Information(
                "[TunDiag] {Ctx}: pre-start cleanup: removed {Removed} TUN adapter(s) total (enumeration + direct fallback)",
                context, removed);

            return removed;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[TunDiag] {Ctx}: pre-start cleanup failed (non-fatal)", context);
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
    /// Best-effort device removal via PowerShell <c>Remove-NetAdapter</c>.
    /// Returns true on exit 0 (adapter gone) or "adapter not found"
    /// (already gone — same end state, counts as success).
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
    /// </summary>
    private static int s_removeNetAdapterMissing; // 0 = unknown, 1 = confirmed missing

    [SupportedOSPlatform("windows")]
    internal static async Task<bool> TryRemoveAdapterAsync(
        ILogger? logger, string adapterName, string context)
    {
        // BR-2 fast-fail: if a previous call already observed the cmdlet
        // missing, skip the PowerShell round-trip. Saves ~600ms × every
        // cleanup site × every connect attempt.
        if (Volatile.Read(ref s_removeNetAdapterMissing) == 1)
        {
            logger?.Debug(
                "[TunDiag] {Ctx}: skipping Remove-NetAdapter for '{Name}' " +
                "(cmdlet was missing on first probe; cached for process lifetime)",
                context, adapterName);
            return false;
        }

        try
        {
            // Embed adapterName via single-quoted PowerShell string. The
            // whitelist regex in ExtractStaleAdapterNames restricts names
            // to [A-Za-z0-9_-] characters, so single-quote injection is
            // not a concern here — there's no apostrophe path.
            var script =
                $"Get-NetAdapter -Name '{adapterName}' -ErrorAction SilentlyContinue | " +
                "Remove-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue";

            var (exitCode, stdout, stderr) = await RunAndCaptureAsync(
                "powershell.exe",
                $"-NoProfile -NonInteractive -Command \"{script}\"",
                timeoutMs: 10000, logger: logger);

            if (exitCode == 0)
            {
                logger?.Information(
                    "[TunDiag] {Ctx}: removed stale adapter '{Name}' via Remove-NetAdapter",
                    context, adapterName);
                return true;
            }

            // BR-2 cmdlet-missing latch: stderr from powershell.exe when
            // a cmdlet is missing looks like
            //   "Remove-NetAdapter : The term 'Remove-NetAdapter' is not
            //    recognized as the name of a cmdlet, function, ..."
            // The substring "is not recognized as the name of a cmdlet"
            // is locale-EN; the localised Russian/German/etc. variants
            // also contain the cmdlet name literal so we match on the
            // English phrase OR on the literal name appearing after
            // "term '" — robust to either locale.
            var stderrText = stderr ?? string.Empty;
            var stdoutText = stdout ?? string.Empty;
            var looksLikeCmdletMissing =
                stderrText.IndexOf("is not recognized", StringComparison.OrdinalIgnoreCase) >= 0
                || stderrText.IndexOf("не распознано", StringComparison.OrdinalIgnoreCase) >= 0
                || stderrText.IndexOf("nicht erkannt", StringComparison.OrdinalIgnoreCase) >= 0
                || stdoutText.IndexOf("is not recognized", StringComparison.OrdinalIgnoreCase) >= 0;
            if (looksLikeCmdletMissing
                && Interlocked.CompareExchange(ref s_removeNetAdapterMissing, 1, 0) == 0)
            {
                // First-time observation — log once at Information level so
                // the user/ops can see the environment limitation. All
                // subsequent calls log at Debug only.
                logger?.Information(
                    "[TunDiag] {Ctx}: Remove-NetAdapter cmdlet not available in this " +
                    "PowerShell environment (NetAdapter module missing). " +
                    "Skipping direct-by-name fallback for the rest of this process — " +
                    "netsh enumeration cleanup still runs. Adapter '{Name}' left untouched.",
                    context, adapterName);
                return false;
            }

            logger?.Warning(
                "[TunDiag] {Ctx}: Remove-NetAdapter for '{Name}' returned exit {Exit}: stdout='{Out}' stderr='{Err}'",
                context, adapterName, exitCode, stdoutText.Trim(), stderrText.Trim());
            return false;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex,
                "[TunDiag] {Ctx}: Remove-NetAdapter for '{Name}' threw (non-fatal)",
                context, adapterName);
            return false;
        }
    }

    /// <summary>
    /// Spawn a child process, capture stdout/stderr, return exit code.
    /// Bounded by <paramref name="timeoutMs"/> — kills the process on
    /// timeout so a hung netsh / PowerShell can't stall pre-start.
    /// Errors swallowed and surfaced as exit code -1.
    /// </summary>
    private static async Task<(int exitCode, string stdout, string stderr)> RunAndCaptureAsync(
        string fileName, string arguments, int timeoutMs, ILogger? logger)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (-1, string.Empty, string.Empty);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-1, await stdoutTask, await stderrTask);
            }

            return (proc.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[TunDiag] command '{Cmd} {Args}' threw", fileName, arguments);
            return (-1, string.Empty, string.Empty);
        }
    }
}
