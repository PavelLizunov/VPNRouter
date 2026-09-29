package main

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestCheckIntegrity_EmptyDir(t *testing.T) {
	tmp := t.TempDir()
	rep := CheckIntegrity(tmp)
	if rep.Mismatched {
		t.Errorf("expected NOT mismatched on empty dir, got: %+v", rep)
	}
	if len(rep.Hashes) != 0 {
		t.Errorf("expected zero readable hashes, got: %v", rep.Hashes)
	}
	if len(rep.SkipReasons) != len(expectedDlls) {
		t.Errorf("expected one skip reason per expected DLL (%d), got %d: %v",
			len(expectedDlls), len(rep.SkipReasons), rep.SkipReasons)
	}
}

func TestExtractHash(t *testing.T) {
	cases := []struct {
		in   string
		want string
	}{
		{"1.0.0+f3da9a3", "f3da9a3"},
		{"1.0.0+6cb40e80a5cd717aec077962e4af3fea43640610", "6cb40e80a5cd717aec077962e4af3fea43640610"},
		{"2.31.9-r1", "2.31.9-r1"},
		{"", ""},
		{"1.0.0+", "1.0.0+"},
		{"  1.0.0+abc  ", "abc"},
	}
	for _, c := range cases {
		got := extractHash(c.in)
		if got != c.want {
			t.Errorf("extractHash(%q) = %q, want %q", c.in, got, c.want)
		}
	}
}

func TestRecentRepair_NoMarker(t *testing.T) {
	clearMarker()
	if recentRepair() {
		t.Errorf("expected no recent repair when marker absent")
	}
}

func TestRecentRepair_FreshMarker(t *testing.T) {
	clearMarker()
	defer clearMarker()
	touchMarker()
	if !recentRepair() {
		t.Errorf("expected recent repair right after touchMarker")
	}
}

func TestRecentRepair_OldMarker(t *testing.T) {
	clearMarker()
	defer clearMarker()
	touchMarker()
	old := time.Now().Add(-11 * time.Minute)
	if err := os.Chtimes(markerPath(), old, old); err != nil {
		t.Fatalf("Chtimes: %v", err)
	}
	if recentRepair() {
		t.Errorf("expected NOT recent for marker 11 min old")
	}
}

func TestReadProductVersion_NotFound(t *testing.T) {
	tmp := t.TempDir()
	v, err := readProductVersion(filepath.Join(tmp, "does-not-exist.dll"))
	if err != nil {
		t.Errorf("expected nil error for missing file, got: %v", err)
	}
	if v != "" {
		t.Errorf("expected empty version for missing file, got: %q", v)
	}
}

func TestReadProductVersion_RealAppDll(t *testing.T) {
	candidates := []string{
		os.Getenv("VPNROUTER_TEST_APP_DIR"),
		`C:\Program Files\VPNRouter\app`,
		`C:\Program Files (x86)\VPNRouter\app`,
	}
	var realDir string
	for _, c := range candidates {
		if c == "" {
			continue
		}
		if _, err := os.Stat(filepath.Join(c, "VPNRouter.Core.dll")); err == nil {
			realDir = c
			break
		}
	}
	if realDir == "" {
		t.Skip("no real install found (set VPNROUTER_TEST_APP_DIR to override)")
	}

	rep := CheckIntegrity(realDir)
	t.Logf("integrity report: %+v", rep)
	if len(rep.Hashes) < 2 {
		t.Errorf("expected ≥2 readable DLLs in real install, got %d: %v",
			len(rep.Hashes), rep.Hashes)
	}
}

func TestIsPrerelease(t *testing.T) {
	original := ChannelHint
	defer func() { ChannelHint = original }()

	cases := []struct {
		hint string
		want bool
	}{
		{"stable", false},
		{"prerelease", true},
		{"PRERELEASE", true},
		{" prerelease ", true},
		{"", false},
		{"unknown", false},
	}
	for _, c := range cases {
		ChannelHint = c.hint
		if got := IsPrerelease(); got != c.want {
			t.Errorf("ChannelHint=%q IsPrerelease()=%v, want %v", c.hint, got, c.want)
		}
	}
}
