using System.Diagnostics;
using VPNRouter.Core;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private const string SudoersFormatMarker = "# vpnrouter 2026-09-02 sudoers (sing-box + exact kill + networksetup DNS + pfctl kill-switch)";

    private void EnsureMacSudoAccess()
    {
        const string sudoersPath = "/etc/sudoers.d/vpnrouter";

        var sudoersMarkerPath = Path.Combine(AppPaths.DataDir, "macos-sudoers.marker");
        bool needsRewrite = true;
        try
        {
            if (File.Exists(sudoersMarkerPath) &&
                File.ReadAllText(sudoersMarkerPath).Contains(SudoersFormatMarker, StringComparison.Ordinal))
            {
                needsRewrite = false;
            }
            else if (File.Exists(sudoersPath))
            {
                try
                {
                    if (File.ReadAllText(sudoersPath).Contains(SudoersFormatMarker, StringComparison.Ordinal))
                        needsRewrite = false;
                }
                catch { }
            }
        }
        catch { needsRewrite = true; }
        if (!needsRewrite) return;

        StatusText = IsRussian ? "Настройка sudo (один раз)..." : "Setting up sudo (one-time)...";

        var user = Environment.UserName;
        var singbox = AppPaths.SingBoxExePath;
        var singboxEscaped = singbox.Replace(" ", "\\ ");
        var tmpFile = Path.Combine(Path.GetTempPath(), "vpnrouter-sudoers");
        File.WriteAllText(tmpFile,
            $"{SudoersFormatMarker}\n" +
            $"{user} ALL=(root) NOPASSWD: {singboxEscaped} *\n" +
            $"{user} ALL=(root) NOPASSWD: /bin/kill -KILL -- [0-9]*\n" +
            $"{user} ALL=(root) NOPASSWD: /usr/sbin/networksetup *\n" +
            $"{user} ALL=(root) NOPASSWD: /usr/bin/dscacheutil *\n" +
            $"{user} ALL=(root) NOPASSWD: /usr/bin/killall -HUP mDNSResponder\n" +
            $"{user} ALL=(root) NOPASSWD: /sbin/pfctl *\n");

        var helperScript = Path.Combine(Path.GetTempPath(), "vpnrouter-setup.sh");
        File.WriteAllText(helperScript,
            $"#!/bin/bash\ncp \"{tmpFile}\" {sudoersPath}\nchmod 0440 {sudoersPath}\nchown root:wheel {sudoersPath}\nrm -f \"{tmpFile}\" \"{helperScript}\"\n");
        File.SetUnixFileMode(helperScript,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var cmd = $"\\\"{helperScript}\\\"";
        var psi = new ProcessStartInfo("/usr/bin/osascript")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add($"do shell script \"{cmd}\" with administrator privileges");

        _logger.Information("Running osascript for sudo setup...");
        var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null)
        {
            _logger.Error("Failed to start osascript");
            return;
        }

        var stderr = proc.StandardError.ReadToEnd();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60000);
        var osascriptExit = proc.HasExited ? proc.ExitCode : -1;

        _logger.Information("osascript exit={Exit} stdout={Out} stderr={Err}",
            osascriptExit, stdout, stderr);
        proc.Dispose();

        if (osascriptExit != 0)
        {
            _logger.Warning("sudoers setup: osascript exit {Exit} (cancelled/failed) — NOT writing marker; will re-prompt next time", osascriptExit);
            return;
        }
        if (!File.Exists(sudoersPath))
        {
            _logger.Warning("Failed to configure sudoers (file absent after a successful osascript?)");
            return;
        }
        if (!ProbeSudoGrant())
        {
            _logger.Warning("sudoers setup: pfctl grant probe failed after osascript — NOT writing marker; will re-prompt");
            return;
        }

        _logger.Information("Passwordless sudo configured + probed");
        try { File.WriteAllText(sudoersMarkerPath, SudoersFormatMarker); }
        catch (Exception ex) { _logger.Warning(ex, "Failed to write sudoers marker — may re-prompt next launch"); }
    }

    private static bool ProbeSudoGrant()
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/sudo")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("/sbin/pfctl");
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add("info");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
