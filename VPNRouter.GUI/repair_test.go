// repair_test.go — P01 UPD-2 regression tests for the trampoline repair.
//
// Pins the ClickFix-safe invocation shape: the repair bootstrap is written
// to a temp .ps1 and launched via `-File` (NEVER inline `-Command`, the exact
// shape Defender's Trojan:Win32/ClickFix.DCW!MTB heuristic fires on). Mirrors
// VPNRouter.App/Services/SelfRepair.cs.
//
// Run from the module dir (Windows — the package uses Windows-only
// syscall.SysProcAttr fields, so it does not cross-compile to Linux/macOS):
//
//	cd VPNRouter.GUI && go test ./...
package main

import (
	"strings"
	"testing"
)

// TestRepairArgs_UsesFileNotCommand — the argv MUST use `-File` (path as the
// final element) and MUST NOT contain `-Command`.
func TestRepairArgs_UsesFileNotCommand(t *testing.T) {
	const path = `C:\x\y.ps1`
	args := repairArgs(path)

	if strings.Contains(strings.Join(args, " "), "-Command") {
		t.Errorf("repairArgs must not use -Command (ClickFix heuristic), got: %v", args)
	}

	fileIdx := -1
	for i, a := range args {
		if a == "-File" {
			fileIdx = i
			break
		}
	}
	if fileIdx == -1 {
		t.Fatalf("repairArgs missing -File, got: %v", args)
	}
	if fileIdx != len(args)-2 {
		t.Errorf("path must be the final element immediately after -File, got: %v", args)
	}
	if args[len(args)-1] != path {
		t.Errorf("final element = %q, want %q", args[len(args)-1], path)
	}
}

// TestRepairArgs_PathWithSpaces_PreservedAsSingleArg — a path containing
// spaces must occupy exactly ONE argv slot immediately after -File (no shell
// re-splitting), which is what keeps `C:\Users\Some User\...` intact.
func TestRepairArgs_PathWithSpaces_PreservedAsSingleArg(t *testing.T) {
	const path = `C:\Users\Some User\AppData\Local\Temp\vpnr.ps1`
	args := repairArgs(path)

	fileIdx := -1
	for i, a := range args {
		if a == "-File" {
			fileIdx = i
			break
		}
	}
	if fileIdx == -1 {
		t.Fatalf("repairArgs missing -File, got: %v", args)
	}
	if fileIdx+1 >= len(args) {
		t.Fatalf("no element after -File, got: %v", args)
	}
	if args[fileIdx+1] != path {
		t.Errorf("arg after -File = %q, want %q (single element, spaces preserved)",
			args[fileIdx+1], path)
	}
	if fileIdx+2 != len(args) {
		t.Errorf("path must be the last element; trailing args present: %v", args[fileIdx+2:])
	}
}

// TestRepairScript_DownloadsAndDotExecutes — the stable-channel script keeps
// the download + dot-execute shape and does NOT carry -Prerelease.
func TestRepairScript_DownloadsAndDotExecutes(t *testing.T) {
	script := repairScript(false)

	for _, want := range []string{
		"Invoke-WebRequest",
		"https://vpn.ninitux.com/install.ps1",
		"-OutFile",
		"& $tmp",
	} {
		if !strings.Contains(script, want) {
			t.Errorf("repairScript(false) missing %q\nscript:\n%s", want, script)
		}
	}
	if strings.Contains(script, "-Prerelease") {
		t.Errorf("repairScript(false) must not contain -Prerelease\nscript:\n%s", script)
	}
}

// TestRepairScript_Prerelease_AddsFlag — the prerelease channel appends
// ` -Prerelease` to the dot-execute line.
func TestRepairScript_Prerelease_AddsFlag(t *testing.T) {
	script := repairScript(true)
	if !strings.Contains(script, "& $tmp -Prerelease") {
		t.Errorf("repairScript(true) missing \"& $tmp -Prerelease\"\nscript:\n%s", script)
	}
}
