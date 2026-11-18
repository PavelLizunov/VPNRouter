package main

import (
	"context"
	_ "embed"
	"encoding/base64"
	"encoding/binary"
	"fmt"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"time"
	"unicode/utf16"
	"unsafe"
)

//go:embed repair-bootstrap.ps1
var repairBootstrap string

// Set by the release build alongside ChannelHint. A launcher with no known
// edition cannot choose the public installer for a damaged installation.
var RepairEdition = ""

func canUsePublicRepair(edition string) bool {
	return edition == "Standard"
}

func repairArgs(appDir string, prerelease bool) []string {
	flag := "$false"
	if prerelease {
		flag = "$true"
	}
	source := strings.NewReplacer("__APPLICATION_DIR__", base64.StdEncoding.EncodeToString([]byte(appDir)), "__PRERELEASE__", flag).Replace(repairBootstrap)
	chars := utf16.Encode([]rune(source))
	b := make([]byte, 2*len(chars))
	for i, ch := range chars {
		binary.LittleEndian.PutUint16(b[2*i:], ch)
	}
	return []string{"-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", base64.StdEncoding.EncodeToString(b)}
}

func systemDirectory() (string, error) {
	// kernel32 is a Windows KnownDLL. Resolve the native directory independently of the caller's environment.
	buffer := make([]uint16, 32768)
	n, _, err := syscall.NewLazyDLL("kernel32.dll").NewProc("GetSystemDirectoryW").Call(uintptr(unsafe.Pointer(&buffer[0])), uintptr(len(buffer)))
	if n == 0 || n >= uintptr(len(buffer)) {
		return "", fmt.Errorf("GetSystemDirectoryW: %v", err)
	}
	return syscall.UTF16ToString(buffer[:n]), nil
}

func RunRepair(appDir string, prerelease bool, deadline time.Duration) (time.Duration, error) {
	if !canUsePublicRepair(RepairEdition) {
		return 0, fmt.Errorf("automatic public repair is unavailable for this edition")
	}
	start := time.Now()
	system, err := systemDirectory()
	if err != nil {
		return 0, err
	}
	ctx, cancel := context.WithTimeout(context.Background(), deadline)
	defer cancel()
	cmd := windowsSystemCommand(ctx, system, filepath.Join("WindowsPowerShell", "v1.0", "powershell.exe"), repairArgs(appDir, prerelease)...)
	err = cmd.Run()
	return time.Since(start), err
}

func windowsSystemCommand(ctx context.Context, system, executable string, args ...string) *exec.Cmd {
	shellDir := filepath.Join(system, "WindowsPowerShell", "v1.0")
	cmd := exec.CommandContext(ctx, filepath.Join(system, executable), args...)
	cmd.Dir = system
	cmd.Env = []string{
		"SystemRoot=" + filepath.Dir(system), "WINDIR=" + filepath.Dir(system),
		"PATH=" + system + ";" + shellDir, "PSModulePath=" + filepath.Join(shellDir, "Modules"),
		"ComSpec=" + filepath.Join(system, "cmd.exe"), "SystemDrive=" + filepath.VolumeName(system),
		"PATHEXT=.COM;.EXE;.BAT;.CMD",
	}
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x08000000}
	return cmd
}
