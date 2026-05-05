// integrity.go — v2.31.9-r1 trampoline integrity check.
//
// Reads the auto-generated AssemblyInformationalVersionAttribute from each
// shipped .NET DLL via the Win32 version-info APIs (version.dll). All
// DLLs from the same build carry the same `+<commit-hash>` suffix in
// ProductVersion (.NET embeds it automatically). If any two DLLs disagree,
// the install is mixed-version → trigger repair.
//
// Why this lives in the Go stub instead of App.exe:
//   - Stub runs BEFORE App.exe, so its DLLs aren't locked. PowerShell-
//     side FileVersionInfo on locked DLLs returned empty ProductVersion
//     in user environments — see InstallHealthCheck.cs r6 commentary.
//   - Native binary, no .NET runtime dependency. Bug in App.exe / Core.dll
//     can't block the trampoline from running.
//   - <250 LOC total, pure stdlib syscall — minimal surface to break.
//
// Detection policy (false-positive-averse):
//   - MISSING DLLs are NOT damage — they could indicate a different
//     install layout, partial extraction we don't own, etc. Skip.
//   - UNREADABLE version info is NOT damage — fall through.
//   - Only a clear MISMATCH between 2+ readable hashes triggers repair.
package main

import (
	"fmt"
	"path/filepath"
	"strings"
	"syscall"
	"unsafe"
)

// expectedDlls lists the .NET assemblies whose ProductVersion must agree
// across a healthy install. Add a DLL here if a future release packages
// a new project that should ship in lockstep.
var expectedDlls = []string{
	"VPNRouter.App.dll",
	"VPNRouter.Core.dll",
	"VPNRouter.Service.dll",
}

// IntegrityReport is the trampoline's view of one install directory.
type IntegrityReport struct {
	// Hashes maps each readable DLL name → commit hash (the part after
	// '+' in ProductVersion). Skipped DLLs are simply absent.
	Hashes map[string]string
	// RawVersions keeps the full ProductVersion strings for postmortem
	// logging. Same key set as Hashes.
	RawVersions map[string]string
	// Mismatched is true if 2+ readable DLLs reported different commit
	// hashes (i.e. NeedsRepair).
	Mismatched bool
	// SkipReasons records why each expected DLL didn't make it into
	// Hashes (missing file, unreadable version-info, etc.) — useful
	// for postmortem but never affects the repair decision on its own.
	SkipReasons []string
}

// CheckIntegrity inspects the install at `dir` and returns a report.
// Returns a non-nil report on every call; failures degrade silently to
// `Mismatched=false` (we never trigger repair on inability to read).
func CheckIntegrity(dir string) IntegrityReport {
	rep := IntegrityReport{
		Hashes:      make(map[string]string),
		RawVersions: make(map[string]string),
	}
	for _, name := range expectedDlls {
		path := filepath.Join(dir, name)
		v, err := readProductVersion(path)
		if err != nil {
			rep.SkipReasons = append(rep.SkipReasons,
				fmt.Sprintf("%s: %v", name, err))
			continue
		}
		if v == "" {
			rep.SkipReasons = append(rep.SkipReasons,
				fmt.Sprintf("%s: no version info", name))
			continue
		}
		rep.RawVersions[name] = v
		rep.Hashes[name] = extractHash(v)
	}
	seen := map[string]bool{}
	for _, h := range rep.Hashes {
		if h == "" {
			continue
		}
		seen[h] = true
	}
	rep.Mismatched = len(seen) > 1
	return rep
}

// extractHash returns the substring after '+' in a ProductVersion. .NET
// auto-embeds this as the source-control commit hash via
// AssemblyInformationalVersionAttribute. Falls back to the full version
// when no '+' separator is present (some builds without source-link).
func extractHash(productVersion string) string {
	idx := strings.IndexByte(productVersion, '+')
	if idx < 0 || idx+1 >= len(productVersion) {
		return strings.TrimSpace(productVersion)
	}
	return strings.TrimSpace(productVersion[idx+1:])
}

// ─────────────────────────────────────────────────────────────────────
// Win32 version.dll wiring
// ─────────────────────────────────────────────────────────────────────
//
// We deliberately use the Win32 version-info APIs instead of parsing
// the PE / CLI metadata ourselves. Reasons:
//   1. version.dll has been on every Windows since XP — zero portability
//      risk.
//   2. Same code path .NET's FileVersionInfo / Get-Item .VersionInfo use,
//      so behaviour matches `gh` / `pwsh` postmortem inspections.
//   3. ~80 LOC vs ~300 LOC for hand-rolled CLI metadata reader.

var (
	modVersion                  = syscall.NewLazyDLL("version.dll")
	procGetFileVersionInfoSizeW = modVersion.NewProc("GetFileVersionInfoSizeW")
	procGetFileVersionInfoW     = modVersion.NewProc("GetFileVersionInfoW")
	procVerQueryValueW          = modVersion.NewProc("VerQueryValueW")
)

// readProductVersion extracts ProductVersion from a Windows PE file's
// VS_VERSIONINFO resource. Pure syscall — no cgo, no third-party deps.
//
// Returns ("", nil) when the file simply lacks version-info (e.g. native
// binaries without a resource section). Errors are reserved for genuine
// failures we want logged.
func readProductVersion(path string) (string, error) {
	pathPtr, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return "", err
	}

	// Step 1: get size of version-info block.
	var dummy uint32
	sizeRet, _, _ := procGetFileVersionInfoSizeW.Call(
		uintptr(unsafe.Pointer(pathPtr)),
		uintptr(unsafe.Pointer(&dummy)),
	)
	size := uint32(sizeRet)
	if size == 0 {
		// ERROR_RESOURCE_DATA_NOT_FOUND, file unreadable, etc. We treat
		// this as "no opinion" — the comparison loop will skip this DLL.
		return "", nil
	}

	// Step 2: read version-info into a buffer.
	buf := make([]byte, size)
	rc, _, _ := procGetFileVersionInfoW.Call(
		uintptr(unsafe.Pointer(pathPtr)),
		0,
		uintptr(size),
		uintptr(unsafe.Pointer(&buf[0])),
	)
	if rc == 0 {
		return "", nil
	}

	// Step 3: enumerate translations to find StringFileInfo block.
	transKey, _ := syscall.UTF16PtrFromString(`\VarFileInfo\Translation`)
	var transPtr unsafe.Pointer
	var transLen uint32
	rc, _, _ = procVerQueryValueW.Call(
		uintptr(unsafe.Pointer(&buf[0])),
		uintptr(unsafe.Pointer(transKey)),
		uintptr(unsafe.Pointer(&transPtr)),
		uintptr(unsafe.Pointer(&transLen)),
	)
	if rc == 0 || transLen < 4 {
		return "", nil
	}

	type translation struct {
		Lang     uint16
		CodePage uint16
	}
	trans := (*translation)(transPtr)

	// Step 4: query ProductVersion for the first translation.
	queryStr := fmt.Sprintf(`\StringFileInfo\%04x%04x\ProductVersion`,
		trans.Lang, trans.CodePage)
	queryPtr, _ := syscall.UTF16PtrFromString(queryStr)
	var valPtr unsafe.Pointer
	var valLen uint32
	rc, _, _ = procVerQueryValueW.Call(
		uintptr(unsafe.Pointer(&buf[0])),
		uintptr(unsafe.Pointer(queryPtr)),
		uintptr(unsafe.Pointer(&valPtr)),
		uintptr(unsafe.Pointer(&valLen)),
	)
	if rc == 0 || valLen == 0 {
		return "", nil
	}

	// valLen is the character count (UTF-16 code units, including the
	// trailing NUL).
	utf16Slice := unsafe.Slice((*uint16)(valPtr), int(valLen))
	return strings.TrimRight(syscall.UTF16ToString(utf16Slice), "\x00 \t\r\n"),
		nil
}

// ChannelHint is built-in at link time via -ldflags="-X main.ChannelHint=...".
// It tells the trampoline whether to pass -Prerelease to install.ps1 when
// it triggers a repair, so a user who's tracking the rolling-rN channel
// stays on it after recovery.
//
// Values:
//   - "stable"     — invoke install.ps1 plain (default if ldflag unset)
//   - "prerelease" — invoke install.ps1 -Prerelease
//
// Override at build time:
//   go build -ldflags="-X main.ChannelHint=prerelease" ...
var ChannelHint = "stable"

// IsPrerelease returns true if this stub binary was built for a -rN
// candidate, i.e. ChannelHint=="prerelease".
func IsPrerelease() bool {
	return strings.EqualFold(strings.TrimSpace(ChannelHint), "prerelease")
}
