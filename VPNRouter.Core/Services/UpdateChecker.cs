using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using VPNRouter.Core.Models;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Core.Services;

public class UpdateChecker : IDesktopInstaller
{
    private readonly IHttpClient _http;
    private readonly UpdateSettings _settings;
    private readonly string _currentVersion;
    private readonly string _stagingDir;

    public event Action<int>? DownloadProgress;
    public event Action<string>? StatusChanged;

    public UpdateChecker(UpdateSettings settings, string currentVersion)
        : this(settings, currentVersion, PolicyHttpClient.Shared)
    {
    }

    public UpdateChecker(UpdateSettings settings, string currentVersion, IHttpClient http)
    {
        _settings = settings;
        _currentVersion = currentVersion;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _stagingDir = Path.Combine(AppPaths.DataDir, "update-staging");
    }

    public Task<UpdateSourceInfo?> CheckAsync(CancellationToken ct = default)
    {
        var source = new GitHubReleaseSource(_settings, _currentVersion, _http, this);
        return source.CheckAsync(ct);
    }

    Task<string> IDesktopInstaller.DownloadAndStageAsync(
        UpdateSourceInfo info,
        IProgress<UpdateSources.DownloadProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(info);

        var legacy = new UpdateInfo
        {
            CurrentVersion = _currentVersion,
            LatestVersion = info.Version,
            DownloadUrl = info.DownloadUrl,
            SizeBytes = info.AssetSize,
            ReleaseNotes = info.ReleaseNotes,
            HtmlUrl = info.ReleaseUrl,
            IsNewer = true,
            HasLiteUpdate = false,
            FullChecksumUrl = null,
            FullChecksumSha256 = info.AssetSha256,
        };

        Action<int>? handler = null;
        if (progress != null)
        {
            handler = pct => progress.Report(new UpdateSources.DownloadProgress(
                BytesReceived: info.AssetSize > 0 ? info.AssetSize * pct / 100 : 0,
                TotalBytes: info.AssetSize > 0 ? info.AssetSize : null));
            DownloadProgress += handler;
        }
        return Run();

        async Task<string> Run()
        {
            try
            {
                return await DownloadAndStageAsync(legacy, ct).ConfigureAwait(false);
            }
            finally
            {
                if (handler != null)
                    DownloadProgress -= handler;
            }
        }
    }

    Task<bool> IDesktopInstaller.ApplyStagedAsync(
        UpdateSourceInfo info,
        string stagedPath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (string.IsNullOrWhiteSpace(stagedPath))
            throw new ArgumentException("Staged path must be non-empty.", nameof(stagedPath));
        var isDowngrade = IsVersionDowngrade(_currentVersion, info.Version);
        if (isDowngrade)
        {
            var backup = BackupConfigForDowngrade(info.Version);
            DeleteInstallReceiptForDowngrade();
            StatusChanged?.Invoke(backup == null
                ? Strings.DowngradeNoConfigBackup
                : string.Format(Strings.DowngradeConfigBackupCreated, backup));
        }
        ApplyUpdate(
            stagedPath,
            writeInstallReceipt: ShouldWriteInstallReceipt(_currentVersion, info.Version));
        return Task.FromResult(true);
    }

    internal static bool IsVersionDowngrade(string currentVersion, string targetVersion) =>
        TryParseSemVer(currentVersion, out var current) &&
        TryParseSemVer(targetVersion, out var target) &&
        target.CompareTo(current) < 0;

    internal static bool ShouldWriteInstallReceipt(string currentVersion, string targetVersion) =>
        !IsVersionDowngrade(currentVersion, targetVersion);

    internal static void DeleteInstallReceiptForDowngrade(string? dataDir = null)
    {
        var receiptPath = Path.Combine(dataDir ?? AppPaths.DataDir, ".update-installed-version");
        if (!File.Exists(receiptPath))
            return;
        File.Delete(receiptPath);
        if (File.Exists(receiptPath))
            throw new IOException(Strings.DowngradeReceiptCleanupFailed);
    }

    internal static string? BackupConfigForDowngrade(
        string targetVersion,
        string? configPath = null)
    {
        if (!TryParseSemVer(targetVersion, out _))
            throw new InvalidOperationException(Strings.DowngradeInvalidVersion);

        configPath ??= AppPaths.ConfigYamlPath;
        if (!File.Exists(configPath))
            return null;

        var backupPath =
            $"{configPath}.before-downgrade-to-{targetVersion}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}";
        using var source = new FileStream(
            configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var destination = AppPaths.CreatePrivateFile(backupPath);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
        return backupPath;
    }

    public async Task<string> DownloadAndStageAsync(UpdateInfo info, CancellationToken ct = default)
    {
        var useLite = info.HasLiteUpdate && !string.IsNullOrEmpty(info.LiteDownloadUrl);
        var downloadUrl = useLite ? info.LiteDownloadUrl! : info.DownloadUrl;
        var expectedSize = useLite ? info.LiteSizeBytes : info.SizeBytes;
        var checksumUrl = useLite ? info.LiteChecksumUrl : info.FullChecksumUrl;
        var label = useLite ? "lite update" : "full update";

        StatusChanged?.Invoke($"Downloading {label}...");

        TrySweepStaleStagingDirs();
        Directory.CreateDirectory(_stagingDir);
        var stagingDir = Path.Combine(
            _stagingDir, Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(stagingDir);

        string downloadExt =
            downloadUrl.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz"
            : downloadUrl.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)  ? ".tgz"
            : ".zip";
        var zipPath = Path.Combine(stagingDir, $"VPNRouter-v{info.LatestVersion}{downloadExt}");

        long totalBytes;
        await using (var response = await _http.SendStreamingAsync(
            new HttpRequest(HttpMethod.Get, new Uri(downloadUrl)),
            ct).ConfigureAwait(false))
        {
            if (!response.IsSuccess())
                throw new HttpRequestException(
                    $"HTTP {response.StatusCode} downloading update from {downloadUrl}");

            totalBytes = response.ContentLength ?? expectedSize;

            using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await response.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                totalRead += bytesRead;

                if (totalBytes > 0)
                    DownloadProgress?.Invoke((int)(totalRead * 100 / totalBytes));
            }

            fileStream.Close();
        }

        var downloadedSize = new FileInfo(zipPath).Length;
        if (expectedSize > 0 && downloadedSize < expectedSize * 0.9)
            throw new InvalidOperationException(
                $"Downloaded file is too small ({downloadedSize / 1024 / 1024} MB vs expected {expectedSize / 1024 / 1024} MB). Download may be corrupted.");

        string? expectedSha = info.FullChecksumSha256;
        if (string.IsNullOrEmpty(expectedSha) && !string.IsNullOrEmpty(checksumUrl))
        {
            var shaResponse = await _http.SendAsync(
                new HttpRequest(HttpMethod.Get, new Uri(checksumUrl)),
                ct);
            if (!shaResponse.IsSuccess())
                throw new InvalidOperationException(
                    $"Checksum download failed: HTTP {shaResponse.StatusCode}");
            expectedSha = shaResponse.AsString().Trim().ToLowerInvariant();

            if (expectedSha.Contains(' '))
                expectedSha = expectedSha.Split(' ', 2)[0].Trim();
        }

        if (string.IsNullOrEmpty(expectedSha))
        {
            try { File.Delete(zipPath); } catch { }
            throw new InvalidOperationException(
                "Update checksum is missing — refusing to extract an unverified package.");
        }

        StatusChanged?.Invoke("Verifying checksum...");

        if (expectedSha.Length != 64)
            throw new InvalidOperationException(
                $"Checksum is not a valid SHA256 (got {expectedSha.Length} hex chars, expected 64).");

        string actualSha;
        await using (var fs = File.OpenRead(zipPath))
        {
            var hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct);
            actualSha = Convert.ToHexStringLower(hashBytes);
        }

        if (!string.Equals(actualSha, expectedSha, StringComparison.Ordinal))
        {
            try { File.Delete(zipPath); } catch { }
            throw new InvalidOperationException(
                $"Checksum mismatch — download is corrupted.\r\n" +
                $"Expected: {expectedSha}\r\n" +
                $"Got:      {actualSha}\r\n" +
                $"File has been deleted. Click 'Update' again to retry.");
        }

        StatusChanged?.Invoke("Extracting update...");

        var extractDir = Path.Combine(stagingDir, "extracted");
        if (zipPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            zipPath.EndsWith(".tgz",    StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(extractDir);
            var tarArgs = new[] { "-xzf", zipPath, "-C", extractDir };
            var (tarExit, tarOut, tarErr) = RunWithCapture("tar", tarArgs, 120_000);
            if (tarExit != 0)
            {
                if (tarExit == -1)
                    throw new InvalidOperationException(
                        "tar extraction timed out after 120 s — archive may be corrupt. " +
                        $"Source: {zipPath}");
                throw new InvalidOperationException(
                    $"tar extraction failed (exit {tarExit}): {Truncate(tarErr, 200)}".Trim());
            }
        }
        else
        {
            ZipFile.ExtractToDirectory(zipPath, extractDir);
        }

        ValidateExtractedContent(extractDir);

        StatusChanged?.Invoke("Update ready to apply.");
        return extractDir;
    }

    public void CleanupStagingDir()
    {
        TrySweepStaleStagingDirs();

        try
        {
            var appDir = AppContext.BaseDirectory;
            foreach (var bak in Directory.GetFiles(appDir, "*.bak", SearchOption.AllDirectories))
            {
                try { File.Delete(bak); } catch { }
            }
        }
        catch { }
    }

    private void TrySweepStaleStagingDirs()
    {
        try
        {
            if (!Directory.Exists(_stagingDir)) return;
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(2);
            foreach (var dir in Directory.GetDirectories(_stagingDir))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                        Directory.Delete(dir, true);
                }
                catch { }
            }
        }
        catch { }
    }

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

    public static string? CheckInstallReceipt(string currentVersion)
    {
        try
        {
            var receiptPath = Path.Combine(AppPaths.DataDir, ".update-installed-version");
            if (!File.Exists(receiptPath))
                return null;

            var lines = File.ReadAllLines(receiptPath);
            if (lines.Length < 2) { TryDelete(receiptPath); return null; }
            var previousVersion = lines[1].Trim();

            if (!TryParseSemVer(previousVersion, out var prev) ||
                !TryParseSemVer(currentVersion, out var cur))
            {
                TryDelete(receiptPath);
                return null;
            }

            if (cur.CompareTo(prev) > 0)
            {
                TryDelete(receiptPath);
                return null;
            }

            var updateLogPath = Path.Combine(AppPaths.LogsDir, "update.log");
            return $"Last update attempt did not take effect. Still running {currentVersion}. " +
                   $"See {updateLogPath} for details.";
        }
        catch { return null; }

        static void TryDelete(string p) { try { File.Delete(p); } catch { } }
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

    private void TryWriteInstallReceipt(string logPath, Action<string> log)
    {
        try
        {
            var receiptPath = Path.Combine(AppPaths.DataDir, ".update-installed-version");
            File.WriteAllText(receiptPath, $"{DateTime.UtcNow:o}\n{_currentVersion}\n");
            log($"Receipt written: {receiptPath}");
        }
        catch (Exception ex)
        {
            log($"Receipt write failed: {ex.Message}");
        }
    }

    private void TryWriteInstallReceipt()
    {
        try
        {
            var receiptPath = Path.Combine(AppPaths.DataDir, ".update-installed-version");
            File.WriteAllText(receiptPath, $"{DateTime.UtcNow:o}\n{_currentVersion}\n");
        }
        catch
        {
        }
    }

    private static (int exit, string stdout, string stderr) RunWithCapture(
        string fileName, IEnumerable<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "timeout");
        }
        try { outTask.Wait(500); } catch { }
        try { errTask.Wait(500); } catch { }
        return (proc.ExitCode,
                outTask.IsCompletedSuccessfully ? outTask.Result : "",
                errTask.IsCompletedSuccessfully ? errTask.Result : "");
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

    private static void ValidateExtractedContent(string extractDir)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (Directory.GetDirectories(extractDir, "*.app", SearchOption.TopDirectoryOnly).Length > 0)
                return;
            if (Directory.Exists(Path.Combine(extractDir, "Contents")))
                return;
            if (File.Exists(Path.Combine(extractDir, "VPNRouter.Mac.dll")))
                return;
            throw new InvalidOperationException(
                "Invalid update package: no .app bundle or VPNRouter.Mac.dll found.");
        }

        if (OperatingSystem.IsLinux())
        {
            var linuxSubDir = Path.Combine(extractDir, "VPNRouter");
            if (File.Exists(Path.Combine(linuxSubDir, "VPNRouter.App")) ||
                File.Exists(Path.Combine(linuxSubDir, "VPNRouter.App.dll")))
                return;
            if (File.Exists(Path.Combine(extractDir, "VPNRouter.App")) ||
                File.Exists(Path.Combine(extractDir, "VPNRouter.App.dll")))
                return;
            throw new InvalidOperationException(
                "Invalid update package: VPNRouter.App not found in extracted tarball.");
        }

        var checkDir = extractDir;
        var appSubDir = Path.Combine(extractDir, "app");
        if (Directory.Exists(appSubDir) &&
            (File.Exists(Path.Combine(appSubDir, "VPNRouter.GUI.exe")) ||
             File.Exists(Path.Combine(appSubDir, "VPNRouter.GUI.dll"))))
        {
            checkDir = appSubDir;
        }

        if (!File.Exists(Path.Combine(checkDir, "VPNRouter.GUI.exe")) &&
            !File.Exists(Path.Combine(checkDir, "VPNRouter.GUI.dll")))
            throw new InvalidOperationException(
                "Invalid update package: VPNRouter.GUI.exe/dll not found.");
    }

    internal readonly struct SemVer : IComparable<SemVer>, IEquatable<SemVer>
    {
        public readonly Version Core;
        public readonly int? Rc;

        public SemVer(Version core, int? rc) { Core = core; Rc = rc; }

        public int CompareTo(SemVer other)
        {
            var c = Core.CompareTo(other.Core);
            if (c != 0) return c;
            if (!Rc.HasValue && !other.Rc.HasValue) return 0;
            if (!Rc.HasValue) return 1;
            if (!other.Rc.HasValue) return -1;
            return Rc.Value.CompareTo(other.Rc.Value);
        }

        public bool Equals(SemVer other) => CompareTo(other) == 0;
        public override bool Equals(object? obj) => obj is SemVer v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(Core, Rc);
        public override string ToString() => Rc.HasValue ? $"{Core}-r{Rc.Value}" : Core.ToString();
    }

    internal static bool TryParseSemVer(string? tag, out SemVer result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var s = tag.StartsWith('v') || tag.StartsWith('V') ? tag.Substring(1) : tag;

        var dash = s.IndexOf('-');
        string corePart = dash < 0 ? s : s.Substring(0, dash);
        string? suffix  = dash < 0 ? null : s.Substring(dash + 1);

        if (!Version.TryParse(corePart, out var core))
            return false;

        if (suffix is null)
        {
            result = new SemVer(core, null);
            return true;
        }

        if (suffix.Length < 2 || (suffix[0] != 'r' && suffix[0] != 'R'))
            return false;
        if (!int.TryParse(suffix.AsSpan(1), out var rc) || rc < 0)
            return false;

        result = new SemVer(core, rc);
        return true;
    }
}
