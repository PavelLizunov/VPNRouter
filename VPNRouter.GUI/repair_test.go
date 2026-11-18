package main

import (
	"context"
	"encoding/base64"
	"encoding/binary"
	"path/filepath"
	"strings"
	"testing"
	"unicode/utf16"
)

func TestRepairLaunchKeepsDirectoryAsDataAndChannel(t *testing.T) {
	const dir = "D:\\Apps\\O'Brien & Co % 目标"
	for _, prerelease := range []bool{false, true} {
		args := repairArgs(dir, prerelease)
		if len(args) != 6 || args[4] != "-EncodedCommand" {
			t.Fatal("unexpected repair arguments")
		}
		b, err := base64.StdEncoding.DecodeString(args[5])
		if err != nil {
			t.Fatal(err)
		}
		chars := make([]uint16, len(b)/2)
		for i := range chars {
			chars[i] = binary.LittleEndian.Uint16(b[2*i:])
		}
		source := string(utf16.Decode(chars))
		if strings.Contains(source, dir) || !strings.Contains(source, base64.StdEncoding.EncodeToString([]byte(dir))) ||
			strings.Contains(source, "__APPLICATION_DIR__") || strings.Contains(source, "$prerelease = $true") != prerelease {
			t.Fatal("repair directory or channel was not bound as data")
		}
	}
}

func TestPublicRepairIsStandardOnly(t *testing.T) {
	for _, edition := range []string{"", "Business", "Unknown", "Standard"} {
		if canUsePublicRepair(edition) != (edition == "Standard") {
			t.Errorf("unexpected repair decision for %q", edition)
		}
	}
}

func TestSystemCommandUsesOnlyWindowsPaths(t *testing.T) {
	t.Setenv("DOTNET_STARTUP_HOOKS", "fixture")
	system := `C:\Windows\System32`
	cmd := windowsSystemCommand(context.Background(), system, filepath.Join("WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile")
	if cmd.Path != filepath.Join(system, "WindowsPowerShell", "v1.0", "powershell.exe") || cmd.Dir != system {
		t.Fatalf("unexpected repair executable %q or working directory %q", cmd.Path, cmd.Dir)
	}
	for _, value := range cmd.Environ() {
		if strings.HasPrefix(value, "DOTNET_STARTUP_HOOKS=") {
			t.Fatal("inherited user-controlled launch environment")
		}
	}
	if !cmd.SysProcAttr.HideWindow {
		t.Fatal("unexpected visible installer")
	}
}
