using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using VPNRouter.Core.Models;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Core.Services;

public partial class UpdateChecker
{
    public void ApplyUpdate(string extractedDir) =>
        ApplyUpdate(extractedDir, writeInstallReceipt: true);

    private void ApplyUpdate(string extractedDir, bool writeInstallReceipt)
    {
        if (OperatingSystem.IsMacOS())
            ApplyUpdateMac(extractedDir);
        else if (OperatingSystem.IsLinux())
            ApplyUpdateLinux(extractedDir, writeInstallReceipt);
        else
            ApplyUpdateWindows(extractedDir, writeInstallReceipt);
    }

    private void ApplyUpdateWindows(string extractedDir, bool writeInstallReceipt)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd('\\');

        var appSubDir = Path.Combine(extractedDir, "app");
        if (Directory.Exists(appSubDir) &&
            (File.Exists(Path.Combine(appSubDir, "VPNRouter.GUI.exe")) ||
             File.Exists(Path.Combine(appSubDir, "VPNRouter.GUI.dll"))))
        {
            extractedDir = appSubDir;
        }

        var bootstrapSubDir = Path.Combine(extractedDir, "_bootstrap");
        if (Directory.Exists(bootstrapSubDir) &&
            File.Exists(Path.Combine(extractedDir, "VPNRouter.GUI.exe")))
        {
            try
            {
                File.Copy(
                    Path.Combine(extractedDir, "VPNRouter.GUI.exe"),
                    Path.Combine(bootstrapSubDir, "VPNRouter.GUI.exe"),
                    overwrite: true);
            }
            catch { }
            extractedDir = bootstrapSubDir;
        }

        var guiExe = Path.Combine(appDir, "VPNRouter.GUI.exe");
        var parentPid = Environment.ProcessId;

        if (writeInstallReceipt)
            TryWriteInstallReceipt();

        var installDir = Path.GetDirectoryName(appDir.TrimEnd('\\')) ?? string.Empty;
        if (!string.IsNullOrEmpty(installDir))
        {
            try
            {
                var snap = UpdateBackup.CreateSnapshot(installDir);
                StatusChanged?.Invoke(snap.Success
                    ? "Backup created — update will be reversible if it fails."
                    : $"Backup creation skipped: {snap.Diagnostic}");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Backup creation threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        var tempDir = Path.GetTempPath();
        var helperPath = Path.Combine(tempDir, $"vpnrouter-update-{parentPid}.cmd");
        var logsDir = AppPaths.LogsDir;
        try { Directory.CreateDirectory(logsDir); } catch { }
        var helperLog = Path.Combine(logsDir, "update.log");

        var skipRelaunchForCi =
            string.Equals(Environment.GetEnvironmentVariable("VPNROUTER_CI"), "1",
                StringComparison.Ordinal);

        var cmd = string.Join("\r\n", new[]
        {
            "@echo off",
            "setlocal EnableDelayedExpansion",
            $"set \"LOG={helperLog}\"",
            $"set \"PARENT_PID={parentPid}\"",
            $"set \"SRC={extractedDir.TrimEnd('\\')}\"",
            $"set \"DST={appDir}\"",
            "echo [%TIME%] vpnrouter-update helper start, parent=%PARENT_PID% >>\"%LOG%\"",
            "set /a TRIES=0",
            ":waitloop",
            "tasklist /FI \"PID eq %PARENT_PID%\" 2>nul | find \"%PARENT_PID%\" >nul",
            "if errorlevel 1 goto parentgone",
            "set /a TRIES+=1",
            "if !TRIES! gtr 30 (",
            "  echo [%TIME%] parent %PARENT_PID% still alive after 30 s, proceeding anyway >>\"%LOG%\"",
            "  goto parentgone",
            ")",
            "ping -n 2 127.0.0.1 >nul",
            "goto waitloop",
            ":parentgone",
            "echo [%TIME%] parent gone, checking VPNRouter Windows Service >>\"%LOG%\"",
            "set \"SVC_WAS_RUNNING=0\"",
            "sc query VPNRouter >nul 2>&1",
            "if errorlevel 1 (",
            "  echo [%TIME%] VPNRouter Service not installed >>\"%LOG%\"",
            ") else (",
            "  sc query VPNRouter | find \"RUNNING\" >nul",
            "  if errorlevel 1 (",
            "    echo [%TIME%] VPNRouter Service installed but not RUNNING — leaving alone >>\"%LOG%\"",
            "  ) else (",
            "    set \"SVC_WAS_RUNNING=1\"",
            "    echo [%TIME%] VPNRouter Service RUNNING — stopping for file copy >>\"%LOG%\"",
            "    echo [%TIME%] disabling Service failure recovery during update >>\"%LOG%\"",
            "    sc failure VPNRouter reset= 0 actions= \"\" >>\"%LOG%\" 2>&1",
            "    sc stop VPNRouter >>\"%LOG%\" 2>&1",
            "    call :wait_service_stop",
            "    taskkill /IM VPNRouter.Service.exe /F >nul 2>&1",
            "    echo [%TIME%] Service process cleared for file copy >>\"%LOG%\"",
            "  )",
            ")",
            "echo [%TIME%] killing sing-box and copying files >>\"%LOG%\"",
            "taskkill /IM sing-box.exe /F >nul 2>&1",
            "ping -n 2 127.0.0.1 >nul",
            "xcopy \"%SRC%\\*\" \"%DST%\\\" /E /Y /Q /R /I >>\"%LOG%\" 2>&1",
            "set XCOPY_EXIT=!ERRORLEVEL!",
            "echo [%TIME%] xcopy exit=!XCOPY_EXIT! >>\"%LOG%\"",
            "if not \"!XCOPY_EXIT!\"==\"0\" (",
            "  echo [%TIME%] xcopy exit !XCOPY_EXIT! — writing .update-failed marker into %DST% >>\"%LOG%\"",
            "  > \"%DST%\\.update-failed\" echo xcopy exit=!XCOPY_EXIT! at %DATE% %TIME%",
            ")",
            "if \"!SVC_WAS_RUNNING!\"==\"1\" (",
            "  echo [%TIME%] restarting VPNRouter Service >>\"%LOG%\"",
            "  sc start VPNRouter >>\"%LOG%\" 2>&1",
            "  echo [%TIME%] restoring Service failure recovery actions >>\"%LOG%\"",
            "  sc failure VPNRouter reset= 86400 actions= restart/60000/restart/60000/restart/60000 >>\"%LOG%\" 2>&1",
            ")",
            skipRelaunchForCi
                ? "echo [%TIME%] VPNROUTER_CI=1 — skipping GUI relaunch (CI integration test mode) >>\"%LOG%\""
                : "echo [%TIME%] launching new VPNRouter.GUI.exe >>\"%LOG%\"",
            skipRelaunchForCi
                ? "rem CI mode: relaunch suppressed"
                : $"start \"\" \"{guiExe}\"",
            "echo [%TIME%] helper done >>\"%LOG%\"",
            "del /Q \"%~f0\" >nul 2>&1",
            "exit /b 0",
            "",
            ":wait_service_stop",
            "set /a SVC_TRIES=0",
            ":svcstoploop",
            "sc query VPNRouter | find \"STOPPED\" >nul",
            "if not errorlevel 1 goto :eof",
            "set /a SVC_TRIES+=1",
            "if !SVC_TRIES! gtr 10 (",
            "  echo [%TIME%] Service still not STOPPED after 10 s — forcing process exit >>\"%LOG%\"",
            "  goto :eof",
            ")",
            "ping -n 2 127.0.0.1 >nul",
            "goto svcstoploop",
        });

        File.WriteAllText(helperPath, cmd);

        var argString = skipRelaunchForCi
            ? $"/c \"\"{helperPath}\" > \"{Path.Combine(logsDir, "helper-stderr.log")}\" 2>&1\""
            : $"/c \"{helperPath}\"";

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = argString,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

    }

    private static void ApplyUpdateMac(string extractedDir)
    {
        var stagedApp = Directory.GetDirectories(extractedDir, "*.app", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (stagedApp == null)
            throw new InvalidOperationException(
                "Mac update ZIP does not contain a .app bundle at its top level. " +
                "Re-package the release with build-mac.sh or verify the downloaded asset.");

        var targetApp = FindCurrentAppBundle()
            ?? throw new InvalidOperationException("Cannot locate current .app bundle.");

        var pid       = Environment.ProcessId;
        var logPath   = $"/tmp/vpnrouter-update-{pid}.log";
        var scriptPath = $"/tmp/vpnrouter-update-{pid}.sh";

        var safeLogPath = EscapeShellArgument(logPath);
        var safeStagedApp = EscapeShellArgument(stagedApp);
        var safeTargetApp = EscapeShellArgument(targetApp);

        var script =
            "#!/bin/bash\n" +
            $"exec >'{safeLogPath}' 2>&1\n" +
            "set +e\n" +
            "ts() { date '+%Y-%m-%dT%H:%M:%S%z'; }\n" +
            "log() { echo \"[$(ts)] $*\"; }\n" +
            "log '── VPNRouter macOS updater ──'\n" +
            $"log 'Old PID: {pid}'\n" +
            $"log 'Staged:  {safeStagedApp}'\n" +
            $"log 'Target:  {safeTargetApp}'\n" +
            $"for i in $(seq 1 75); do\n" +
            $"  if ! kill -0 {pid} 2>/dev/null; then break; fi\n" +
            "  sleep 0.2\n" +
            "done\n" +
            $"if kill -0 {pid} 2>/dev/null; then\n" +
            $"  log 'Old process {pid} did not exit within 15s — forcing SIGTERM'\n" +
            $"  kill {pid} 2>/dev/null\n" +
            "  sleep 1\n" +
            "fi\n" +
            "sleep 0.5\n" +
            $"xattr -dr com.apple.quarantine '{safeStagedApp}' 2>/dev/null\n" +
            "log 'Stripped quarantine from staging'\n" +
            $"BACKUP='{safeTargetApp}.old-{pid}'\n" +
            $"if [ -d '{safeTargetApp}' ]; then\n" +
            $"  mv '{safeTargetApp}' \"$BACKUP\" && log 'Backed up old bundle to '\"$BACKUP\" || {{ log 'FAIL: mv old bundle aside'; exit 10; }}\n" +
            "fi\n" +
            $"ditto --rsrc '{safeStagedApp}' '{safeTargetApp}' || {{ log 'FAIL: ditto copy'; if [ -d \"$BACKUP\" ]; then rm -rf '{safeTargetApp}' && mv \"$BACKUP\" '{safeTargetApp}'; fi; exit 11; }}\n" +
            "log 'Installed new bundle via ditto'\n" +
            $"xattr -dr com.apple.quarantine '{safeTargetApp}' 2>/dev/null\n" +
            "log 'Stripped quarantine from target'\n" +
            $"chmod -R +x '{safeTargetApp}/Contents/MacOS' 2>/dev/null\n" +
            "log 'chmod +x on MacOS/'\n" +
            "rm -rf \"$BACKUP\" 2>/dev/null\n" +
            $"open '{safeTargetApp}' && log 'Launched new bundle' || log 'WARN: open exited non-zero'\n" +
            "log 'Done.'\n";

        File.WriteAllText(scriptPath, script);

        try
        {
            var chmodPsi = new ProcessStartInfo("/bin/chmod") { UseShellExecute = false };
            chmodPsi.ArgumentList.Add("+x");
            chmodPsi.ArgumentList.Add(scriptPath);
            Process.Start(chmodPsi)?.WaitForExit(5000);
        }
        catch { }

        var bashPsi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        bashPsi.ArgumentList.Add(scriptPath);
        Process.Start(bashPsi);
    }

    private void ApplyUpdateLinux(string extractedDir, bool writeInstallReceipt)
    {
        var logPath = Path.Combine(AppPaths.LogsDir, "update.log");
        using var updateLog = OpenUpdateLog(logPath);
        void Log(string msg)
        {
            var line = $"[{DateTime.UtcNow:HH:mm:ss}] {msg}";
            try { updateLog.WriteLine(line); updateLog.Flush(); } catch { }
        }

        Log($"=== Linux update started (pid {Environment.ProcessId}) ===");
        Log($"Source: {extractedDir}");

        var sourceDir = Path.Combine(extractedDir, "VPNRouter");
        if (!Directory.Exists(sourceDir))
            sourceDir = extractedDir;
        Log($"Effective source: {sourceDir}");

        var installDir = AppContext.BaseDirectory.TrimEnd('/');
        Log($"Install dir: {installDir}");

        var ownedTarget = ProcessOwnership.FindOwnedSingBox(ProcessOwnership.ConfiguredExePath);
        Log(ownedTarget is { } target
            ? $"Owned sing-box target: PID {target.Pid}"
            : "Owned sing-box target: none");

        if (installDir.Contains("/.mount_", StringComparison.OrdinalIgnoreCase) ||
            installDir.StartsWith("/tmp/", StringComparison.OrdinalIgnoreCase))
        {
            Log("ABORT: install dir is an AppImage mount — auto-update not supported");
            throw new InvalidOperationException(
                "AppImage auto-update is not yet supported. " +
                "Please download the new VPNRouter-linux-x86_64.AppImage " +
                "manually from the Releases page.");
        }

        var needsRoot = installDir.StartsWith("/opt/", StringComparison.OrdinalIgnoreCase) ||
                        installDir.StartsWith("/usr/", StringComparison.OrdinalIgnoreCase);
        var pkexec = needsRoot ? LinuxRuntimeEnvironment.ResolvePkexec() : null;
        Log($"Needs root (pkexec): {needsRoot}");
        if (needsRoot && pkexec == null)
            throw new InvalidOperationException("Trusted pkexec not found; cannot update a root-owned Linux installation.");

        if (needsRoot)
        {
            const string helper = "/usr/libexec/vpnrouter-update-helper";
            var helperSupportsExactSignal = HelperSupportsExactOwnedSignal(helper);
            if (!helperSupportsExactSignal)
            {
                Log($"WARNING: {helper} missing or legacy — using exact inline helper path");
                RunLegacyPrivilegedSteps(sourceDir, installDir, Log, logPath, pkexec, ownedTarget);
            }
            else
            {
                Log($"Invoking update helper via pkexec: {helper}");
                var helperArgs = new List<string> { helper, sourceDir, installDir };
                AppendOwnedSignalArguments(helperArgs, ownedTarget);
                var (hExit, hOut, hErr) = RunWithCapture(
                    pkexec!,
                    helperArgs,
                    timeoutMs: 120_000);
                Log($"helper exit={hExit} stdout={Truncate(hOut)} stderr={Truncate(hErr)}");
                if (hExit != 0)
                {
                    var hint = hExit switch
                    {
                        126 => " (authentication dialog was dismissed)",
                        127 => " (pkexec / polkit agent not available — install policykit-1 and try again)",
                        2   => " (helper: bad arguments)",
                        3   => " (helper: refused destination for safety)",
                        4   => " (helper: staging dir missing or not a directory)",
                        5   => " (helper: source missing VPNRouter.App)",
                        6   => " (helper: exact owned sing-box stop failed)",
                        _   => ""
                    };
                    throw new InvalidOperationException(
                        $"Update helper failed (exit {hExit}){hint}: {Truncate(hErr, 200)}".Trim());
                }
            }
        }
        else
        {
            RunLegacyPrivilegedSteps(sourceDir, installDir, Log, logPath, null, ownedTarget);
        }

        if (writeInstallReceipt)
            TryWriteInstallReceipt(logPath, Log);

        var newAppPath = Path.Combine(installDir, "VPNRouter.App");
        Log($"Launching new binary via detached relaunch helper: {newAppPath}");
        try
        {
            var parentPid = Environment.ProcessId;
            var helperPath = Path.Combine("/tmp", $"vpnrouter-relaunch-{parentPid}.sh");
            var helperLog  = Path.Combine("/tmp", $"vpnrouter-relaunch-{parentPid}.log");
            var safeHelperLog = EscapeShellArgument(helperLog);
            var safeNewAppPath = EscapeShellArgument(newAppPath);
            var safeHelperPath = EscapeShellArgument(helperPath);
            var helperScript =
                "#!/bin/sh\n" +
                "set +e\n" +
                $"exec >>'{safeHelperLog}' 2>&1\n" +
                $"echo \"[$(date -u +%H:%M:%S)] vpnrouter-relaunch helper started, parent={parentPid}\"\n" +
                "for i in $(seq 1 150); do\n" +
                $"  if ! kill -0 {parentPid} 2>/dev/null; then\n" +
                $"    break\n" +
                $"  fi\n" +
                $"  sleep 0.2\n" +
                $"done\n" +
                "echo \"[$(date -u +%H:%M:%S)] parent gone, launching update\"\n" +
                $"setsid --fork nohup '{safeNewAppPath}' </dev/null >/dev/null 2>&1\n" +
                $"echo \"[$(date -u +%H:%M:%S)] setsid returned $?\"\n" +
                $"rm -f '{safeHelperPath}'\n";
            File.WriteAllText(helperPath, helperScript);
            try { File.SetUnixFileMode(helperPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { }

            var psi = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = installDir,
            };
            psi.ArgumentList.Add(helperPath);
            using var helperProc = Process.Start(psi);
            if (helperProc == null)
            {
                Log("FATAL: relaunch helper Process.Start returned null");
                throw new InvalidOperationException(
                    $"Failed to launch relaunch helper. See {logPath}.");
            }
            if (helperProc.WaitForExit(500) && helperProc.ExitCode != 0)
            {
                Log($"WARNING: helper exited early with exit {helperProc.ExitCode} — see {helperLog}");
            }
            Log($"Relaunch helper detached (helper log: {helperLog})");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            Log($"FATAL: relaunch helper launch threw: {ex.Message}");
            throw new InvalidOperationException(
                $"Failed to launch relaunch helper: {ex.Message}. See {logPath}.");
        }
        Log("Update successful, old instance exiting");
    }

    private void RunLegacyPrivilegedSteps(string sourceDir, string installDir,
        Action<string> log, string logPath, string? pkexec,
        OwnedProcessIdentity? ownedTarget)
    {
        var needsRoot = installDir.StartsWith("/opt/", StringComparison.OrdinalIgnoreCase) ||
                        installDir.StartsWith("/usr/", StringComparison.OrdinalIgnoreCase);

        if (ownedTarget is { } target)
        {
            var hostPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(hostPath) || !Path.IsPathFullyQualified(hostPath))
                throw new InvalidOperationException("Current VPNRouter helper executable is unavailable.");

            var helperArgs = SingBoxManager.BuildLinuxOwnedSignalHelperArguments(
                hostPath,
                target,
                signal: 9);
            var helperCommand = hostPath;
            IReadOnlyList<string> signalArgs = helperArgs.Skip(1).ToArray();
            if (needsRoot)
            {
                helperCommand = pkexec!;
                signalArgs = helperArgs;
            }

            var (signalExit, _, signalError) = RunWithCapture(helperCommand, signalArgs, 30_000);
            log($"exact owned sing-box signal exit={signalExit} stderr={Truncate(signalError)}");
            if (signalExit != 0)
                throw new InvalidOperationException(
                    $"Exact owned sing-box stop failed (exit {signalExit}): {Truncate(signalError, 200)}".Trim());
        }

        {
            var cpCmd = needsRoot ? pkexec! : "/bin/cp";
            var cpArgs = needsRoot
                ? new[] { "cp", "-rfT", sourceDir, installDir }
                : new[] { "-rfT", sourceDir, installDir };
            var (cpExit, _, cpErr) = RunWithCapture(cpCmd, cpArgs, 120_000);
            log($"cp exit={cpExit} stderr={Truncate(cpErr)}");
            if (cpExit != 0)
            {
                var hint = cpExit switch
                {
                    126 => " (authentication dismissed)",
                    127 => " (pkexec / polkit agent not available)",
                    _   => ""
                };
                throw new InvalidOperationException(
                    $"Update copy failed (exit {cpExit}){hint}: {Truncate(cpErr, 200)}".Trim());
            }
        }

        try
        {
            var chmodCmd = needsRoot ? pkexec! : "/bin/chmod";
            var chmodArgs = needsRoot
                ? new[] { "chmod", "+x", $"{installDir}/VPNRouter.App", $"{installDir}/sing-box" }
                : new[] { "+x", $"{installDir}/VPNRouter.App", $"{installDir}/sing-box" };
            var (chExit, _, chErr) = RunWithCapture(chmodCmd, chmodArgs, 10_000);
            log($"chmod exit={chExit} stderr={Truncate(chErr)}");
        }
        catch (Exception ex) { log($"chmod threw: {ex.Message}"); }
    }

    internal static bool HelperSupportsExactOwnedSignal(string path)
    {
        try
        {
            return File.Exists(path)
                   && File.ReadAllText(path).Contains("--owned-signal-v1", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    internal static void AppendOwnedSignalArguments(
        ICollection<string> args,
        OwnedProcessIdentity? target)
    {
        if (target is not { } owned) return;
        args.Add("--owned-signal-v1");
        args.Add(owned.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        args.Add(owned.StartedAtUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        args.Add(owned.ExecutablePath);
    }

    private static StreamWriter OpenUpdateLog(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new StreamWriter(path, append: true);
        }
        catch
        {
            var ms = new MemoryStream();
            return new StreamWriter(ms);
        }
    }

    internal static string EscapeShellArgument(string arg) =>
        arg.Replace("'", "'\\''");

    private static string Truncate(string s, int max = 120)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    private static string? FindCurrentAppBundle()
    {
        var dir = AppContext.BaseDirectory.TrimEnd('/');
        while (!string.IsNullOrEmpty(dir))
        {
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
