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

To be filled in after the green CI run and the worker dry run.
