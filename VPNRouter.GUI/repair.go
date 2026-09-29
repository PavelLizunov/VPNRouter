package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
	"time"
)

func RunRepair(prerelease bool, deadline time.Duration) (elapsed time.Duration, err error) {
	start := time.Now()
	defer func() { elapsed = time.Since(start) }()

	scriptPath := filepath.Join(
		os.TempDir(),
		fmt.Sprintf("vpnr-trampoline-repair-%d.ps1", time.Now().UnixNano()))
	if writeErr := os.WriteFile(scriptPath, []byte(repairScript(prerelease)), 0644); writeErr != nil {
		return elapsed, fmt.Errorf("write repair script: %w", writeErr)
	}
	defer os.Remove(scriptPath)

	cmd := exec.Command("powershell.exe", repairArgs(scriptPath)...)
	cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow:    true,
		CreationFlags: 0x08000000,
	}

	done := make(chan error, 1)
	go func() {
		done <- cmd.Run()
	}()

	select {
	case err = <-done:
		return elapsed, err
	case <-time.After(deadline):
		if cmd.Process != nil {
			_ = cmd.Process.Kill()
		}
		return elapsed, fmt.Errorf("repair timed out after %v", deadline)
	}
}

func repairScript(prerelease bool) string {
	prereleaseFlag := ""
	if prerelease {
		prereleaseFlag = " -Prerelease"
	}
	return "$ErrorActionPreference = 'Stop'\r\n" +
		"$ProgressPreference = 'SilentlyContinue'\r\n" +
		"[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12\r\n" +
		"$tmp = Join-Path $env:TEMP 'vpnr-trampoline-install.ps1'\r\n" +
		"Invoke-WebRequest -Uri 'https://vpn.ninitux.com/install.ps1' -OutFile $tmp -UseBasicParsing\r\n" +
		fmt.Sprintf("& $tmp%s\r\n", prereleaseFlag)
}

func repairArgs(scriptPath string) []string {
	return []string{
		"-NoProfile",
		"-WindowStyle", "Hidden",
		"-ExecutionPolicy", "Bypass",
		"-File", scriptPath,
	}
}
