using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class SingBoxManager
{
    /// <summary>
    /// v2.29.0-r5 — Unix stop escalation chain. Tries kill methods from
    /// least-privileged to most-privileged, verifying after each step that
    /// sing-box is actually gone. Shared by Linux and macOS; the platform
    /// controls which steps run.
    ///
    /// <para>Steps (Linux runs all three; macOS runs only Step 3):</para>
    /// <list type="number">
    /// <item>Plain user pkill (Linux only — works in capability mode + .deb
    ///   installs where sing-box runs as user via setcap CAP_NET_ADMIN).</item>
    /// <item>pkexec pkill -KILL (Linux only — polkit GUI prompt; SIGKILL
    ///   bypasses any signal mask sing-box might have set; pkexec does not
    ///   exist on macOS).</item>
    /// <item>sudo pkill -KILL (NOPASSWD if sudoers entry was set up at
    ///   first Connect; falls through if not configured). This is the
    ///   PRIMARY path on macOS, where sing-box runs as root via sudoers.</item>
    /// </list>
    ///
    /// <para>v2.40.x (Fix #8, macOS deep-audit): Steps 1-2 are gated behind
    /// <c>OperatingSystem.IsLinux()</c>. On macOS a user-level pkill cannot
    /// signal the root sing-box and pkexec is absent, so both steps were
    /// guaranteed-failing no-ops that wasted ~1.3 s of sleeps and emitted a
    /// misleading "escalating to pkexec" WARN on every disconnect. macOS goes
    /// straight to Step 3, which is the only step that has ever worked there.</para>
    ///
    /// <para>Each attempt is followed by IsSingBoxAlive() check (Clash API
    /// probe + pgrep) so we know immediately if it worked. Logs each step
    /// for postmortem.</para>
    /// </summary>
    private void LinuxStopEscalationChain()
    {
        // Steps 1-2 only apply on Linux. On macOS sing-box runs as root and
        // neither a user pkill nor pkexec can touch it — skip straight to the
        // sudo path (Step 3) instead of burning a sleep + a failing pkexec spawn.
        if (OperatingSystem.IsLinux())
        {
            // Step 1: plain user pkill. Cheap and works in capability mode.
            if (TrySpawnAndWait("/usr/bin/pkill", "-TERM -f sing-box", 3000, "user pkill -TERM"))
            {
                // Wait briefly for graceful exit (sing-box on SIGTERM should
                // tear down TUN cleanly within ~1 s).
                System.Threading.Thread.Sleep(800);
                if (!IsSingBoxAlive())
                {
                    _logger.Information("[SingBoxManager] Linux stop: user pkill -TERM succeeded");
                    return;
                }
            }

            _logger.Information("[SingBoxManager] Linux stop: user pkill didn't kill sing-box, escalating to pkexec");

            // Step 2: pkexec with SIGKILL. GUI prompt — user might dismiss.
            if (TrySpawnAndWait("/usr/bin/pkexec", "pkill -KILL -f sing-box", 30000, "pkexec pkill -KILL"))
            {
                System.Threading.Thread.Sleep(500);
                if (!IsSingBoxAlive())
                {
                    _logger.Information("[SingBoxManager] Linux stop: pkexec pkill -KILL succeeded");
                    return;
                }
            }

            _logger.Warning("[SingBoxManager] Linux stop: pkexec didn't kill sing-box, trying sudo");
        }

        // Step 3: sudo with -n (non-interactive — fail if password needed
        // rather than block forever). If user set up NOPASSWD sudoers, this
        // works without prompt; otherwise it fails fast and we give up
        // (better to surface the failure than hang forever).
        if (TrySpawnAndWait("/usr/bin/sudo", "-n pkill -KILL -f sing-box", 5000, "sudo -n pkill -KILL"))
        {
            System.Threading.Thread.Sleep(500);
            if (!IsSingBoxAlive())
            {
                _logger.Information("[SingBoxManager] Unix stop: sudo -n pkill -KILL succeeded");
                return;
            }
        }

        if (IsSingBoxAlive())
        {
            // Cause list is platform-specific: macOS only ever reaches Step 3,
            // so a failure there is a missing/broken sudoers NOPASSWD grant.
            var causes = OperatingSystem.IsMacOS()
                ? "sudoers NOPASSWD not set up (re-grant via the app's mac sudo prompt); " +
                  "sing-box running under a uid we can't sudo-kill."
                : "pkexec/polkit agent not installed; sudoers NOPASSWD not set up; " +
                  "sing-box running under a different uid we can't kill.";
            _logger.Error("[SingBoxManager] Unix stop: ALL escalation steps failed — sing-box still alive. " +
                          "Manual intervention required: `sudo pkill -KILL -f sing-box`. " +
                          "Possible causes: " + causes);
        }
    }

    /// <summary>v2.29.0-r5: spawn an external process, wait, return true
    /// iff exit code 0. Used by Linux stop escalation chain. Errors logged
    /// but never thrown.</summary>
    private bool TrySpawnAndWait(string fileName, string args, int timeoutMs, string label)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null)
            {
                _logger.Warning("[SingBoxManager] Linux stop: {Label} — Process.Start returned null", label);
                return false;
            }
            if (!p.WaitForExit(timeoutMs))
            {
                _logger.Warning("[SingBoxManager] Linux stop: {Label} timed out after {Ms} ms", label, timeoutMs);
                try { p.Kill(true); } catch { }
                return false;
            }
            _logger.Information("[SingBoxManager] Linux stop: {Label} exit={Code}", label, p.ExitCode);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SingBoxManager] Linux stop: {Label} threw", label);
            return false;
        }
    }

    /// <summary>v2.29.0-r5: check if sing-box is still running.
    /// Two-signal test: Clash API at 127.0.0.1:9090 + pgrep -f sing-box.
    /// Returns true if EITHER signal says alive (defensive — false
    /// negative on Clash API alone could leave a zombie).</summary>
    private bool IsSingBoxAlive()
    {
        if (IsClashApiAlive()) return true;
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/pgrep", "-f sing-box")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(2000)) { try { p.Kill(true); } catch { } return false; }
            // pgrep exit 0 = found at least one process; 1 = none.
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
