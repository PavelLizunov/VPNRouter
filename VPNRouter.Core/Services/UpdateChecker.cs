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

        var zipPath = Path.Combine(_stagingDir, $"VPNRouter-v{info.LatestVersion}.zip");

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

    private static void ApplyUpdateWindows(string extractedDir)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd('\\');

        // Strip app/ wrapper from install ZIP layout
        var appSubDir = Path.Combine(extractedDir, "app");
        if (Directory.Exists(appSubDir) &&
            (File.Exists(Path.Combine(appSubDir, "VPNRouter.GUI.exe")) ||
             File.Exists(Path.Combine(appSubDir, "VPNRouter.GUI.dll"))))
        {
            extractedDir = appSubDir;
        }

        var guiExe = Path.Combine(appDir, "VPNRouter.GUI.exe");

        // Kill sing-box before copying — it's a running process that locks
        // its exe file. Without this, sing-box.exe silently fails to update.
        foreach (var proc in Process.GetProcessesByName("sing-box"))
        {
            try { proc.Kill(entireProcessTree: true); proc.WaitForExit(3000); }
            catch { }
            finally { proc.Dispose(); }
        }

        foreach (var srcFile in Directory.GetFiles(extractedDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(extractedDir, srcFile);
            var destPath = Path.Combine(appDir, relativePath);

            var destDir = Path.GetDirectoryName(destPath);
            if (destDir != null)
                Directory.CreateDirectory(destDir);

            try
            {
                File.Copy(srcFile, destPath, overwrite: true);
            }
            catch (IOException)
            {
                var bakPath = destPath + ".bak";
                try { File.Delete(bakPath); } catch { }
                try { File.Move(destPath, bakPath); } catch { }
                try { File.Copy(srcFile, destPath); } catch { }
            }
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = guiExe,
            WorkingDirectory = Path.GetDirectoryName(guiExe)!,
            UseShellExecute = false
        });
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

        // Stop sing-box first so the updater doesn't collide with a running
        // root process that keeps open file descriptors on the binary.
        // Ignore failures — if sing-box isn't running, pkill returns 1.
        try
        {
            var (_, ksout, kserr) = needsRoot
                ? RunWithCapture("/usr/bin/pkexec", "pkill -f sing-box", 5000)
                : RunWithCapture("/usr/bin/pkill",  "-f sing-box",       5000);
            Log($"pkill sing-box: stdout={Truncate(ksout)} stderr={Truncate(kserr)}");
        }
        catch (Exception ex) { Log($"pkill sing-box threw: {ex.Message}"); }

        // Synchronous copy. -rfT (recursive, force, treat dest as regular
        // dir) — the T flag makes cp copy source's CONTENTS into dest
        // instead of creating dest/source. Requires GNU coreutils' cp.
        {
            var cpCmd  = needsRoot ? "/usr/bin/pkexec" : "/bin/cp";
            var cpArgs = needsRoot
                ? $"cp -rfT \"{sourceDir}\" \"{installDir}\""
                : $"-rfT \"{sourceDir}\" \"{installDir}\"";
            Log($"cp cmd: {cpCmd} {cpArgs}");
            var (cpExit, cpOut, cpErr) = RunWithCapture(cpCmd, cpArgs, timeoutMs: 120_000);
            Log($"cp exit={cpExit} stdout={Truncate(cpOut)} stderr={Truncate(cpErr)}");
            if (cpExit != 0)
            {
                var hint = cpExit switch
                {
                    126 => " (authentication dialog was dismissed)",
                    127 => " (pkexec / polkit agent not available — install policykit-1 and try again)",
                    _   => ""
                };
                throw new InvalidOperationException(
                    $"Update copy failed (exit {cpExit}){hint}: {Truncate(cpErr, 200)}".Trim());
            }
        }

        // chmod +x on the newly-written binaries. File.Copy / cp normally
        // preserves the mode bits on Linux, but tar extraction quirks and
        // weird umasks mean a belt-and-braces chmod is cheap insurance.
        try
        {
            var chmodCmd  = needsRoot ? "/usr/bin/pkexec" : "/bin/chmod";
            var chmodArgs = needsRoot
                ? $"chmod +x \"{installDir}/VPNRouter.App\" \"{installDir}/sing-box\""
                : $"+x \"{installDir}/VPNRouter.App\" \"{installDir}/sing-box\"";
            var (chExit, _, chErr) = RunWithCapture(chmodCmd, chmodArgs, 10_000);
            Log($"chmod exit={chExit} stderr={Truncate(chErr)}");
            if (chExit != 0 && chExit != 126 && chExit != 127)
                Log($"WARNING: chmod non-zero exit {chExit} — launch may fail");
        }
        catch (Exception ex) { Log($"chmod threw: {ex.Message}"); }

        // Drop an install receipt BEFORE attempting launch. Next boot, if
        // ReadInstallReceipt() returns a version newer than AppVersion,
        // we know the update tried but the new binary didn't come up, and
        // we can surface that in the UI / app log.
        TryWriteInstallReceipt(logPath, Log);

        // Launch the new version with stdout/stderr captured briefly so we
        // can detect immediate startup failures. If the new process dies
        // within 2s (exit code != null), throw and KEEP the old VPNRouter
        // running — better to show "update launch failed" than to fall
        // into the void.
        var newAppPath = Path.Combine(installDir, "VPNRouter.App");
        Log($"Launching new binary: {newAppPath}");
        Process? child = null;
        try
        {
            child = Process.Start(new ProcessStartInfo(newAppPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log($"FATAL: Process.Start failed: {ex.Message}");
            throw new InvalidOperationException(
                $"Failed to launch new VPNRouter.App: {ex.Message}. " +
                $"See {logPath} for details.");
        }

        if (child == null)
        {
            Log("FATAL: Process.Start returned null");
            throw new InvalidOperationException(
                $"Failed to launch new VPNRouter.App (null handle). See {logPath}.");
        }

        Log($"New process started, pid={child.Id}. Waiting 2s to verify...");
        var alive = !child.WaitForExit(2000);
        if (!alive)
        {
            var earlyExit = child.ExitCode;
            Log($"FATAL: new binary died within 2s, exit code {earlyExit}");
            throw new InvalidOperationException(
                $"New VPNRouter.App exited immediately with code {earlyExit}. " +
                $"See {logPath} for details. Old version still running.");
        }
        Log($"New process alive after 2s — update successful, exiting old instance");
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
