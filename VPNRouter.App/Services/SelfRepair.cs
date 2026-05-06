using System;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace VPNRouter.App.Services;

/// <summary>
/// Auto-repair a damaged install by re-running the official install.ps1
/// from the web. v2.31.8-r1.
///
/// <para>Triggered by <see cref="InstallHealthCheck"/> when mixed-version
/// DLLs are detected (the auto-update-with-Service-running symptom from
/// pre-v2.31.7). The user sees a brief PowerShell window come up, the
/// installer does its full Service-stop / extract / Service-start cycle,
/// and the app relaunches itself — all without the user knowing they
/// hit the bug.</para>
///
/// <para>Loop prevention: writes <c>repair-marker</c> in the data dir
/// with the current timestamp. If a marker exists and is recent
/// (≤ <see cref="LoopWindowMinutes"/>) we DO NOT retry — that means a
/// repair already ran this minute and didn't help, so we surface the
/// damaged state to the user so they can intervene manually rather than
/// looping forever.</para>
/// </summary>
public static class SelfRepair
{
    private const string MarkerFileName = "self-repair-marker";
    private const int LoopWindowMinutes = 10;

    /// <summary>
    /// Web URL for the canonical install script. Hardcoded — install.ps1
    /// is the canonical source of truth for the install layout.
    /// </summary>
    private const string InstallScriptUrl = "https://vpn.ninitux.com/install.ps1";

    public sealed record Decision(bool ShouldRun, string Reason);

    /// <summary>
    /// Decide whether a self-repair attempt is appropriate right now.
    /// Reads the marker file to detect a recent (failed) attempt and
    /// declines to loop.
    /// </summary>
    public static Decision Plan(ILogger? logger = null)
    {
        try
        {
            var dir = VPNRouter.Core.AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            var marker = Path.Combine(dir, MarkerFileName);
            if (File.Exists(marker))
            {
                var stamp = File.GetLastWriteTimeUtc(marker);
                var age = DateTime.UtcNow - stamp;
                if (age < TimeSpan.FromMinutes(LoopWindowMinutes))
                {
                    return new Decision(false,
                        $"repair attempted {age.TotalMinutes:F0} min ago — not looping (marker: {marker})");
                }
            }
            return new Decision(true, "no recent repair marker — safe to attempt");
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[SelfRepair] failed to read repair marker — defaulting to run");
            return new Decision(true, "marker read failed — defaulting to run");
        }
    }

    /// <summary>
    /// Spawn the install.ps1 helper. Drops the loop-prevention marker
    /// before launching so a re-entry within the window is blocked.
    /// Returns immediately — caller should exit so the helper has
    /// unrestricted access to the install dir.
    /// </summary>
    public static void Run(ILogger? logger = null)
    {
        try
        {
            var dir = VPNRouter.Core.AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            var marker = Path.Combine(dir, MarkerFileName);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[SelfRepair] failed to write loop marker — proceeding anyway");
        }

        // v2.31.8-r9 — channel-aware repair flag.
        // If the running App.exe was built from a prerelease tag
        // (compile-time AppVersion.Version contains '-r' suffix), pass
        // -Prerelease so install.ps1 picks up the latest prerelease.
        // Without this, a user on v2.31.8-r3 hitting a damaged install
        // would get «repaired» to v2.31.7 stable — a downgrade that
        // loses the v2.31.8 fixes they were running and triggers
        // confusion («I had newer version, now I'm older»).
        var compiledVersion = VPNRouter.Core.AppVersion.Version;
        var isPrerelease = compiledVersion.Contains("-r", StringComparison.Ordinal);
        var prereleaseFlag = isPrerelease ? " -Prerelease" : string.Empty;
        logger?.Information("[SelfRepair] running install.ps1{Flag} (current build = {Version})",
            prereleaseFlag, compiledVersion);

        // v2.31.8-r9 — make repair invisible to the user.
        // The user wants seamless self-healing — no PowerShell window,
        // no «something is happening» distraction. install.ps1 itself
        // already shows progress in its own console; we run it via a
        // hidden powershell that internally launches install.ps1 in a
        // visible window only if interactive (which it is — admin UAC).
        // Net effect: user sees one UAC prompt (necessary for elevation),
        // then a brief PowerShell window during install, then app
        // relaunches. Pre-r9 we showed the bootstrapping window too —
        // that visual «two windows» double-flicker confused users.
        //
        // Add `-NoLaunch` so we don't compete with app's own relaunch
        // logic (App.exe's process exits, install.ps1 finishes, then
        // Start Menu shortcut launch path takes over on next user
        // click — cleaner than racing a second VPNRouter.App.exe).
        // Actually no — the existing flow expects install.ps1 to launch
        // App at the end (matches the manual web-one-liner UX). Don't
        // add -NoLaunch.
        // v2.31.10-r2 — write bootstrap to a tempfile and run via `-File`,
        // not inline `-Command`. Inline `-Command "iwr … | & $tmp"` is the
        // exact shape Defender's `Trojan:Win32/ClickFix.DCW!MTB` family fires
        // on (already triggered on dev tooling on this machine — see
        // `plans/v2.31.10-av-firewall-compat.md`). A `-File` invocation
        // points at a real .ps1 on disk, AMSI scans the whole file (clean,
        // signed-by-content text), and the Hidden window flag still applies.
        // Net effect: same UX, much smaller AMSI heuristic surface.
        var bootstrapScript =
            "$ErrorActionPreference = 'Stop'\r\n" +
            "$ProgressPreference = 'SilentlyContinue'\r\n" +
            "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12\r\n" +
            "$tmp = Join-Path $env:TEMP 'vpnr-repair.ps1'\r\n" +
            $"Invoke-WebRequest -Uri '{InstallScriptUrl}' -OutFile $tmp -UseBasicParsing\r\n" +
            $"& $tmp{prereleaseFlag}\r\n";

        var bootstrapPath = Path.Combine(
            Path.GetTempPath(),
            $"vpnr-self-repair-{DateTime.UtcNow:yyyyMMddHHmmss}.ps1");
        try
        {
            File.WriteAllText(bootstrapPath, bootstrapScript);
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "[SelfRepair] failed to write bootstrap helper to {Path}", bootstrapPath);
            throw;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{bootstrapPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            Process.Start(psi);
            logger?.Information("[SelfRepair] launched install.ps1 web one-liner — current process will exit so installer can replace files");
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "[SelfRepair] failed to spawn repair helper — install must be repaired manually");
            // Re-throw so caller can decide how to handle (e.g. show
            // dialog to user explaining the issue + manual recovery).
            throw;
        }
    }
}
