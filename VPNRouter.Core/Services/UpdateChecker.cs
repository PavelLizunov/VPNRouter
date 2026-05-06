using System.Diagnostics;
using System.IO.Compression;
using Newtonsoft.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public class UpdateChecker
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly UpdateSettings _settings;
    private readonly string _currentVersion;
    private readonly string _stagingDir;

    public event Action<UpdateInfo>? UpdateAvailable;
    public event Action<int>? DownloadProgress;        // 0-100
    public event Action<string>? StatusChanged;

    // ── Platform-specific asset naming ──
    // v2.0+: VPNRouter-v2.0.0-win.zip / VPNRouter-v2.0.0-mac.zip
    // v2.21.0+: VPNRouter-v2.21.0-linux.tar.gz (Linux build added)
    // Legacy: VPNRouter-install-v1.24.6.zip (old Windows naming, still supported)
    private static readonly string PlatformSuffix =
        OperatingSystem.IsMacOS()   ? "-mac"
        : OperatingSystem.IsLinux() ? "-linux"
        :                              "-win";

    static UpdateChecker()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "VPNRouter");
    }

    public UpdateChecker(UpdateSettings settings, string currentVersion)
    {
        _settings = settings;
        _currentVersion = currentVersion;
        _stagingDir = Path.Combine(AppPaths.DataDir, "update-staging");
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.GitHubRepo))
            return null;

        // v2.21.10: parser now also understands rolling `-rN` candidate
        // suffixes (e.g. "2.22.0-r1") per the strategy in
        // plans/vpnrouter-release-strategy.md. Tags with suffixes that
        // aren't -rN (e.g. "v1.0.0-mac") still return null and are skipped.
        if (!TryParseSemVer(_currentVersion, out var current))
            return null;

        var url = $"https://api.github.com/repos/{_settings.GitHubRepo}/releases?per_page=30";
        var json = await _http.GetStringAsync(url, ct);

        var releases = JsonConvert.DeserializeAnonymousType(json, new[]
        {
            new
            {
                tag_name = "",
                body = "",
                html_url = "",
                draft = false,
                prerelease = false,
                assets = new[] { new { browser_download_url = "", size = 0L, name = "" } }
            }
        });

        if (releases == null || releases.Length == 0)
            return null;

        var newerReleases = releases
            .Where(r => !r.draft && (_settings.IsExperimental || !r.prerelease))
            .Select(r => new
            {
                Release = r,
                Tag = r.tag_name.TrimStart('v'),
                Parsed = TryParseSemVer(r.tag_name.TrimStart('v'), out var v) ? v : (SemVer?)null
            })
            .Where(r => r.Parsed != null && r.Parsed.Value.CompareTo(current) > 0)
            .OrderByDescending(r => r.Parsed!.Value)
            .ToList();

        if (newerReleases.Count == 0)
            return null;

        var latestRelease = newerReleases[0];

        // ── Find platform-specific assets ──
        var fullAsset = FindFullAsset(latestRelease.Release.assets);
        var liteAsset = FindLiteAsset(latestRelease.Release.assets);
        var fullSha   = FindChecksumAsset(latestRelease.Release.assets, fullAsset);
        var liteSha   = FindChecksumAsset(latestRelease.Release.assets, liteAsset);

        bool canUseLite = liteAsset != null && IsSharedRuntimeInstall();

        if (fullAsset == null && !canUseLite)
            return null;

        var allNotes = newerReleases
            .Where(r => !string.IsNullOrWhiteSpace(r.Release.body))
            .Select(r => r.Release.body!.Trim())
            .ToList();

        var info = new UpdateInfo
        {
            CurrentVersion = _currentVersion,
            LatestVersion = latestRelease.Tag,
            DownloadUrl = fullAsset?.browser_download_url
                          ?? liteAsset?.browser_download_url ?? string.Empty,
            ReleaseNotes = string.Join("\n\n", allNotes),
            HtmlUrl = latestRelease.Release.html_url ?? string.Empty,
            SizeBytes = fullAsset?.size ?? liteAsset?.size ?? 0,
            IsNewer = true,
            LiteDownloadUrl = liteAsset?.browser_download_url,
            LiteSizeBytes = liteAsset?.size ?? 0,
            HasLiteUpdate = canUseLite,
            FullChecksumUrl = (string?)fullSha?.browser_download_url,
            LiteChecksumUrl = (string?)liteSha?.browser_download_url,
        };

        UpdateAvailable?.Invoke(info);
        return info;
    }

    public async Task<string> DownloadAndStageAsync(UpdateInfo info, CancellationToken ct = default)
    {
        var useLite = info.HasLiteUpdate && !string.IsNullOrEmpty(info.LiteDownloadUrl);
        var downloadUrl = useLite ? info.LiteDownloadUrl! : info.DownloadUrl;
        var expectedSize = useLite ? info.LiteSizeBytes : info.SizeBytes;
        var checksumUrl = useLite ? info.LiteChecksumUrl : info.FullChecksumUrl;
        var label = useLite ? "lite update" : "full update";

        StatusChanged?.Invoke($"Downloading {label}...");

        if (Directory.Exists(_stagingDir))
            Directory.Delete(_stagingDir, true);
        Directory.CreateDirectory(_stagingDir);

        // v2.22.2: derive extension from the download URL, NOT hardcode .zip.
        // Linux releases ship as .tar.gz. If we save the tarball as .zip,
        // the extraction branch picks ZipFile.ExtractToDirectory (PKZIP
        // only) and hangs forever on a gzip-tar magic number. The tar-
        // extraction fix in v2.22.1 was never reached because the filename
        // suffix didn't match. This was the ACTUAL root cause of the
        // "Extracting update..." forever-hang users saw on Linux.
        string downloadExt =
            downloadUrl.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz"
            : downloadUrl.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)  ? ".tgz"
            : ".zip";
        var zipPath = Path.Combine(_stagingDir, $"VPNRouter-v{info.LatestVersion}{downloadExt}");

        using var response = await _http.GetAsync(downloadUrl,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;

        using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            if (totalBytes > 0)
                DownloadProgress?.Invoke((int)(totalRead * 100 / totalBytes));
        }

        fileStream.Close();

        var downloadedSize = new FileInfo(zipPath).Length;
        if (expectedSize > 0 && downloadedSize < expectedSize * 0.9)
            throw new InvalidOperationException(
                $"Downloaded file is too small ({downloadedSize / 1024 / 1024} MB vs expected {expectedSize / 1024 / 1024} MB). Download may be corrupted.");

        // ── Verify SHA256 against .sha256 asset on the release (v2.15.8) ──
        if (!string.IsNullOrEmpty(checksumUrl))
        {
            StatusChanged?.Invoke("Verifying checksum...");
            var expectedSha = (await _http.GetStringAsync(checksumUrl, ct)).Trim().ToLowerInvariant();

            // Strip any trailing filename portion if the .sha256 file used "HASH  filename" format
            if (expectedSha.Contains(' '))
                expectedSha = expectedSha.Split(' ', 2)[0].Trim();

            if (expectedSha.Length != 64)
                throw new InvalidOperationException(
                    $"Checksum file content is not a valid SHA256 (got {expectedSha.Length} hex chars, expected 64).");

            string actualSha;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            await using (var fs = File.OpenRead(zipPath))
            {
                var hashBytes = await sha.ComputeHashAsync(fs, ct);
                actualSha = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            if (!string.Equals(actualSha, expectedSha, StringComparison.Ordinal))
            {
                // Delete the corrupt file so a retry pulls it fresh
                try { File.Delete(zipPath); } catch { }
                throw new InvalidOperationException(
                    $"Checksum mismatch — download is corrupted.\r\n" +
                    $"Expected: {expectedSha}\r\n" +
                    $"Got:      {actualSha}\r\n" +
                    $"File has been deleted. Click 'Update' again to retry.");
            }
        }

        StatusChanged?.Invoke("Extracting update...");

        var extractDir = Path.Combine(_stagingDir, "extracted");
        // v2.21.8: on Linux we ship the update as .tar.gz (to preserve Unix
        // execute bits). ZipFile.ExtractToDirectory doesn't understand
        // gzip-tar — it only reads PKZIP format, so calling it on a
        // tarball threw silently (depending on the leading bytes it would
        // either throw "Not a ZIP archive" or hang trying to read a
        // non-existent central directory). Previously this surfaced as
        // a stuck "Extracting update..." banner with no way forward.
        // Route by extension: .tar.gz → shell out to tar; everything
        // else (Windows .zip, macOS .zip) → ZipFile as before.
        if (zipPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            zipPath.EndsWith(".tgz",    StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(extractDir);
            // tar -xzf <archive> -C <dir> — preserves file modes.
            //
            // v2.22.1: BUG FIX for "Extracting update..." hanging forever.
            // The original code set RedirectStandardOutput/Error = true and
            // then called WaitForExit() without reading the streams. tar
            // eventually fills the OS pipe buffer (~64 KB on Linux) with
            // any warning it emits (e.g. "ignoring unknown extended header
            // keyword 'SCHILY.*'" when extracting GNU-produced tarballs
            // on some distros), blocks on write, and we block on exit.
            // Classic deadlock. Fix: use RunWithCapture which reads both
            // streams async, plus a 120 s timeout.
            var tarCmd = $"-xzf \"{zipPath}\" -C \"{extractDir}\"";
            var (tarExit, tarOut, tarErr) = RunWithCapture("tar", tarCmd, 120_000);
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
        try
        {
            if (Directory.Exists(_stagingDir))
                Directory.Delete(_stagingDir, true);
        }
        catch { }

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

    /// <summary>
    /// Apply update. Platform-specific:
    /// - Windows: copy files, rename locked executables (.bak), relaunch .exe
    /// - macOS:   replace .app bundle contents via detached bash helper
    /// - Linux:   same detached-bash-helper model, rsync/cp of the extracted
    ///            tar.gz layout over the current install dir; AppImage users
    ///            get a clear "not supported, download a new AppImage"
    ///            message because replacing a FUSE-mounted AppImage while it
    ///            runs is a separate (and risky) flow.
    /// </summary>
    public void ApplyUpdate(string extractedDir)
    {
        if (OperatingSystem.IsMacOS())
            ApplyUpdateMac(extractedDir);
        else if (OperatingSystem.IsLinux())
            ApplyUpdateLinux(extractedDir);
        else
            ApplyUpdateWindows(extractedDir);
    }

    // ─── Windows ─────────────────────────────────────────────────────────────

    private void ApplyUpdateWindows(string extractedDir)
    {
        // v2.29.0-r7: changed from static → instance so we can call
        // TryWriteInstallReceipt (which uses _currentVersion). The Mac
        // and Linux variants similarly differ — Mac is static, Linux is
        // instance because Linux already wrote receipts via Log helper.
        // v2.29.0-r5 — detached .cmd helper. User report 2026-04-29:
        // «обновление завершается, приложение перезапускается, но снова
        // со старой версией».
        //
        // Pre-r5 logic did the file copy in-process from VPNRouter.App.exe.
        // Many runtime DLLs (VPNRouter.App.dll, VPNRouter.Core.dll,
        // hostfxr.dll, coreclr.dll, runtime DLLs) are mapped into our own
        // process address space and Windows refuses to overwrite-while-
        // mapped on same path. Pre-r5 caught the IOException and tried a
        // .bak rename + copy retry — but rename can also fail if the
        // file was opened without FILE_SHARE_DELETE share-mode (which
        // .NET hosting layer may do for native bootstrap DLLs).
        //
        // EVERY failure was swallowed by `try { } catch { }` and the loop
        // moved on. Result: ~10 % of files (the locked ones) didn't get
        // replaced. App restarts, hostfxr resolves DLLs from current
        // appDir → some are NEW, some still OLD → mixed-version run that
        // shows the OLD AppVersion string.
        //
        // r5 fix: write a self-contained .cmd helper that runs AFTER our
        // process exits. By that time no DLL is mapped, copy/move is
        // unrestricted, and the relaunch is guaranteed to load the new
        // binary set. Mirrors the existing Mac `ditto` helper pattern.
        var appDir = AppContext.BaseDirectory.TrimEnd('\\');

        // Strip app/ wrapper from install ZIP layout (legacy install ZIPs).
        var appSubDir = Path.Combine(extractedDir, "app");
        if (Directory.Exists(appSubDir) &&
            (File.Exists(Path.Combine(appSubDir, "VPNRouter.GUI.exe")) ||
             File.Exists(Path.Combine(appSubDir, "VPNRouter.GUI.dll"))))
        {
            extractedDir = appSubDir;
        }

        // v2.29.0-r6 — strip `_bootstrap/` wrapper from update ZIP layout.
        // The bootstrap layout was added in r6 to rescue users on the
        // pre-r6 broken updater (see r6 release notes / VPNRouter.GUI/main.go).
        // For r6+ users running this fixed updater, we want to copy the
        // _bootstrap/ contents directly to appDir and skip the GUI.exe-
        // bootstrap recovery (which would still WORK as a no-op fallback,
        // but adds a redundant copy round-trip). The Go stub at
        // extractedDir-root is identical to what's inside _bootstrap/'s
        // peer set — keep it where it is, that's the launcher.
        var bootstrapSubDir = Path.Combine(extractedDir, "_bootstrap");
        if (Directory.Exists(bootstrapSubDir) &&
            File.Exists(Path.Combine(extractedDir, "VPNRouter.GUI.exe")))
        {
            // Move the Go stub into _bootstrap/ so the unified xcopy
            // brings it across alongside everything else; otherwise
            // we'd need a separate copy step for it.
            try
            {
                File.Copy(
                    Path.Combine(extractedDir, "VPNRouter.GUI.exe"),
                    Path.Combine(bootstrapSubDir, "VPNRouter.GUI.exe"),
                    overwrite: true);
            }
            catch { /* ignore — original stub stays at root, helper still finds it */ }
            extractedDir = bootstrapSubDir;
        }

        var guiExe = Path.Combine(appDir, "VPNRouter.GUI.exe");
        var parentPid = Environment.ProcessId;

        // v2.29.0-r7 LAYER 7: install receipt. Mirrors Linux's existing
        // .update-installed-version receipt (TryWriteInstallReceipt below).
        // Records the currently-running version + timestamp BEFORE the
        // file copy. On next startup CheckInstallReceipt() reads it; if
        // the running version is NOT strictly newer than the receipt's
        // pre-update version → surface "Last update didn't take effect"
        // warning to the user. Catches partial updates that the cmd
        // helper xcopy can't recover from.
        TryWriteInstallReceipt();

        // Helper script lives in %TEMP% (always user-writable).
        // v2.31.8-r5: helper LOG moved from %TEMP% to LogsDir/update.log so
        // CheckInstallReceipt's «See {LogsDir}/update.log for details»
        // banner reference actually points to a real file. Pre-r5 the
        // banner referenced LogsDir/update.log but Windows helper wrote
        // to %TEMP%\vpnrouter-update-{pid}.log — different path, file
        // never appeared at the documented location, user was left
        // looking for nothing. Linux helper has always used LogsDir;
        // now Windows matches.
        var tempDir = Path.GetTempPath();
        var helperPath = Path.Combine(tempDir, $"vpnrouter-update-{parentPid}.cmd");
        var logsDir = AppPaths.LogsDir;
        try { Directory.CreateDirectory(logsDir); } catch { /* helper will surface via .cmd echo */ }
        var helperLog = Path.Combine(logsDir, "update.log");

        // CI integration test (.github/workflows/test-windows-update.yml) sets
        // VPNROUTER_CI=1. In that mode the helper still does the full file
        // copy (so we exercise the actual xcopy + Service-stop dance and any
        // CMD parser bugs surface), but skips the final `start "" GUI.exe`
        // relaunch — otherwise the runner would spawn a stray Avalonia
        // window that the test can't close cleanly, and the Go stub's
        // SelfRepair path could trigger a stray install.ps1 download.
        var skipRelaunchForCi =
            string.Equals(Environment.GetEnvironmentVariable("VPNROUTER_CI"), "1",
                StringComparison.Ordinal);

        // Build the .cmd. Notes:
        // - SET LF to a single newline so we can echo blank lines if needed.
        // - `>>"%LOG%" 2>&1` on each line so failures are visible postmortem.
        // - Wait loop: poll up to 30 s for parent PID to exit before copy.
        //   `tasklist /FI "PID eq <pid>" | find` returns 0 if found, 1 if not.
        // - `xcopy /S /Y /Q /R /I` recursively copies overwriting. /R copies
        //   read-only files (the install dir may have some). /I treats
        //   destination as directory. Trailing backslashes matter on xcopy.
        // - `taskkill /IM sing-box.exe /F` kills any sing-box owned by the
        //   service or by us; ignored if missing.
        // - `del "%~f0"` self-delete at the end (unreachable if the
        //   helper crashes, but %TEMP% gets cleaned eventually).
        var cmd = string.Join("\r\n", new[]
        {
            "@echo off",
            // v2.31.8-r10 — CRITICAL FIX. Pre-r10 helper.cmd had a CMD
            // parser bug: the nested `if errorlevel 1 (...) else (...)`
            // block referenced `%SVC_TRIES%` (initialised inside the else
            // branch) at PARSE time. CMD pre-expands all %...% references
            // when parsing a parenthesised block, so SVC_TRIES — not yet
            // defined — became empty, the line `if %SVC_TRIES% gtr 20`
            // turned into `if  gtr 20` → "20 was unexpected at this time"
            // → entire helper aborted right after «checking Service»
            // log line. Net effect: 100% of v2.31.7 user upgrades to
            // v2.31.8 silently failed; the partial helper run renamed
            // nothing, copied nothing, just exited; install receipt
            // detected mismatch on next launch → banner.
            //
            // Discovered via probe-driven trace 2026-05-05:
            //   sc query VPNRouter  1>nul 2>&1
            //   20 was unexpected at this time.
            //
            // Fix: setlocal EnableDelayedExpansion + use !VAR! everywhere
            // a variable is set inside the same parsed block. Affects
            // TRIES, SVC_TRIES, SVC_WAS_RUNNING, XCOPY_EXIT.
            "setlocal EnableDelayedExpansion",
            $"set \"LOG={helperLog}\"",
            $"set \"PARENT_PID={parentPid}\"",
            $"set \"SRC={extractedDir.TrimEnd('\\')}\"",
            $"set \"DST={appDir}\"",
            "echo [%TIME%] vpnrouter-update helper start, parent=%PARENT_PID% >>\"%LOG%\"",
            // Wait for parent to exit (max 30 s = 60 × 0.5 s).
            "set /a TRIES=0",
            ":waitloop",
            "tasklist /FI \"PID eq %PARENT_PID%\" 2>nul | find \"%PARENT_PID%\" >nul",
            "if errorlevel 1 goto parentgone",
            "set /a TRIES+=1",
            "if !TRIES! gtr 60 (",
            "  echo [%TIME%] parent %PARENT_PID% still alive after 30 s, proceeding anyway >>\"%LOG%\"",
            "  goto parentgone",
            ")",
            "ping -n 1 -w 500 127.0.0.1 >nul",
            "goto waitloop",
            ":parentgone",
            "echo [%TIME%] parent gone, checking VPNRouter Windows Service >>\"%LOG%\"",
            // v2.31.7-r1: stop the Windows Service if it's running before
            // the file copy, otherwise the Service holds locks on
            // VPNRouter.Service.dll, VPNRouter.Core.dll and friends. xcopy
            // /R skips locked files silently → mixed-version DLL set after
            // restart. spark-wraith 2026-05-04: «впн не хочет обновляться
            // сам» traced to exactly this. We restart the Service after
            // the copy if it was running pre-update.
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
            // v2.31.8-r7: disable Service failure recovery actions BEFORE
            // sc stop. ServiceInstaller registers "restart 3x/60s" recovery
            // for unexpected exits. On slow VMs / large xcopy operations
            // (272 files, ~50 MB), if SCM treats the stop as a failure
            // (rare but possible — Service.exe exit code edge cases) the
            // recovery scheduler queues a restart 60 s later. If the
            // restart fires DURING xcopy, Core.dll/Service.dll relock and
            // /R silently skips them → mixed-version DLLs land. Disabling
            // failure actions guarantees no auto-restart can fire mid-
            // update. Re-enable after our explicit sc start so future
            // crashes still get auto-recovered.
            "    echo [%TIME%] disabling Service failure recovery during update >>\"%LOG%\"",
            "    sc failure VPNRouter reset= 0 actions= \"\" >>\"%LOG%\" 2>&1",
            "    sc stop VPNRouter >>\"%LOG%\" 2>&1",
            "    set /a SVC_TRIES=0",
            "    :svcstoploop",
            "    sc query VPNRouter | find \"STOPPED\" >nul",
            "    if not errorlevel 1 goto svcgone",
            "    set /a SVC_TRIES+=1",
            "    if !SVC_TRIES! gtr 20 (",
            "      echo [%TIME%] Service still not STOPPED after 10 s, proceeding anyway >>\"%LOG%\"",
            "      goto svcgone",
            "    )",
            "    ping -n 1 -w 500 127.0.0.1 >nul",
            "    goto svcstoploop",
            "    :svcgone",
            "    echo [%TIME%] Service stop confirmed >>\"%LOG%\"",
            "  )",
            ")",
            "echo [%TIME%] killing sing-box and copying files >>\"%LOG%\"",
            "taskkill /IM sing-box.exe /F >nul 2>&1",
            // Give Windows a moment to release file handles after parent exit.
            "ping -n 1 -w 750 127.0.0.1 >nul",
            "xcopy \"%SRC%\\*\" \"%DST%\\\" /E /Y /Q /R /I >>\"%LOG%\" 2>&1",
            "set XCOPY_EXIT=!ERRORLEVEL!",
            "echo [%TIME%] xcopy exit=!XCOPY_EXIT! >>\"%LOG%\"",
            // Restart Service if it was running pre-update — preserves the
            // user's set-and-forget Service-mode install across upgrades.
            "if \"!SVC_WAS_RUNNING!\"==\"1\" (",
            "  echo [%TIME%] restarting VPNRouter Service >>\"%LOG%\"",
            "  sc start VPNRouter >>\"%LOG%\" 2>&1",
            // v2.31.8-r7: restore Service failure recovery actions
            // (matches the values ServiceInstaller / WindowsServiceHelper
            // configure on install: restart 3 times with 60 s delay,
            // 24 h reset window). Without this restore, a future
            // unexpected sing-box-driven Service crash would not auto-
            // restart and the user would be left disconnected.
            "  echo [%TIME%] restoring Service failure recovery actions >>\"%LOG%\"",
            "  sc failure VPNRouter reset= 86400 actions= restart/60000/restart/60000/restart/60000 >>\"%LOG%\" 2>&1",
            ")",
            // Drop install receipt so next launch can detect failed update.
            // Format mirrors Linux receipt: line 1 = timestamp, line 2 = version.
            // Stale receipt (already there from a prior successful update) is
            // overwritten — that's the point.
            skipRelaunchForCi
                ? "echo [%TIME%] VPNROUTER_CI=1 — skipping GUI relaunch (CI integration test mode) >>\"%LOG%\""
                : "echo [%TIME%] launching new VPNRouter.GUI.exe >>\"%LOG%\"",
            skipRelaunchForCi
                ? "rem CI mode: relaunch suppressed"
                : $"start \"\" \"{guiExe}\"",
            "echo [%TIME%] helper done >>\"%LOG%\"",
            "del /Q \"%~f0\" >nul 2>&1",
            "exit /b 0",
        });

        File.WriteAllText(helperPath, cmd);

        // CI mode: tee cmd.exe's own stdout+stderr into a sidecar log so the
        // workflow can detect CMD-parser errors (e.g. "<x> was unexpected at
        // this time") that abort the helper BEFORE it manages to write to
        // its own update.log. Without this, parser bugs only surface as a
        // 5-minute timeout-on-missing-helper-done — slow + uninformative.
        // Production is unchanged because CreateNoWindow=true makes cmd.exe
        // output invisible anyway.
        var argString = skipRelaunchForCi
            ? $"/c \"\"{helperPath}\" > \"{Path.Combine(logsDir, "helper-stderr.log")}\" 2>&1\""
            : $"/c \"{helperPath}\"";

        // Launch the helper detached. UseShellExecute=true with no Window
        // ensures the cmd doesn't keep our parent process attached to its
        // console. WorkingDirectory irrelevant.
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = argString,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        // Don't wait. The caller (UpdateUiHandler) will Environment.Exit(0)
        // a moment later; the helper's wait loop catches that and proceeds.
    }

    // ─── macOS ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Self-update flow on macOS. Hard constraints on this platform:
    ///   1. A running .app bundle CANNOT be safely overwritten file-by-file.
    ///      macOS holds the executable mapped; replacing individual files of
    ///      a live bundle produces an undefined state and usually ends with
    ///      the new .app silently refusing to launch.
    ///   2. <c>File.Copy</c> on Unix drops the executable bit — the copied
    ///      Mach-O binary ends up as -rw-r--r-- and <c>/usr/bin/open</c>
    ///      won't launch it. Pre-v2.18.1 tried to fix this with per-file
    ///      chmod wrapped in silent try/catch — which routinely failed
    ///      silently on slow disks or SIP-protected directories.
    ///   3. Anything downloaded via HTTP carries the <c>com.apple.quarantine</c>
    ///      extended attribute. Gatekeeper will refuse to launch a
    ///      quarantined bundle and <c>open</c> exits 0 anyway — so the
    ///      user sees "update applied" but nothing launches.
    ///
    /// v2.18.1 replaces the in-process file copy with a detached bash
    /// helper script that runs AFTER the current process exits. The script
    /// uses <c>ditto</c> (preserves permissions, symlinks, xattrs, the
    /// whole bundle tree atomically), strips quarantine, ensures <c>+x</c>
    /// on MacOS/, and launches the freshly-installed bundle. Script stdout
    /// + stderr are tee'd to <c>/tmp/vpnrouter-update-&lt;pid&gt;.log</c> so
    /// any failure mode is visible postmortem instead of vanishing.
    /// </summary>
    private static void ApplyUpdateMac(string extractedDir)
    {
        // Locate the staged .app bundle. The install ZIP is expected to
        // contain VPNRouter.app/ at the top level (see build-mac.sh).
        // Older layouts (just Contents/ or flat files) are rejected here —
        // supporting them requires the unsafe in-place overwrite we're
        // deliberately moving away from.
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

        // The helper script. Runs asynchronously after the current process
        // exits. Every step is logged with a timestamp so the log is
        // self-explanatory if the user reports "update didn't happen".
        //
        // Key decisions:
        //   • `kill -0 <PID>` polls for the old process while it shuts
        //     down. Timeout caps at 15s to avoid zombie scripts if the
        //     old process hangs.
        //   • The live .app is moved aside to <app>.old-<pid> before
        //     `ditto` writes the new tree, so ditto never writes to a
        //     path macOS might still have open.
        //   • `ditto -rsrc` copies resource forks + xattrs + symlinks
        //     and preserves the Unix mode bits — no per-file chmod dance.
        //   • `xattr -dr com.apple.quarantine` is applied AFTER ditto so
        //     the freshly-installed bundle is clean for Gatekeeper.
        //   • `open <app>` (no -n) launches the new version without
        //     forcing a duplicate; the old PID is gone by this point.
        var script =
            "#!/bin/bash\n" +
            $"exec >\"{logPath}\" 2>&1\n" +
            "set +e\n" +
            "ts() { date '+%Y-%m-%dT%H:%M:%S%z'; }\n" +
            "log() { echo \"[$(ts)] $*\"; }\n" +
            "log '── VPNRouter macOS updater ──'\n" +
            $"log 'Old PID: {pid}'\n" +
            $"log 'Staged:  {stagedApp}'\n" +
            $"log 'Target:  {targetApp}'\n" +
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
            $"xattr -dr com.apple.quarantine \"{stagedApp}\" 2>/dev/null\n" +
            "log 'Stripped quarantine from staging'\n" +
            $"BACKUP=\"{targetApp}.old-{pid}\"\n" +
            $"if [ -d \"{targetApp}\" ]; then\n" +
            $"  mv \"{targetApp}\" \"$BACKUP\" && log 'Backed up old bundle to '\"$BACKUP\" || {{ log 'FAIL: mv old bundle aside'; exit 10; }}\n" +
            "fi\n" +
            $"ditto --rsrc \"{stagedApp}\" \"{targetApp}\" || {{ log 'FAIL: ditto copy'; [ -d \"$BACKUP\" ] && mv \"$BACKUP\" \"{targetApp}\"; exit 11; }}\n" +
            "log 'Installed new bundle via ditto'\n" +
            $"xattr -dr com.apple.quarantine \"{targetApp}\" 2>/dev/null\n" +
            "log 'Stripped quarantine from target'\n" +
            $"chmod -R +x \"{targetApp}/Contents/MacOS\" 2>/dev/null\n" +
            "log 'chmod +x on MacOS/'\n" +
            "rm -rf \"$BACKUP\" 2>/dev/null\n" +
            $"open \"{targetApp}\" && log 'Launched new bundle' || log 'WARN: open exited non-zero'\n" +
            "log 'Done.'\n";

        File.WriteAllText(scriptPath, script);

        // chmod +x the script itself (File.WriteAllText creates it 0644).
        // Block briefly — if this fails the whole flow is dead anyway.
        try
        {
            Process.Start(new ProcessStartInfo("/bin/chmod", $"+x \"{scriptPath}\"")
                { UseShellExecute = false })?.WaitForExit(5000);
        }
        catch { /* proceeding; bash may still run the script via /bin/bash scriptPath */ }

        // Fire and forget. We intentionally do NOT WaitForExit — the
        // script's whole job is to wait for us to exit. Use /bin/bash so
        // it runs even if chmod above failed.
        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"\"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    // ─── Linux ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Self-update flow on Linux.
    ///
    /// <para>
    /// v2.21.5 rewrite: the previous version spawned a detached bash
    /// helper in /tmp that waited for our PID to exit, then ran
    /// <c>cp -rfT</c> (optionally under pkexec for /opt/vpnrouter). That
    /// worked in principle but any failure — pkexec dialog dismissed,
    /// path resolution wrong, cp exit != 0 — vanished into a log file
    /// the user never knew existed. Net effect: "downloaded the update
    /// but nothing happened."
    /// </para>
    /// <para>
    /// This version does the copy synchronously BEFORE returning.
    /// Exceptions propagate back to <see cref="UpdateNotificationViewModel"/>
    /// which already catches and displays them, so a failed pkexec or
    /// cp surfaces as a visible error in the update banner instead of
    /// being swallowed. Once the copy succeeds, we spawn the new
    /// VPNRouter.App detached and let the caller (UpdateNotificationVm)
    /// do <c>Environment.Exit(0)</c>.
    /// </para>
    /// </summary>
    private void ApplyUpdateLinux(string extractedDir)
    {
        // v2.22.0-r1: top-to-bottom overhaul — everything gets written to
        // ~/.config/vpnrouter/logs/update.log with exit codes + stderr,
        // new PID is verified before we exit, and an install-receipt is
        // dropped so next launch can detect "tried to update but running
        // an older version" and surface it in the UI.
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
            sourceDir = extractedDir; // legacy / CLI-stripped layout
        Log($"Effective source: {sourceDir}");

        var installDir = AppContext.BaseDirectory.TrimEnd('/');
        Log($"Install dir: {installDir}");

        // AppImage: BaseDirectory lives on a FUSE mount under /tmp/.mount_*
        // Writing there while the AppImage is running is not safe.
        if (installDir.Contains("/.mount_", StringComparison.OrdinalIgnoreCase) ||
            installDir.StartsWith("/tmp/", StringComparison.OrdinalIgnoreCase))
        {
            Log("ABORT: install dir is an AppImage mount — auto-update not supported");
            throw new InvalidOperationException(
                "AppImage auto-update is not yet supported. " +
                "Please download the new VPNRouter-linux-x86_64.AppImage " +
                "manually from the Releases page.");
        }

        // .deb installs live under /opt/vpnrouter, owned by root. Plain
        // user-owned cp fails with EPERM. Use pkexec for the privileged
        // copy; tar.gz / user-extracted installs don't need it.
        var needsRoot = installDir.StartsWith("/opt/", StringComparison.OrdinalIgnoreCase) ||
                        installDir.StartsWith("/usr/", StringComparison.OrdinalIgnoreCase);
        Log($"Needs root (pkexec): {needsRoot}");

        if (needsRoot)
        {
            // v2.22.2-r8: all three privileged steps (pkill sing-box, cp,
            // chmod) are now collapsed into a single invocation of our
            // update-helper shipped with the .deb at
            // /usr/libexec/vpnrouter-update-helper. That helper is
            // whitelisted in /usr/share/polkit-1/actions/com.vpnrouter.update.policy
            // with allow_active=yes — so a locally-logged-in user doesn't
            // get the polkit password prompt at all. Falls back to admin
            // auth if the policy file is missing (e.g. tar.gz install
            // that skipped the .deb postinst).
            const string helper = "/usr/libexec/vpnrouter-update-helper";
            var helperExists = File.Exists(helper);
            if (!helperExists)
            {
                Log($"WARNING: {helper} missing — falling back to inline cp/chmod (will prompt for password each time)");
                RunLegacyPrivilegedSteps(sourceDir, installDir, Log, logPath);
            }
            else
            {
                Log($"Invoking update helper via pkexec: {helper}");
                var (hExit, hOut, hErr) = RunWithCapture(
                    "/usr/bin/pkexec",
                    $"{helper} \"{sourceDir}\" \"{installDir}\"",
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
                        _   => ""
                    };
                    throw new InvalidOperationException(
                        $"Update helper failed (exit {hExit}){hint}: {Truncate(hErr, 200)}".Trim());
                }
            }
        }
        else
        {
            // User-writable install (tar.gz extracted to $HOME etc): do the
            // cp + chmod + pkill inline. No privilege escalation needed.
            RunLegacyPrivilegedSteps(sourceDir, installDir, Log, logPath);
        }

        // Drop an install receipt BEFORE attempting launch. Next boot, if
        // ReadInstallReceipt() returns a version newer than AppVersion,
        // we know the update tried but the new binary didn't come up, and
        // we can surface that in the UI / app log.
        TryWriteInstallReceipt(logPath, Log);

        // v2.29.0-r5: switched from in-process `setsid --fork` to a
        // detached shell helper. User report 2026-04-29: «приложение на
        // линуксе не перезапускается автоматически после обновления».
        //
        // Pre-r5 issue: setsid was started with RedirectStandardOutput =
        // true / RedirectStandardError = true — that creates pipes
        // connected to the parent process. setsid exits after forking,
        // but the new VPNRouter.App child inherits those pipes. When the
        // parent process exits a moment later (Environment.Exit(0)),
        // the read ends of those pipes close. Any subsequent
        // Console.Write* / Avalonia trace output from the child triggers
        // SIGPIPE → child dies silently. User sees no app, has to
        // launch by hand.
        //
        // r5 fix: write a one-shot /tmp/vpnrouter-relaunch-<pid>.sh that
        // sleeps until parent PID disappears, then nohup-launches the
        // new binary with stdio detached to /dev/null. Helper is
        // started fully detached (own session, own stdio) so its
        // lifecycle is independent of ours.
        var newAppPath = Path.Combine(installDir, "VPNRouter.App");
        Log($"Launching new binary via detached relaunch helper: {newAppPath}");
        try
        {
            var parentPid = Environment.ProcessId;
            var helperPath = Path.Combine("/tmp", $"vpnrouter-relaunch-{parentPid}.sh");
            var helperLog  = Path.Combine("/tmp", $"vpnrouter-relaunch-{parentPid}.log");
            var helperScript =
                "#!/bin/sh\n" +
                "set +e\n" +
                $"exec >>'{helperLog}' 2>&1\n" +
                $"echo \"[$(date -u +%H:%M:%S)] vpnrouter-relaunch helper started, parent={parentPid}\"\n" +
                // Wait for parent process to die (max 30 s; bail out earlier if it goes).
                $"for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do\n" +
                $"  if ! kill -0 {parentPid} 2>/dev/null; then\n" +
                $"    break\n" +
                $"  fi\n" +
                $"  sleep 0.2\n" +
                $"done\n" +
                $"echo \"[$(date -u +%H:%M:%S)] parent gone, launching {newAppPath}\"\n" +
                // setsid + nohup + detached stdio = fully independent child.
                $"setsid --fork nohup '{newAppPath}' </dev/null >/dev/null 2>&1\n" +
                $"echo \"[$(date -u +%H:%M:%S)] setsid returned $?\"\n" +
                $"rm -f '{helperPath}'\n";
            File.WriteAllText(helperPath, helperScript);
            try { File.SetUnixFileMode(helperPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { }

            // Detach the helper. Don't redirect stdio — we don't need to
            // see its output (the helper logs to /tmp/vpnrouter-relaunch-*.log
            // itself), and any pipe creation here would re-introduce the
            // SIGPIPE-on-parent-exit hazard we're fixing.
            var psi = new ProcessStartInfo("/bin/sh", $"'{helperPath}'")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = installDir,
            };
            using var helperProc = Process.Start(psi);
            if (helperProc == null)
            {
                Log("FATAL: relaunch helper Process.Start returned null");
                throw new InvalidOperationException(
                    $"Failed to launch relaunch helper. See {logPath}.");
            }
            // Don't wait for the helper — it will outlive us. Just give
            // it a tiny window to fail-fast on missing /bin/sh.
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

    /// <summary>
    /// Called once at app startup. If a previous update wrote a receipt
    /// and the currently-running version is older than (or equal to) what
    /// the receipt recorded, the update didn't land properly. Returns a
    /// short human-readable warning string, or null if everything's fine.
    /// The receipt is consumed (deleted) on successful matching.
    /// </summary>
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

            // If running version is STRICTLY NEWER, the update landed.
            if (cur.CompareTo(prev) > 0)
            {
                TryDelete(receiptPath);
                return null;
            }

            // Running same-or-older than the pre-update marker → update failed
            var updateLogPath = Path.Combine(AppPaths.LogsDir, "update.log");
            return $"Last update attempt did not take effect. Still running {currentVersion}. " +
                   $"See {updateLogPath} for details.";
        }
        catch { return null; }

        static void TryDelete(string p) { try { File.Delete(p); } catch { } }
    }

    /// <summary>
    /// Fallback path for when the privileged update helper isn't installed
    /// (e.g. user is running a tar.gz install instead of the .deb, or the
    /// .deb postinst didn't run). Runs pkill + cp + chmod inline. Each
    /// step still goes through pkexec if needsRoot, so users get 3
    /// password prompts — the whole point of the helper + polkit policy
    /// is to avoid this, but we keep the code so pre-policy environments
    /// still work.
    /// </summary>
    private void RunLegacyPrivilegedSteps(string sourceDir, string installDir,
        Action<string> log, string logPath)
    {
        var needsRoot = installDir.StartsWith("/opt/", StringComparison.OrdinalIgnoreCase) ||
                        installDir.StartsWith("/usr/", StringComparison.OrdinalIgnoreCase);

        // 1. Stop sing-box (ignore failure if not running)
        try
        {
            var (_, _, kserr) = needsRoot
                ? RunWithCapture("/usr/bin/pkexec", "pkill -f sing-box", 5000)
                : RunWithCapture("/usr/bin/pkill",  "-f sing-box",       5000);
            log($"pkill sing-box: stderr={Truncate(kserr)}");
        }
        catch (Exception ex) { log($"pkill sing-box threw: {ex.Message}"); }

        // 2. cp -rfT
        {
            var cpCmd  = needsRoot ? "/usr/bin/pkexec" : "/bin/cp";
            var cpArgs = needsRoot
                ? $"cp -rfT \"{sourceDir}\" \"{installDir}\""
                : $"-rfT \"{sourceDir}\" \"{installDir}\"";
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

        // 3. chmod +x (best effort)
        try
        {
            var chmodCmd  = needsRoot ? "/usr/bin/pkexec" : "/bin/chmod";
            var chmodArgs = needsRoot
                ? $"chmod +x \"{installDir}/VPNRouter.App\" \"{installDir}/sing-box\""
                : $"+x \"{installDir}/VPNRouter.App\" \"{installDir}/sing-box\"";
            var (chExit, _, chErr) = RunWithCapture(chmodCmd, chmodArgs, 10_000);
            log($"chmod exit={chExit} stderr={Truncate(chErr)}");
        }
        catch (Exception ex) { log($"chmod threw: {ex.Message}"); }
    }

    // ─── Update log + install receipt helpers ────────────────────────────────

    private static StreamWriter OpenUpdateLog(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new StreamWriter(path, append: true);
        }
        catch
        {
            // Fallback: throwaway in-memory writer so Log() calls don't throw
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

    /// <summary>v2.29.0-r7 — receipt-write overload for Windows
    /// updater (no log file). Writes the same .update-installed-version
    /// receipt as the Linux flow. On next startup
    /// <see cref="CheckInstallReceipt"/> compares the running version to
    /// this receipt; if not strictly newer, surfaces "Last update didn't
    /// take effect" warning to the UI.</summary>
    private void TryWriteInstallReceipt()
    {
        try
        {
            var receiptPath = Path.Combine(AppPaths.DataDir, ".update-installed-version");
            File.WriteAllText(receiptPath, $"{DateTime.UtcNow:o}\n{_currentVersion}\n");
        }
        catch
        {
            // Non-fatal — best-effort receipt; CheckInstallReceipt
            // returns null gracefully if the file is missing.
        }
    }

    /// <summary>
    /// Start a process, wait for exit (with timeout), capture stdout + stderr.
    /// Returns (exitCode, stdout, stderr). On timeout, kills the process and
    /// returns exitCode = -1.
    /// </summary>
    private static (int exit, string stdout, string stderr) RunWithCapture(
        string fileName, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "timeout");
        }
        // Ensure async reads complete
        try { outTask.Wait(500); } catch { }
        try { errTask.Wait(500); } catch { }
        return (proc.ExitCode,
                outTask.IsCompletedSuccessfully ? outTask.Result : "",
                errTask.IsCompletedSuccessfully ? errTask.Result : "");
    }

    private static string Truncate(string s, int max = 120)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    /// <summary>
    /// Walk up from AppContext.BaseDirectory to find the .app bundle.
    /// e.g. /Applications/VPNRouter.app/Contents/MacOS/ → /Applications/VPNRouter.app
    /// </summary>
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

    // CopyDirectoryRecursive was the per-file copy helper used by the old
    // Mac updater. v2.18.1 retired it because File.Copy drops the Unix
    // execute bit and doesn't preserve xattrs — both fatal on macOS.
    // The new updater shells out to `ditto` which handles all of that.
    // Windows still uses an inline per-file File.Copy loop above; it's
    // fine on NTFS where execute permissions don't apply.

    // ─── Asset matching ──────────────────────────────────────────────────────

    /// <summary>
    /// Find the full install asset for the current platform.
    /// v2.0+: VPNRouter-v2.0.0-win.zip / VPNRouter-v2.0.0-mac.zip
    /// v2.21.0+: VPNRouter-v2.21.0-linux.tar.gz (Linux uses a tarball, not a zip)
    /// Legacy: VPNRouter-install-v*.zip (Windows only, no platform suffix)
    /// </summary>
    private static dynamic? FindFullAsset(dynamic[]? assets)
    {
        if (assets == null) return null;

        // v2.21.0: Linux ships as .tar.gz instead of .zip to preserve Unix
        // execute bits on extract and avoid chmod +x dance in ApplyUpdate.
        var extension = OperatingSystem.IsLinux() ? ".tar.gz" : ".zip";

        // New naming: VPNRouter-v*-{platform}{ext} (not containing "update")
        var newFormat = ((IEnumerable<dynamic>)assets).FirstOrDefault(a =>
        {
            string name = a.name;
            return name.StartsWith("VPNRouter-v", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith($"{PlatformSuffix}{extension}", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("update", StringComparison.OrdinalIgnoreCase);
        });
        if (newFormat != null) return newFormat;

        // Legacy (Windows only): VPNRouter-install-v*.zip
        if (OperatingSystem.IsWindows())
        {
            return ((IEnumerable<dynamic>)assets).FirstOrDefault(a =>
            {
                string name = a.name;
                return name.StartsWith("VPNRouter-install-v", StringComparison.OrdinalIgnoreCase) &&
                       name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            });
        }

        return null;
    }

    /// <summary>
    /// Find the lite update asset (Windows only).
    /// v2.0+: VPNRouter-update-v2.0.0-win.zip
    /// Legacy: VPNRouter-update-v*.zip
    /// </summary>
    private static dynamic? FindLiteAsset(dynamic[]? assets)
    {
        if (assets == null || !OperatingSystem.IsWindows()) return null;

        var newFormat = ((IEnumerable<dynamic>)assets).FirstOrDefault(a =>
        {
            string name = a.name;
            return name.StartsWith("VPNRouter-update-v", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith($"{PlatformSuffix}.zip", StringComparison.OrdinalIgnoreCase);
        });
        if (newFormat != null) return newFormat;

        return ((IEnumerable<dynamic>)assets).FirstOrDefault(a =>
        {
            string name = a.name;
            return name.StartsWith("VPNRouter-update-v", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Find the .sha256 companion file for a given ZIP asset. Naming convention
    /// (set by build.ps1 in v2.15.8+): "{zipName}.sha256". Returns null if the
    /// companion is missing — in that case we fall back to size-only validation.
    /// </summary>
    private static dynamic? FindChecksumAsset(dynamic[]? assets, dynamic? zipAsset)
    {
        if (assets == null || zipAsset == null) return null;
        string zipName = zipAsset.name;
        var target = $"{zipName}.sha256";
        return ((IEnumerable<dynamic>)assets).FirstOrDefault(a =>
            string.Equals((string)a.name, target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Validate extracted content based on platform.
    /// </summary>
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
            // v2.22.2: Linux tarball extracts to a "VPNRouter/" top-level dir
            // (set by build-linux during the tar -czf stage) or occasionally
            // CLI-stripped flat layout if repacked. Check both. The binary
            // on Linux is "VPNRouter.App" (no .exe suffix) — validating the
            // Windows name here produced the "VPNRouter.GUI.exe/dll not
            // found" error users saw in v2.22.2-r4.
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

        // Windows: support flat and app/ layout
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

    /// <summary>
    /// Shared runtime detection. Only meaningful on Windows — macOS has no lite update.
    /// </summary>
    private static bool IsSharedRuntimeInstall()
    {
        if (OperatingSystem.IsMacOS()) return false;
        var appDir = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(appDir, "hostfxr.dll"));
    }

    // ─── Versioning helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Lightweight semver-ish version with optional rolling release candidate
    /// number. Supports:
    /// <list type="bullet">
    ///   <item>"2.22.0"        → Core=2.22.0.0, Rc=null</item>
    ///   <item>"2.22.0-r1"     → Core=2.22.0.0, Rc=1</item>
    ///   <item>"2.22.0-r12"    → Core=2.22.0.0, Rc=12</item>
    /// </list>
    /// Comparison: Core first; if equal, Rc=null (stable) sorts above any Rc
    /// value (a release candidate is "less than" its final stable), otherwise
    /// numeric Rc comparison. So 2.22.0-r1 &lt; 2.22.0-r2 &lt; 2.22.0.
    ///
    /// Tags with suffixes that aren't plain `-rN` (e.g. "1.0.0-mac",
    /// "2.0.0-beta.1") return false — they're not part of the rolling scheme
    /// and would otherwise be picked up by UpdateChecker by accident.
    /// </summary>
    internal readonly struct SemVer : IComparable<SemVer>, IEquatable<SemVer>
    {
        public readonly Version Core;
        public readonly int? Rc;

        public SemVer(Version core, int? rc) { Core = core; Rc = rc; }

        public int CompareTo(SemVer other)
        {
            var c = Core.CompareTo(other.Core);
            if (c != 0) return c;
            // null (stable) > any rN
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

        // Strip any leading 'v' once more (defensive)
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

        // Only accept -rN where N is a non-negative integer. Anything else
        // (e.g. "-mac", "-beta.1") is intentionally rejected so platform-
        // specific legacy tags don't poison the update flow.
        if (suffix.Length < 2 || (suffix[0] != 'r' && suffix[0] != 'R'))
            return false;
        if (!int.TryParse(suffix.AsSpan(1), out var rc) || rc < 0)
            return false;

        result = new SemVer(core, rc);
        return true;
    }
}
