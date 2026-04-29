// Backwards-compat launcher stub for VPNRouter, with v2.29.0-r6
// post-update bootstrap.
//
// Why this exists:
//   - Old auto-updater (v2.3.x WinForms) validates the update package by
//     checking that VPNRouter.GUI.exe exists inside.
//   - Old desktop shortcuts and Start Menu entries point to VPNRouter.GUI.exe.
//   - v2.29.0-r6: serves as the post-update bootstrap for users on the
//     pre-r6 broken in-process updater. See "Bootstrap mode" below.
//
// Normal mode:
//   - Locates VPNRouter.App.exe in the same directory and launches it,
//     forwarding all command-line args.
//
// Bootstrap mode (v2.29.0-r6+):
//   - Triggered by presence of `_bootstrap/` next to this exe.
//   - User report 2026-04-29: «обновление завершается приложение
//     перезапускается но снова со старой версией ... на всех windows
//     чтоли сломалось обновление? категорически нет качать вручную».
//   - Pre-r6 broken updater (shipped in v2.28.x and earlier) tried to
//     overwrite locked .NET runtime DLLs in-process; ~10% of files
//     stayed old; relaunch loaded a mixed-version DLL set → user saw
//     same old version after "Update".
//   - r6 fix: ship update ZIP with everything in `_bootstrap/`. Pre-r6
//     ApplyUpdateWindows walks the ZIP recursively and copies each file
//     (including those under _bootstrap/) to appDir at relative paths.
//     None of those copies are blocked because _bootstrap/ is a fresh
//     subdir — nothing locked, nothing fails.
//   - Pre-r6 ApplyUpdate then `Process.Start`s `VPNRouter.GUI.exe` —
//     which it ALSO copied (the only file at root) successfully (Go
//     stub, ~2 MB, freestanding native binary, never mapped by .NET
//     runtime → never locked).
//   - This NEW Go stub takes over: waits for parent VPNRouter.App.exe
//     to die (now no DLLs locked), xcopies _bootstrap/* over appDir,
//     deletes _bootstrap/, then launches the freshly-replaced App.exe.
//
// Why Go:
//   - Compiles to a native ~2 MB exe with zero runtime dependency.
//   - Old Windows machines without .NET 8 runtime can run this stub.
//   - Not part of the .NET runtime → never locked while VPNRouter.App
//     is running → safely replaceable by the broken pre-r6 updater.
package main

import (
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"time"
)

const (
	// Max time we wait for parent VPNRouter.App.exe to exit before we
	// proceed with the bootstrap copy. 30 s is generous; pre-r6 updater
	// calls Environment.Exit immediately after Process.Start returns
	// (typically <100 ms wall-clock to actually unmap).
	bootstrapWaitParentMaxSeconds = 30
	// After parent exits, give Windows a moment to fully release file
	// handles (kernel cleanup of mapped pages is async).
	bootstrapPostParentDelayMs = 750
	// Per-file copy retry count + spacing — for the rare case the file
	// was just released by another process.
	copyAttempts    = 5
	copyAttemptWait = 500 * time.Millisecond
)

func main() {
	self, err := os.Executable()
	if err != nil {
		os.Exit(1)
	}
	dir := filepath.Dir(self)

	// Bootstrap mode: if _bootstrap/ exists, this is a post-update
	// relaunch from a pre-r6 broken ApplyUpdateWindows that couldn't
	// replace locked DLLs in-process. We finish the job here.
	bootstrapDir := filepath.Join(dir, "_bootstrap")
	if info, err := os.Stat(bootstrapDir); err == nil && info.IsDir() {
		// Best-effort log so postmortem is possible. Failures here are
		// non-fatal — old App.exe is still launchable below.
		logPath := filepath.Join(os.TempDir(), "vpnrouter-bootstrap.log")
		_ = runBootstrap(dir, bootstrapDir, logPath)
	}

	target := filepath.Join(dir, "VPNRouter.App.exe")
	if _, err := os.Stat(target); err != nil {
		os.Exit(2)
	}

	// Forward arguments and launch detached so the stub exits immediately.
	cmd := exec.Command(target, os.Args[1:]...)
	cmd.Dir = dir
	cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow:    false,
		CreationFlags: 0x00000008, // DETACHED_PROCESS — stub exits, app keeps running
	}
	if err := cmd.Start(); err != nil {
		os.Exit(3)
	}
	os.Exit(0)
}

// runBootstrap executes the post-update file copy in two phases:
//   1. Wait for any other VPNRouter.App.exe process to exit (the parent
//      that just called us has DLLs locked; we can't write them yet).
//   2. xcopy _bootstrap/* → dir, overwriting; then rmdir _bootstrap.
// Returns nil on success; otherwise the first error encountered. Errors
// are non-fatal — the caller falls through to launching App.exe with
// whatever DLL set is on disk.
func runBootstrap(dir, bootstrapDir, logPath string) error {
	// Open log (append). Truncate if previous run is older than 24h
	// would be nice, but not worth the complexity for a recovery file.
	logFile, _ := os.OpenFile(logPath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
	if logFile != nil {
		defer logFile.Close()
		fmt.Fprintf(logFile, "[%s] vpnrouter-bootstrap start, dir=%s\n",
			time.Now().UTC().Format("15:04:05"), dir)
	}
	logf := func(format string, args ...interface{}) {
		if logFile != nil {
			line := fmt.Sprintf(format, args...)
			fmt.Fprintf(logFile, "[%s] %s\n",
				time.Now().UTC().Format("15:04:05"), line)
		}
	}

	// Phase 1: wait for parent App.exe to exit.
	logf("waiting for VPNRouter.App.exe to exit (max %ds)", bootstrapWaitParentMaxSeconds)
	deadline := time.Now().Add(time.Duration(bootstrapWaitParentMaxSeconds) * time.Second)
	parentGone := false
	for time.Now().Before(deadline) {
		if !isProcessRunning("VPNRouter.App.exe") {
			parentGone = true
			break
		}
		time.Sleep(200 * time.Millisecond)
	}
	if !parentGone {
		logf("WARNING: VPNRouter.App.exe still running after %ds, proceeding anyway",
			bootstrapWaitParentMaxSeconds)
	} else {
		logf("parent gone, sleeping %dms before file copy", bootstrapPostParentDelayMs)
		time.Sleep(time.Duration(bootstrapPostParentDelayMs) * time.Millisecond)
	}

	// Phase 2: walk _bootstrap/, copy each entry to dir at relative path.
	logf("copying _bootstrap/* over %s", dir)
	copied := 0
	failed := 0
	walkErr := filepath.Walk(bootstrapDir, func(srcPath string, info os.FileInfo, err error) error {
		if err != nil {
			logf("walk error at %s: %v", srcPath, err)
			return nil // skip but continue
		}
		relPath, relErr := filepath.Rel(bootstrapDir, srcPath)
		if relErr != nil || relPath == "." {
			return nil
		}
		destPath := filepath.Join(dir, relPath)
		if info.IsDir() {
			if err := os.MkdirAll(destPath, 0755); err != nil {
				logf("mkdir %s: %v", destPath, err)
			}
			return nil
		}
		if err := copyFileWithRetry(srcPath, destPath); err != nil {
			failed++
			logf("copy %s -> %s: %v", relPath, destPath, err)
		} else {
			copied++
		}
		return nil
	})
	logf("walk done: copied=%d failed=%d walkErr=%v", copied, failed, walkErr)

	// Phase 3: cleanup _bootstrap/.
	if err := os.RemoveAll(bootstrapDir); err != nil {
		logf("cleanup _bootstrap/: %v", err)
		return err
	}
	logf("bootstrap cleanup done; relaunching App.exe")
	return nil
}

// copyFileWithRetry handles the rare case where a file was just released
// by another process — retry a few times before giving up.
func copyFileWithRetry(src, dst string) error {
	var lastErr error
	for attempt := 0; attempt < copyAttempts; attempt++ {
		if err := copyFile(src, dst); err == nil {
			return nil
		} else {
			lastErr = err
		}
		time.Sleep(copyAttemptWait)
	}
	return lastErr
}

func copyFile(src, dst string) error {
	s, err := os.Open(src)
	if err != nil {
		return err
	}
	defer s.Close()

	// Ensure parent directory exists (defensive — should be created by
	// the walk's directory-entry handler, but cover the case where the
	// directory got cleaned up between iterations).
	if err := os.MkdirAll(filepath.Dir(dst), 0755); err != nil {
		return err
	}

	d, err := os.OpenFile(dst, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0644)
	if err != nil {
		return err
	}
	defer d.Close()

	if _, err := io.Copy(d, s); err != nil {
		return err
	}
	return d.Sync()
}

// isProcessRunning checks whether any process matching `name` (case-
// insensitive imagename) is currently running. Uses tasklist /FI which
// is built-in to every Windows since XP and doesn't require admin.
func isProcessRunning(name string) bool {
	cmd := exec.Command("tasklist", "/FI", "IMAGENAME eq "+name, "/NH", "/FO", "CSV")
	out, err := cmd.Output()
	if err != nil {
		return false
	}
	// CSV format: "VPNRouter.App.exe","12345","Console","1","50,000 K"
	// On no match, tasklist prints "INFO: No tasks..." or empty.
	return strings.Contains(strings.ToLower(string(out)), strings.ToLower(name))
}
