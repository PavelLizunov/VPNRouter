# Bug — Pavel main machine: VPN "starts but doesn't work" (chronic TUN crash + recovery race)

**Date:** 2026-06-08
**Reporter:** Pavel (main Windows desktop — NOT the dev VM)
**Version:** v2.41.1 stable
**OS:** Windows 11 26200 (Dev channel). **NetAdapter PowerShell module MISSING.**
**Diag:** `Z:\VPNRouter-diagnostics-20260608-102730.zip` (Connected=True at capture)
**Status:** triaged, NOT fixed — needs main-machine test loop (cannot repro on dev VM)

## Symptom
VPN shows "Connected" but traffic doesn't flow for ~25 s after each start; had to
retry to get a stable connection.

## Facts from the logs (not guesses)
- **Chronic, not a one-off:** 8 June app logs show **7 sing-box crashes**
  (`vpnrouter20260608_002.log` alone: crashed=6, tun-fatal=4).
- Crash signature: `FATAL configure tun interface: The device is not ready for use`
  (PID 22320 crashed at 15 s uptime, ~14 s after "Connected" was shown). Also seen
  once: `Cannot create a file when that file already exists` (ERROR_FILE_EXISTS).
- `inbound/tun[tun-in]: open interface take too much time to finish!` repeated — the
  wintun device is slow to become I/O-ready.
- **NetAdapter module missing** (app log: `Remove-NetAdapter cmdlet not available ...
  NetAdapter module missing`). So orphan TUN cleanup can only `netsh disable`, NOT
  delete the device record → next `WintunCreateAdapter` hits "not ready"/"file exists".
- **Recovery race (HealthMonitor vs AutoFailover):** on the crash BOTH fired —
  HealthMonitor "Restarting attempt 1/5" AND F-E AutoFailover "Switched ActiveServer
  Germany→Finland and persisted". AutoFailover's restart threw
  `OperationCanceledException`; HealthMonitor restarted with the on-disk current.json
  (still Germany). End state: **UI/settings = Finland, running sing-box = Germany**
  (current.redacted.json proxy = 104.194.156.93 = Germany). Functional but mismatched.
- End state DID self-heal: by 10:27:26 `outbound/vless[proxy]` connections succeed (~90 ms).

## Root causes (priority order)

### CORRECTION 2026-06-08 (after dev-VM reproduction)
- Pavel: **NetAdapter module IS present + working** (v2.0.0.0, штатный) on the main
  machine. The diagnostic's "0" was just "not loaded into that probe session yet".
  My "module missing" framing was WRONG.
- Reproduced on the dev VM: `Get-NetAdapter -Name X | Remove-NetAdapter` in a spawned
  `powershell.exe -NoProfile -NonInteractive` throws Pavel's exact
  `Remove-NetAdapter ... is not recognized ... CommandNotFoundException`.
- **Real cause: `Remove-NetAdapter` IS NOT A CMDLET.** `Get-Command Remove-NetAdapter
  -All` → count 0. The NetAdapter module exports only Get/Set/Enable/Disable/Rename/
  Restart-NetAdapter — there is NO Remove verb. We have been piping into a phantom
  cmdlet since PinkuDani. Every call threw CommandNotFoundException → fell through to
  `netsh disable` (releases the handle, LEAVES the device record). The lingering
  record → "device is not ready" / "Cannot create a file ... already exists" crash loop.
- This reframes a20a047 (Alena D, 2026-06-07): it silenced a phantom-cmdlet error as
  "module missing" instead of fixing the call. PinkuDani Fix #1's proactive
  `Get-Module NetAdapter -ListAvailable` probe checks module presence — irrelevant,
  the module's there; the cmdlet never existed.
- **Real removal path (both verified present on dev VM, read-only):**
  `Get-PnpDevice -Class Net -FriendlyName 'VPNRouter-TUN'` → InstanceId →
  `pnputil /remove-device "<InstanceId>"`. (`Remove-PnpDevice` also absent here →
  pnputil is the portable deletion tool.) Keep the strict VPNRouter-TUN/sing-box-tun-*
  whitelist.

1. **[P0] Orphan TUN device-record never deleted (phantom `Remove-NetAdapter`).**
   Replace with Get-PnpDevice→pnputil /remove-device. This is the actual fix for the
   crash loop. Existing mitigations (PinkuDani Fix #3 netsh-disable; v2.31.9-r4 settle
   delay) only disabled the adapter, never removed the record.
2. **[P1] AutoFailover ↔ HealthMonitor restart race.** Two recovery mechanisms fire on
   one crash, uncoordinated; the loser persists a server switch the winner ignores →
   UI/runtime server mismatch. Needs serialization + winner must regenerate current.json
   for the active server.
3. **[P1, my v2.41.1 feature] naive+HY2 IPv6 UDP failure.** 7 June "Latvia NAIVE": 17×
   `outbound/naive: open UDP connection to [2001:...]: connect: The requested address is
   not valid in its context`. naive UDP sibling resolves to IPv6 on an IPv6-less host.
   DNS strategy is ipv4_only but naive UDP still dials IPv6.

## Fix strategy (staged; #1 needs main-machine test loop)
- **#1 — pnputil orphan removal fallback (Core/Win).** When `IsNetAdapterModuleAvailable()`
  is false, remove the orphan via `pnputil /remove-device <InstanceId>` (built-in, no
  module). Hard part: map friendly name `VPNRouter-TUN` → device InstanceId without
  Get-NetAdapter. Candidate: registry `HKLM\SYSTEM\CurrentControlSet\Control\Network\
  {4D36E972-...}\<GUID>\Connection\Name` → NetCfgInstanceId → pnputil instance; OR
  `pnputil /enum-devices /class Net` + correlate. MUST keep the existing strict
  whitelist (only VPNRouter-TUN / sing-box-tun-*) so we never nuke a coexisting
  WireGuard/AmneziaWG wintun. Unit-test the parse/whitelist with FakeProcessRunner.
  Verified on dev VM: `pnputil /enum-devices /class Net` works read-only.
  ALSO consider: on "device is not ready" specifically (device exists, just slow),
  a bounded retry-with-settle of the sing-box launch may help more than removal.
- **#2 — serialize recovery.** One restart authority at a time. If AutoFailover switches
  the server, it (or the unified restart) must regenerate current.json so runtime == UI.
  Add a guard so HealthMonitor and AutoFailover can't both drive a restart concurrently.
  Testable on dev VM (logic/coordination + characterization test).
- **#3 — naive UDP force IPv4.** In NaivePairing/config-gen, force the UDP sibling's
  domain_strategy/server resolution to IPv4 (or drop IPv6 candidates when host has no
  IPv6). Unit-test the generated config. Testable on dev VM.

## Test loop
Dev VM has the NetAdapter module (count=1) and won't reproduce #1's "device not ready".
#2 and #3 are unit-testable on the VM. #1 must be verified by Pavel on the main machine:
ship a candidate → run → send fresh diagnostics → confirm orphan removal + fewer crashes.

## Acceptance
- [ ] Main machine: no more "device is not ready" crash loop on connect (or auto-recovers
      in 1 cycle, not 7).
- [ ] After any AutoFailover switch, UI server == running sing-box server.
- [ ] naive+HY2 on IPv6-less host: no IPv6 UDP dial errors.
