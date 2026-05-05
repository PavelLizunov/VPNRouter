// repair.go — v2.31.9-r1 trampoline self-repair invocation.
//
// When the integrity check detects mixed-version damage, the stub spawns
// install.ps1 from vpn.ninitux.com (the same one new-user installs use)
// and BLOCKS until repair finishes. After return — pass or fail — the
// caller proceeds to launch VPNRouter.App.exe with whatever the repair
// left on disk.
//
// Why blocking (vs. SelfRepair.cs spawn-and-exit):
//   - The stub is small, native, no UI dependency. We can afford to wait.
//   - Blocking gives the user one continuous "launch" experience: they
//     double-click the shortcut → wait ≤60 s → app appears, fully
//     repaired. No second-launch round trip needed.
//   - Avoids races where install.ps1 finishes between our exit and
//     App.exe starting (file locks, Service state, etc.).
//
// Hidden window (CREATE_NO_WINDOW + WindowStyle Hidden) keeps the user
// experience close to invisible. The 5-minute deadline is comfortable
// for a normal install (~20-40 s) and bounds worst-case stuck behaviour.
package main

import (
	"fmt"
	"os/exec"
	"syscall"
	"time"
)

// RunRepair downloads + executes install.ps1 silently. It blocks until
// install.ps1 exits or the deadline elapses. Returns elapsed wall-clock
// time and any error from exec / timeout.
//
// `prerelease` controls whether install.ps1 receives the -Prerelease
// flag (the script then asks GitHub for the latest prerelease instead
// of the latest stable). Caller normally passes IsPrerelease() so the
// channel inferred at build time wins.
func RunRepair(prerelease bool, deadline time.Duration) (elapsed time.Duration, err error) {
	start := time.Now()
	defer func() { elapsed = time.Since(start) }()

	prereleaseFlag := ""
	if prerelease {
		prereleaseFlag = " -Prerelease"
	}

	// One-line PowerShell bootstrap. We download install.ps1 to TEMP
	// and dot-source it so any -ScriptName invocation picks up the
	// freshly downloaded copy (and so we get a hash-stable filename for
	// postmortem inspection).
	bootstrap := fmt.Sprintf(
		`$ProgressPreference='SilentlyContinue'; `+
			`$tmp = Join-Path $env:TEMP 'vpnr-trampoline-install.ps1'; `+
			`Invoke-WebRequest -Uri 'https://vpn.ninitux.com/install.ps1' -OutFile $tmp -UseBasicParsing; `+
			`& $tmp%s`,
		prereleaseFlag,
	)

	cmd := exec.Command("powershell.exe",
		"-NoProfile",
		"-WindowStyle", "Hidden",
		"-ExecutionPolicy", "Bypass",
		"-Command", bootstrap,
	)
	// CREATE_NO_WINDOW = 0x08000000. Without it powershell.exe inherits
	// the stub's parent console (cmd.exe / explorer.exe equivalent) and
	// can briefly flash a black box. With it we get a truly hidden run.
	cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow:    true,
		CreationFlags: 0x08000000,
	}

	done := make(chan error, 1)
	go func() {
		done <- cmd.Run()
	}()

	select {
	case err = <-done:
		return elapsed, err
	case <-time.After(deadline):
		// Try to terminate the stuck PowerShell so we don't leak it as
		// an orphan when the trampoline exits.
		if cmd.Process != nil {
			_ = cmd.Process.Kill()
		}
		return elapsed, fmt.Errorf("repair timed out after %v", deadline)
	}
}
