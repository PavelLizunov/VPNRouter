// marker.go — v2.31.9-r1 trampoline repair-loop guard.
//
// The trampoline writes a marker file before invoking install.ps1. If a
// subsequent launch finds the marker is younger than RepairCooldown, the
// stub SKIPS repair and falls through to launching App.exe with whatever
// is on disk. This prevents an infinite loop where:
//
//   trampoline → detect mismatch → repair fails silently → relaunch
//      → trampoline → detect mismatch (still!) → repair → ...
//
// Same logic + same 10-min cooldown as App-side SelfRepair.Plan().
package main

import (
	"os"
	"path/filepath"
	"strconv"
	"time"
)

const (
	// markerFileName lives in TempDir so non-admin users can read+write
	// it. The data inside is a Unix timestamp; we ignore the body and
	// rely on the file's mtime, but write a timestamp anyway for
	// postmortem grep-ability.
	markerFileName = "vpnrouter-trampoline-repair-marker"

	// RepairCooldown is how long a previous repair attempt suppresses
	// new repairs. Matches App-side SelfRepair.Plan loop guard.
	RepairCooldown = 10 * time.Minute
)

// markerPath returns the absolute path of the cooldown marker file.
func markerPath() string {
	return filepath.Join(os.TempDir(), markerFileName)
}

// recentRepair returns true if a repair was attempted within the last
// RepairCooldown. Returns false on any I/O failure (defensive — a flaky
// disk should not block forever; we'd rather attempt repair again than
// be stuck in a "we already tried" state).
func recentRepair() bool {
	info, err := os.Stat(markerPath())
	if err != nil {
		return false
	}
	return time.Since(info.ModTime()) < RepairCooldown
}

// touchMarker writes (or rewrites) the cooldown marker with the current
// timestamp. Best-effort: silent failure leaves the loop guard weaker
// but doesn't crash the trampoline.
func touchMarker() {
	stamp := strconv.FormatInt(time.Now().Unix(), 10)
	_ = os.WriteFile(markerPath(), []byte(stamp+"\n"), 0644)
}

// clearMarker removes the marker file (used by tests; callers in
// production normally let the timestamp age out).
func clearMarker() {
	_ = os.Remove(markerPath())
}
