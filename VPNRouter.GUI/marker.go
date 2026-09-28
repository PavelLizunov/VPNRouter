package main

import (
	"os"
	"path/filepath"
	"strconv"
	"time"
)

const (
	markerFileName = "vpnrouter-trampoline-repair-marker"

	RepairCooldown = 10 * time.Minute
)

func markerPath() string {
	return filepath.Join(os.TempDir(), markerFileName)
}

func recentRepair() bool {
	info, err := os.Stat(markerPath())
	if err != nil {
		return false
	}
	return time.Since(info.ModTime()) < RepairCooldown
}

func touchMarker() {
	stamp := strconv.FormatInt(time.Now().Unix(), 10)
	_ = os.WriteFile(markerPath(), []byte(stamp+"\n"), 0644)
}

func clearMarker() {
	_ = os.Remove(markerPath())
}
