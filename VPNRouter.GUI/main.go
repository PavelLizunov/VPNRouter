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
	bootstrapWaitParentMaxSeconds = 30
	bootstrapPostParentDelayMs = 750
	copyAttempts    = 5
	copyAttemptWait = 500 * time.Millisecond
)

const trampolineRepairDeadline = 5 * time.Minute

func main() {
	self, err := os.Executable()
	if err != nil {
		os.Exit(1)
	}
	dir := filepath.Dir(self)

	trampolineLog := filepath.Join(os.TempDir(), "vpnrouter-trampoline.log")
	logFile, _ := os.OpenFile(trampolineLog,
		os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
	if logFile != nil {
		defer logFile.Close()
	}
	logf := func(format string, args ...interface{}) {
		if logFile == nil {
			return
		}
		fmt.Fprintf(logFile, "[%s] %s\n",
			time.Now().UTC().Format("2006-01-02 15:04:05"),
			fmt.Sprintf(format, args...))
	}
	logf("trampoline start dir=%s channel=%s", dir, ChannelHint)

	bootstrapDir := filepath.Join(dir, "_bootstrap")
	if info, err := os.Stat(bootstrapDir); err == nil && info.IsDir() {
		logPath := filepath.Join(os.TempDir(), "vpnrouter-bootstrap.log")
		_ = runBootstrap(dir, bootstrapDir, logPath)
	}

	rep := CheckIntegrity(dir)
	logf("integrity hashes=%v skip=%v mismatched=%v",
		rep.Hashes, rep.SkipReasons, rep.Mismatched)

	if rep.Mismatched {
		if recentRepair() {
			logf("mismatch detected but repair marker recent (<%v); falling through", RepairCooldown)
		} else {
			logf("mismatch confirmed; running repair (prerelease=%v)", IsPrerelease())
			touchMarker()
			elapsed, runErr := RunRepair(IsPrerelease(), trampolineRepairDeadline)
			if runErr != nil {
				logf("repair returned error after %v: %v", elapsed, runErr)
			} else {
				logf("repair returned ok in %v", elapsed)
			}
			postRep := CheckIntegrity(dir)
			logf("post-repair hashes=%v mismatched=%v",
				postRep.Hashes, postRep.Mismatched)
		}
	}

	target := filepath.Join(dir, "VPNRouter.App.exe")
	if _, err := os.Stat(target); err != nil {
		logf("App.exe missing at %s, exiting (CLR error path)", target)
		os.Exit(2)
	}

	cmd := exec.Command(target, os.Args[1:]...)
	cmd.Dir = dir
	cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow:    false,
		CreationFlags: 0x00000008,
	}
	if err := cmd.Start(); err != nil {
		logf("CreateProcess App.exe failed: %v", err)
		os.Exit(3)
	}
	logf("App.exe launched pid=%d", cmd.Process.Pid)
	os.Exit(0)
}

func runBootstrap(dir, bootstrapDir, logPath string) error {
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

	logf("copying _bootstrap/* over %s", dir)
	copied := 0
	failed := 0
	walkErr := filepath.Walk(bootstrapDir, func(srcPath string, info os.FileInfo, err error) error {
		if err != nil {
			logf("walk error at %s: %v", srcPath, err)
			return nil
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

	if err := os.RemoveAll(bootstrapDir); err != nil {
		logf("cleanup _bootstrap/: %v", err)
		return err
	}
	logf("bootstrap cleanup done; relaunching App.exe")
	return nil
}

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

func isProcessRunning(name string) bool {
	cmd := exec.Command("tasklist", "/FI", "IMAGENAME eq "+name, "/NH", "/FO", "CSV")
	out, err := cmd.Output()
	if err != nil {
		return false
	}
	return strings.Contains(strings.ToLower(string(out)), strings.ToLower(name))
}
