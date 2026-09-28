package main

import (
	"strings"
	"testing"
)

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

func TestRepairScript_Prerelease_AddsFlag(t *testing.T) {
	script := repairScript(true)
	if !strings.Contains(script, "& $tmp -Prerelease") {
		t.Errorf("repairScript(true) missing \"& $tmp -Prerelease\"\nscript:\n%s", script)
	}
}
