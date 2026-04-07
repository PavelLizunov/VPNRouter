// Backwards-compat launcher stub for VPNRouter.
//
// Why this exists:
//   - Old auto-updater (v2.3.x WinForms) validates the update package by
//     checking that VPNRouter.GUI.exe exists inside.
//   - Old desktop shortcuts and Start Menu entries point to VPNRouter.GUI.exe.
//
// What it does:
//   - Locates VPNRouter.App.exe in the same directory and launches it,
//     forwarding all command-line args.
//
// Why Go:
//   - Compiles to a native ~2MB exe with zero runtime dependency.
//   - Old Windows machines without .NET 8 runtime can run this stub.
package main

import (
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
)

func main() {
	// Determine where this exe lives.
	self, err := os.Executable()
	if err != nil {
		os.Exit(1)
	}
	dir := filepath.Dir(self)
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
