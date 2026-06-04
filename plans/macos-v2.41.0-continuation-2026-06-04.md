# v2.41.0 macOS parity — continuation plan (2026-06-04)

Single-source continuation doc for a fresh session. Pairs with
`.claude_handoff.md` (runtime memory), `macos-parity-leak-dns-firewall-update-qa-plan-2026-06-04.md`
(the 7-phase plan), `macos-bug-audit-2026-06-04.md` (Codex audit), and
`macos-olga-debug-2026-06-04.md` (original symptom analysis).

## Current state (shipped, all CI green)

- **Stable:** v2.38.2. **In-flight prerelease:** **v2.41.0-r3** (only one visible).
- **main HEAD = `7031b14`** (AppVersion `2.41.0-r3`). Both remotes (github + Forgejo) in sync.
- Mac host reachable: `ssh -i ~/.ssh/id_ed25519 slovn@192.168.0.246` (macOS 15.5, VPNRouter installed).
- Ground-truth from user's Mac `current.json`: **TUN gateway `172.19.0.1/30`**, auto_route, dns.servers = adguard(proxy)+1.1.1.1(dns-direct)+77.88.8.8(dns-direct).

### What shipped this cycle
- **r1** (`3fe87f0`/`ba78c56`/`736072b`): macOS safe batch — #3 ps space-parse (`PsProcessLineParser`),
  #8 Stop fast-path, #7 tri-state theme (system-follow default), #9 leak-toggle honesty;
  **+ caught a data-loss regression** (SettingsValidator rejected `theme=system` → whole-config reset; fixed).
- **r2** (`32c4d50`): #2b Safari split-tunnel — Safari connects via `com.apple.WebKit.Networking`
  (not "Safari Helper"); added `ConfigGenerator.MacKnownIoProcesses` map.
- **r3** (`7031b14`, dev `f9899f0`): **macOS DNS-leak hardening wired** — `MacDnsHardening` +
  `IUnixDnsHardening` seam + VpnEngine Apply/Restore/RestoreStrandedIfAny + PlatformServices factory
  + #5 sudoers networksetup grant + DnsLeakLockdown toggle un-greyed on macOS.
- Tests added: `PsProcessLineParserTests` (14), `MacHelperNameExpansionTests` (9),
  `MacDnsParsersTests` (20), `MacDnsHardeningTests` (8), `ThemePreferenceTests` (11).

## 🔴 MUST-FIX-FIRST — defects in shipped r3 code (from bug-audit)

These are in code that is LIVE in r3. Fix before relying on the DNS hardening; ship as **r4**
(or a fast r3-followup) — pure Core/App, headless-testable, NOT brick-risk like the firewall.

### 1. HIGH — `MacDnsHardening` reports success even when `networksetup` fails
`VPNRouter.Core/Platform/macOS/MacDnsHardening.cs`. `Run()` logs non-zero only at Debug + returns
stdout; `Apply()` logs "Pinned" + flushes + saves state even if `sudo -n networksetup -setdnsservers`
failed; **`Restore()` deletes the sentinel even if the restore `networksetup` failed** → on a failed
restore the original DNS is lost AND no auto-heal next launch → **DNS can be left stuck at the dead
TUN (172.19.0.1) after disconnect**.
- Fix: `RunSudo`/`SetDnsServers` return bool (check `ProcessResult.ExitCode`). `Apply`: only log
  "Pinned" + flush + (save state) on confirmed success; non-zero → Warning + surface to UI.
  `Restore`: only `TryDeleteState()` after a CONFIRMED-success `SetDnsServers`; on failure KEEP the
  sentinel so RestoreStrandedIfAny retries next launch.
- Tests: non-zero apply (no "Pinned", state still lets restore run), non-zero restore (sentinel kept).

### 2. HIGH — sudoers "one-time" can become "prompt every connect" + stale InstallGuide
`VPNRouter.App/ViewModels/MainWindowViewModel.cs` `EnsureMacSudoAccess()` + `Assets/InstallGuide.html`.
The marker check does `File.ReadAllText("/etc/sudoers.d/vpnrouter")`, but the file is `0440 root:wheel`
— a normal admin user is NOT in `wheel`, so the read throws → `needsRewrite=true` → **admin prompt
every connect**. My r3 marker bump (→ v2.41.0) guarantees at least one re-prompt; the read-fail bug
can make it every time. InstallGuide.html still grants only sing-box + pkill, not networksetup/
dscacheutil/killall.
- Fix: don't read the root-owned file as the user. Detect the grant via `sudo -n <cmd> --help`/
  probe (exit 0 ⇒ granted), OR write a user-readable marker file (`~/Library/.../sudoers-marker`)
  ONLY after a successful grant. Update InstallGuide.html to match the r3 runtime grant exactly.
  Source-pin test for guide-vs-runtime grant parity.

### 3. MED — DNS hardening covers only the one current primary service
`MacDnsHardening` maps default-route device → one service, pins/restores only it. Wi-Fi↔Ethernet
switch, multiple active services, or default-route change while connected ⇒ leak/unpinned.
- Fix: enumerate active services (or reapply on network-change), persist/restore the SET; diagnostics
  log which service(s) pinned + current `scutil --dns`. Tests for missing/renamed service + route change.

## macOS bug-audit (2026-06-04) — remaining findings (prioritized)

| Sev | Finding | Files | Mine? |
|---|---|---|---|
| HIGH | #1 above (DNS false-success) | MacDnsHardening.cs | **r3** |
| HIGH | #2 above (sudoers re-prompt + stale guide) | MainWindowViewModel.cs, InstallGuide.html | partly r3 |
| HIGH | Desktop update **drops SHA256 verify** — `UpdateSourceInfo.AssetSha256` fetched but legacy adapter sets `FullChecksumUrl=null`; download/apply with NO hash check (esp. mac `.app` via `ditto`) | GitHubReleaseSource.cs, UpdateChecker.cs | pre-existing |
| HIGH | macOS CI smoke checks `Contents/Resources/sing-box` but build puts it in `Contents/MacOS/sing-box`, and `exit 0` if missing → silently skips | build-mac.sh, .github/workflows/build-mac.yml | pre-existing |
| MED | #3 above (single-service DNS) | MacDnsHardening.cs | **r3** |
| MED | macOS `block_on_vpn_fail` still no-op (NullFirewallManager) | PlatformServices.cs | pre-existing → r4 |
| MED | MacProcessScanner timeout ineffective (`ReadToEnd()` before `WaitForExit`) | MacProcessScanner.cs | pre-existing |
| LOW/MED | MacProcessMonitor double-poll after rapid Stop/Start (no thread join) | MacProcessMonitor.cs | pre-existing |
| SEC | sudoers helper uses predictable `/tmp/vpnrouter-sudoers` + `-setup.sh` (local race/symlink) | MainWindowViewModel.cs | pre-existing |

Audit confirmed GOOD: PsProcessLineParser, ExpandMacHelperNames, MacDnsParsers tests (93 pass/1 skip).

## 7-phase parity plan — status

| Phase | Status |
|---|---|
| 0 Evidence baseline | 🟡 r1 diagnostics gave ground-truth; tcpdump/scutil before/during/after pending user r3 verify |
| 1 Platform-aware leak honesty | 🟢 UI honesty (#9 + r3 un-grey); formal `PlatformLeakProtectionReport` model NOT built (optional) |
| 2 macOS DNS hardening | 🟢 shipped r3 (Option B: pin service DNS to TUN). **Robustness defects #1/#3 above remain.** Pending tcpdump verify |
| 3 macOS firewall / kill-switch | 🔴 **r4** — `MacFirewallManager` pf anchor `com.ninitux.vpnrouter.killswitch`. BRICK-RISK |
| 4 Update verification | 🔴 shipped-package smoke workflow (+ fix the CI sing-box path bug above) |
| 5 Post-ship macOS QA | 🔴 `post-ship-macos-verify` checklist/skill (mirror Windows) |
| 6 Diagnostics parity | 🟡 export exists; ADD `scutil --dns` / `networksetup -getdnsservers` / `ifconfig` / `netstat -rn` / `pfctl -s info` / `pfctl -a <anchor> -sr`. **Autonomous, do early — makes verify one-click** |
| 7 UX honesty + docs | 🟡 toggle honesty + theme-follow (closed open-Q#5); README/InstallGuide docs pending |

## Next-session work order (recommended)

1. **r4 batch A (robustness, autonomous, headless-testable) — fix shipped-r3 defects:**
   - #1 MacDnsHardening success/restore safety (TOP — prevents stuck-DNS).
   - #2 sudoers probe-based marker + InstallGuide.html update.
   - Optionally #3 multi-service DNS (or defer with a logged limitation).
   Ship r4, user re-verifies on Mac (scutil + tcpdump + disconnect-restore).
2. **Phase 6 diagnostics** (autonomous): add macOS network-state commands to Export Diagnostics.
   Makes every future verify = "Export → send zip". Could ride r4.
3. **r5 — MacFirewallManager pf kill-switch** (Phase 3, BRICK-RISK): default-OFF, pfctl anchor,
   FakeProcessRunner wire tests, then **mandatory kill-9-mid-block live Mac gate** (SSH-verify;
   recovery = `pfctl -F all && pfctl -X`). Needs pfctl sudoers grant (extend EnsureMacSudoAccess).
   Ship SEPARATE from DNS (brat r10/r16 lesson).
4. **CI/update integrity** (pre-existing HIGH): fix build-mac.sh smoke path + hard-fail; wire
   AssetSha256 verification into the desktop update download path + regression test.
5. **Phase 4/5** (update smoke workflow + post-ship-macos-verify skill), **Phase 7** (docs), and the
   MED/LOW audit items (scanner timeout, monitor double-poll, sudoers temp-path hardening).

Sequencing rationale: fix the live DNS robustness defects first (they undermine the r3 fix + risk
stuck-DNS), then diagnostics (cheapens all later debug), then the brick-risk firewall, then CI/QA.

## User verification checklist — r3 (give to Pavel)

Update to r3 → approve the one-time admin prompt (re-grants sudoers incl. networksetup).
1. `scutil --dns | grep nameserver | sort -u` (note ISP resolvers).
2. Network settings → enable "Block DNS outside VPN" → Connect.
3. During VPN: `scutil --dns | grep nameserver | sort -u` → expect **`172.19.0.1`** (TUN gateway).
4. Gold-standard: `sudo tcpdump -i en0 port 53` while browsing → expect **zero** port-53 on en0.
5. Disconnect → `scutil --dns | grep nameserver` → original resolvers restored.
6. Safari (r2): split mode, Safari routed → confirm it tunnels.

**⚠ Recovery if DNS breaks after disconnect** (the audit-#1 restore defect): manually reset with
`sudo networksetup -setdnsservers Wi-Fi empty` (Wi-Fi or your active service). Fixing that defect is
r4 priority #1.

## Key references
- Mac host: `slovn@192.168.0.246` (macOS 15.5), key `~/.ssh/id_ed25519` (via AmneziaWG route).
- TUN gateway: `172.19.0.1` (from `settings.Tun.Ipv4Address` = "172.19.0.1/30", via `MacDnsParsers.DeriveDnsTarget`).
- Update-check: `GitHubReleaseSource.cs:122` filter `!Draft && (IsExperimental || !Prerelease)` —
  prereleases need experimental channel; SemVer compare `UpdateChecker.cs:1324` (r1<r2<stable, correct).
- Ship cadence: rolling -rN autonomous; stable cut user-gated; push BOTH remotes; gate-aware (pre-push
  hook checks HEAD^1 CI; `TOLERATE_FAILURE=test` is the sanctioned escape, never `--no-verify`).
- macOS DnsLeakLockdown gate: `MainWindowViewModel.IsDnsLeakLockdownAvailable => IsWindows || IsMacOS`.
