# macOS bug audit - 2026-06-04

Scope: macOS runtime path, DNS/leak protection, process routing, update path,
packaging/CI, sudoers setup, and shipped QA signals.

## Findings

### HIGH - Desktop update checksum is currently dropped in the new update path

Files:

- `VPNRouter.Core/Services/UpdateSources/GitHubReleaseSource.cs`
- `VPNRouter.Core/Services/UpdateChecker.cs`

`GitHubReleaseSource.CheckAsync()` fetches the `.sha256` sidecar and stores it
in `UpdateSourceInfo.AssetSha256`. But `UpdateChecker.IDesktopInstaller`
adapts that record back into legacy `UpdateInfo` with `FullChecksumUrl = null`.
The legacy download code verifies SHA only when `checksumUrl` is non-empty.

Impact: desktop in-app update can download, extract, and apply an asset without
hash verification. This is cross-platform, but it is especially relevant for
macOS because Mac update applies a full `.app` bundle via detached `ditto`.

What to check/fix:

- Make `IDesktopInstaller.DownloadAndStageAsync(UpdateSourceInfo, ...)` verify
  `info.AssetSha256` directly after downloading the asset.
- Or preserve the checksum URL in `UpdateSourceInfo` and pass it through the
  legacy path.
- Add a regression test: desktop GitHub update with wrong `AssetSha256` must
  throw before `ApplyAsync` can be called and must delete the staged asset.

### HIGH - macOS CI smoke test looks for sing-box in the wrong bundle path

Files:

- `build-mac.sh`
- `.github/workflows/build-mac.yml`

`build-mac.sh` copies `sing-box` into `VPNRouter.app/Contents/MacOS/sing-box`.
The macOS workflow smoke test looks for
`VPNRouter.app/Contents/Resources/sing-box` and exits 0 with a warning if it is
missing.

Impact: the smoke test can silently skip the exact check it claims to run.
Mac artifacts may ship with a broken/missing bundled `sing-box` and still look
green enough in release QA.

What to check/fix:

- Point the workflow to `Contents/MacOS/sing-box`.
- Treat missing `sing-box` as a hard failure, not `exit 0`.
- Prefer testing the generated VPNRouter config shape, not only a tiny hand-made
  sing-box JSON.

### HIGH - MacDnsHardening reports success even when sudo/networksetup fails

File:

- `VPNRouter.Core/Platform/macOS/MacDnsHardening.cs`

`Run()` logs non-zero command exit only at Debug level and returns stdout. The
caller does not receive success/failure. `Apply()` saves state, calls
`SetDnsServers()`, flushes DNS, and logs "Pinned DNS" even if
`sudo -n networksetup -setdnsservers ...` failed. `Restore()` deletes the saved
state after `SetDnsServers()` even if restore failed.

Impact:

- User/logs can say DNS was pinned while DNS still leaks.
- Restore can lose the original DNS state after a failed restore attempt.
- Missing sudoers, changed service names, or networksetup failures are hard to
  diagnose.

What to check/fix:

- Return `ProcessResult` or a bool from `RunSudo()`/`SetDnsServers()`.
- Log non-zero `networksetup` as Warning.
- Only log "Pinned", flush cache, and delete state after confirmed success.
- Add tests for non-zero apply and non-zero restore.

### HIGH - sudoers one-time flow can become "prompt every connect"

Files:

- `VPNRouter.App/ViewModels/MainWindowViewModel.cs`
- `VPNRouter.App/Assets/InstallGuide.html`

`EnsureMacSudoAccess()` checks whether `/etc/sudoers.d/vpnrouter` contains the
current marker by reading the file. The helper writes the file as
`0440 root:wheel`. A normal user may be able to stat the file but not read it,
so the method falls into `UnauthorizedAccessException` and sets
`needsRewrite = true`.

Impact: on macOS this can show the admin prompt repeatedly before Connect,
despite the UX saying "one-time".

The DMG `InstallGuide.html` is also stale: it grants passwordless sudo for
`sing-box` and `pkill`, but not for `networksetup`, `dscacheutil`, or
`killall -HUP mDNSResponder`, which the runtime now needs for DNS hardening.

What to check/fix:

- Do not rely on reading a root-owned 0440 sudoers file as the user.
- Validate with `sudo -n` command probes or store a user-writable marker only
  after probes succeed.
- Update `InstallGuide.html` to match the runtime sudoers grant exactly.
- Add source-pin tests for the guide command and runtime sudoers template.

### MED - DNS hardening only covers one current primary network service

File:

- `VPNRouter.Core/Platform/macOS/MacDnsHardening.cs`

The implementation maps the current default route device to one network service
and pins only that service. It persists/restores one service in the state file.

Impact: DNS can leak or remain unpinned when:

- user switches Wi-Fi/Ethernet while VPN is running;
- Mac has multiple active services;
- a USB/network/VPN interface changes the default route;
- the saved service is renamed/removed before restore.

What to check/fix:

- Enumerate active services or reapply on network-change events.
- Add diagnostics that show which service was pinned and current `scutil --dns`.
- Add tests for missing/renamed service and changed default route.

### MED - macOS block_on_vpn_fail is still a no-op

Files:

- `VPNRouter.Core/Platform/PlatformServices.cs`
- `VPNRouter.Core/Platform/macOS/NullFirewallManager.cs`

Non-Windows platforms get `NullFirewallManager`. On macOS,
`block_on_vpn_fail` only logs a warning; it does not block traffic after a
sing-box crash.

Impact: profile-level leak protection semantics differ from Windows. If a user
expects crash kill-switch behavior on Mac, traffic can go direct.

What to check/fix:

- Implement a `MacFirewallManager` via a `pfctl` anchor.
- Until then, make the UI and profile import path explicitly label
  `block_on_vpn_fail` as unavailable on macOS.
- Add a Mac shipped-QA item: crash sing-box and verify the expected warning or
  actual block behavior.

### MED - Mac process scanner timeout is ineffective

File:

- `VPNRouter.Core/Platform/macOS/MacProcessScanner.cs`

`BuildProcessTree()` calls `proc.StandardOutput.ReadToEnd()` before
`proc.WaitForExit(5000)`. If `/bin/ps` ever stalls, `ReadToEnd()` blocks first,
so the timeout does not protect Connect/hot-reload.

Impact: rare, but this is on the app-start and hot-reload path. A stuck process
scanner can make the UI look frozen.

What to check/fix:

- Use `IProcessRunner.RunAsync()` with a real timeout and async stream drain.
- Add a fake-runner timeout test.

### LOW/MED - Mac process monitor can briefly double-poll after rapid Stop/Start

File:

- `VPNRouter.Core/Platform/macOS/MacProcessMonitor.cs`

`Stop()` cancels the token and immediately sets `_pollThread = null` without
joining. A quick Start can create a second polling thread while the old one is
still sleeping.

Impact: usually small, but it can duplicate process events and add unnecessary
`Process.GetProcesses()` overhead during rapid connect/disconnect cycles.

What to check/fix:

- Replace `Thread.Sleep()` with token-aware wait.
- Join the polling thread with a short timeout before clearing it.
- Add a rapid Start/Stop/Start stress test.

### SECURITY HARDENING - sudoers helper uses predictable temp paths

File:

- `VPNRouter.App/ViewModels/MainWindowViewModel.cs`

The sudoers setup writes predictable files:

- `/tmp/vpnrouter-sudoers`
- `/tmp/vpnrouter-setup.sh`

The helper is then run with administrator privileges and copies the temp file
into `/etc/sudoers.d/vpnrouter`.

Impact: on a multi-user Mac this is a local race/symlink hardening issue. It is
not the most likely user bug, but it is the kind of thing a VPN/security tool
should avoid.

What to check/fix:

- Use a unique private temp directory with restrictive permissions.
- Write files with non-predictable names.
- Validate ownership/mode before the privileged copy.

## Things that look better than before

- `PsProcessLineParser` now preserves process names with spaces, which protects
  Chrome/Electron helper matching.
- `ConfigGenerator.ExpandMacHelperNames()` expands Chromium/Electron helpers and
  Safari WebKit process names.
- Mac DNS parser logic is unit-tested.
- Relevant targeted tests passed locally: 93 passed, 1 skipped.

## Suggested next checks

1. On a real Mac, install current DMG, follow only `InstallGuide.html`, then
   connect with `DnsLeakLockdown=true`. Verify whether the app prompts again and
   whether `networksetup -getdnsservers Wi-Fi` changes to the TUN target.
2. Break sudoers intentionally and verify the app surfaces DNS hardening failure
   clearly instead of logging "Pinned".
3. Run a Mac update from one release to another and verify:
   - SHA mismatch aborts before apply;
   - `/tmp/vpnrouter-update-*.log` shows `ditto` success;
   - relaunched app version equals target release;
   - bundled `sing-box` exists at `Contents/MacOS/sing-box` and runs.
4. Crash `sing-box` during macOS split-tunnel and confirm the app's behavior is
   honest: either real pf kill-switch or explicit "not protected on macOS".
5. Switch Wi-Fi/Ethernet while VPN is running and inspect `scutil --dns` plus
   leak-test behavior.
