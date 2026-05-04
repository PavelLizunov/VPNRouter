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

        // PowerShell one-liner that downloads install.ps1 to TEMP and
        // executes it. Mirrors the public one-liner exactly so any user
        // who manually runs the curl-pipe sees the same install path.
        // The wrapping single quotes inside the -Command string need
        // doubling per PowerShell rules.
        var bootstrap =
            "$ErrorActionPreference = 'Stop'; " +
            "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; " +
            "$tmp = Join-Path $env:TEMP 'vpnr-repair.ps1'; " +
            $"Invoke-WebRequest -Uri '{InstallScriptUrl}' -OutFile $tmp -UseBasicParsing; " +
            "& $tmp";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{bootstrap}\"",
            UseShellExecute = true,
            // Show the window so users see something is happening — a
            // silent self-repair that takes 30+ seconds with no UI looks
            // worse than a small PowerShell progress window. Plus if the
            // download fails the user can read the error.
            WindowStyle = ProcessWindowStyle.Normal,
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
