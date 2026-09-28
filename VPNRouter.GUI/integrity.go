package main

import (
	"fmt"
	"path/filepath"
	"strings"
	"syscall"
	"unsafe"
)

var expectedDlls = []string{
	"VPNRouter.App.dll",
	"VPNRouter.Core.dll",
	"VPNRouter.Service.dll",
}

type IntegrityReport struct {
	Hashes map[string]string
	RawVersions map[string]string
	Mismatched bool
	SkipReasons []string
}

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

func extractHash(productVersion string) string {
	idx := strings.IndexByte(productVersion, '+')
	if idx < 0 || idx+1 >= len(productVersion) {
		return strings.TrimSpace(productVersion)
	}
	return strings.TrimSpace(productVersion[idx+1:])
}

var (
	modVersion                  = syscall.NewLazyDLL("version.dll")
	procGetFileVersionInfoSizeW = modVersion.NewProc("GetFileVersionInfoSizeW")
	procGetFileVersionInfoW     = modVersion.NewProc("GetFileVersionInfoW")
	procVerQueryValueW          = modVersion.NewProc("VerQueryValueW")
)

func readProductVersion(path string) (string, error) {
	pathPtr, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return "", err
	}

	var dummy uint32
	sizeRet, _, _ := procGetFileVersionInfoSizeW.Call(
		uintptr(unsafe.Pointer(pathPtr)),
		uintptr(unsafe.Pointer(&dummy)),
	)
	size := uint32(sizeRet)
	if size == 0 {
		return "", nil
	}

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

	utf16Slice := unsafe.Slice((*uint16)(valPtr), int(valLen))
	return strings.TrimRight(syscall.UTF16ToString(utf16Slice), "\x00 \t\r\n"),
		nil
}

var ChannelHint = "stable"

func IsPrerelease() bool {
	return strings.EqualFold(strings.TrimSpace(ChannelHint), "prerelease")
}
