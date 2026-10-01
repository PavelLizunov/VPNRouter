# I3: `VPNRouter.CLI cleanup`, the uninstall-time cleanup of what the app leaves in Windows

## Why

The installer proposal (`plans/phase-installer-proposal-2026-09-30.md`, section 1.2 item 5, owner-approved 2026-09-30)
found that removing VPNRouter leaves things behind outside its install folder: firewall rules (`VPNRouter_Block_*`,
`VPNRouter-DnsLockdown-*`, `0_VPNRouter-DnsLockdown-*`), the saved DNS hardening state, the split-tunnel driver service
the app creates on first use, the WinDivert services that Zapret installs, and the current user's `Run` value. Nothing
removes them today; with DNS lockdown rules left behind the network can stay partly blocked. The proposal made this
command the first step, independent of the installer itself, so the uninstaller (I2, I4) has something to call.

## What

- `VPNRouter.Core/Services/SystemCleanup.cs`: `SystemCleanup.Run(dryRun)` returns a `CleanupReport`. Steps, in this order:
  1. firewall rules whose names start with the three project prefixes (`FirewallManager.ManagedRulePrefixes`, found with
     the existing `FindRulesByPrefixes`, deleted with the new `DeleteRuleByName`);
  2. the saved DNS hardening state (`WindowsDnsHardening.Restore`, reported only when a state file exists);
  3. the `mullvad-split-tunnel` service: stopped and deleted only when its binary path is VPNRouter's own
     (`SplitTunnelPolicy.ClassifyServiceBinPath` = adopted install, `...\vpnrouter\...\driver\mullvad-split-tunnel.sys`),
     Mullvad's or another VPN's copy is reported as skipped and left alone;
  4. the `zapret`, `WinDivert`, `WinDivert14`, `WinDivert15` services: stopped and deleted only when their binary path
     is inside a `\vpnrouter\` folder (Zapret is downloaded into `%ProgramData%\VPNRouter`); an unreadable or foreign
     path is skipped. This is narrower than the proposal ("remove the WinDivert services"): other programs use WinDivert;
  5. the current user's `HKCU\...\Run\VPNRouter` value.
  Each item ends as removed, would remove (dry run), not present, skipped or failed; a failing step does not stop the
  others; `sc delete` answering 1072 (marked for deletion) counts as removed. The firewall goes before DNS so DNS works
  again before its settings are restored.
- Everything the command does to the system goes through `IProcessRunner` (netsh, sc) or the small `ICleanupRegistry`
  (DNS restore, Run value), so the tests use fakes. The command never kills processes: the uninstaller stops the app
  first, and a driver that is still held open shows up as a failed or "marked for deletion" line.
- `VPNRouter.CLI/Commands/CleanupCommand.cs` and its registration: `vpnrouter cleanup [--dry-run]`. It needs elevation
  except with `--dry-run` (which warns when not elevated because netsh may list less), prints the report as plain text
  and returns 1 if any item failed.
- `FirewallManager`: `ManagedRulePrefixes` and `DeleteRuleByName` (internal); `WindowsDnsHardening.HasSavedState`
  (internal). No behaviour change for existing callers.

## Verification

- `SystemCleanupTests` (CI `test` and `characterization-windows`): dry run changes nothing and issues no stop, delete or
  restore call; only prefixed rules are deleted; firewall before DNS; own services stopped then deleted; another VPN's
  split-tunnel driver and a foreign or unreadable WinDivert path are left alone; 1072 counts as removed; a failed delete is
  reported and the other steps still run; a second run does nothing and reports everything as not present; an exception
  in one step is contained; the parsers for `sc qc`/`sc query` output. No test touches the real firewall, services, DNS
  settings or registry.
- Windows worker: `VPNRouter.CLI cleanup --dry-run` from a published CLI in a temporary folder (recorded below).

## Outcome

CI: the first run failed `test` on three cases (`Run_LeavesAnotherVpnsSplitTunnelDriverAlone`,
`Run_CountsAServiceMarkedForDeletionAsRemoved`, `Run_StopsThenDeletesTheServicesThatAreTheirs`): the code labelled the
service lines `service`, the tests and the brief say `driver` and `zapret`. Fixed by passing the area name
(`CleanService(area, ...)`). A later run on that fix failed `characterization-windows` on
`PostShipVerifierContractTests.PostShipVerifier_GreenCiChildContinuesThroughDeployCyclesAndCleanup` ("Mocked post-ship
verifier timed out", 31 s); that test does not touch this change and passed on the rebased head (timing flake, not
investigated). Rebased head `db4ac21f`: `test`, `grep`, `compile`, `test-update`, `go-test-windows` and
`characterization-windows` all green. Later commits only change this file or rebase onto newer `main`.

Windows worker (`tester@100.115.182.0`, the machine that also hosts a real VPNRouter install), 2026-10-01:

- Load preflight (read-only): 13.3 GB of 16.0 GB RAM free, CPU 0 to 2 %, C: 17.8 GB free, no `dotnet`, `VBCSCompiler`,
  `MSBuild`, `java` or `ISCC` process. The real `VPNRouter.App.exe` (PID 9572) was running and stayed untouched.
- Built from the exact SHA `db4ac21f` (shallow `git fetch` of that commit): `dotnet publish VPNRouter.CLI -c Release
  -r win-x64 --self-contained --disable-build-servers` with the private toolchain, 249 files, exit 0, about 25 s.
- `VPNRouter.CLI.exe cleanup --dry-run`, run twice with `ProgramData` pointing at a scratch folder (so the CLI's own log
  folder is not the real data directory), elevated session, exit code 0 both times, identical output:
  ```
  [not present] firewall: VPNRouter rules
  [not present] dns: saved DNS settings
  [not present] driver: mullvad-split-tunnel
  [not present] zapret: zapret
  [not present] zapret: WinDivert
  [not present] zapret: WinDivert14
  [not present] zapret: WinDivert15
  [not present] autostart: current user's Run value
  Dry run: 0 item(s) would be removed, nothing was changed.
  ```
  The CLI wrote no file in the scratch folder. `cleanup --help` lists `--dry-run`.
- Read-only snapshots before and after (12 items: hash of all 425 firewall rules, hash of all 252 services, the five
  driver services, HKCU and HKLM `Run`, Defender exclusions, Uninstall keys, the shared Start Menu shortcut, name, size and
  time of all 570 files under `Program Files\VPNRouter`, data-folder ACL and top-level entries, DNS client servers):
  identical.
- Not verified: that machine was already clean (no VPNRouter firewall rule, no saved DNS state, no driver or WinDivert
  service, no Run value), so the run proves the read path (`netsh` and `sc` output of a real Windows is parsed without
  error, 425 rules listed, none matched) and the "nothing to do" report, not the "would remove" lines or any real removal.
  Those paths are covered by the unit tests with fakes only; a real removal was not run anywhere (the worker hosts a real
  install and the owner's standing rule forbids touching its firewall, DNS and drivers).
