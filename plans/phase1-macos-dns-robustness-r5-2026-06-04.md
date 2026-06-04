# macOS DNS-hardening robustness — v2.41.0-r5 (2026-06-04)

Brief for r5. Fixes the two HIGH defects the Codex audit
(`plans/macos-bug-audit-2026-06-04.md`) found in the LIVE r3 DNS-hardening code.
Continuation context: `plans/macos-v2.41.0-continuation-2026-06-04.md` §MUST-FIX.

## Why
r3 wired macOS DNS-leak hardening (pin system resolver → TUN gateway). Two
defects make it dishonest/dangerous:
1. **HIGH stuck-DNS** — `MacDnsHardening` logged "Pinned" + saved/cleared state
   regardless of whether `networksetup` actually succeeded. A failed *restore*
   deleted the sentinel → DNS stranded on dead TUN 172.19.0.1, no auto-heal.
2. **HIGH re-prompt** — `EnsureMacSudoAccess` read the 0440 root:wheel sudoers
   file as the user → threw → admin prompt on EVERY connect. InstallGuide.html
   also stale (sing-box+pkill only; runtime now needs networksetup/dscacheutil/
   killall).

## What
- `VPNRouter.Core/Platform/macOS/MacDnsHardening.cs` — `Run`→`RunResult(bool ok,
  stdout)`; `SetDnsServers`→bool; `Apply` logs "Pinned"+flush only on success
  (keeps state on failure — networksetup atomic, restore stays safe no-op);
  `RestoreInternal` deletes sentinel ONLY on confirmed-success restore, keeps it
  + Warning on failure.
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs` `EnsureMacSudoAccess` —
  authority = user-readable marker (`AppPaths.DataDir/macos-sudoers.marker`),
  root-file read is now best-effort + swallowed; marker written after confirmed
  grant. → prompt at most once per marker version.
- `VPNRouter.App/Assets/InstallGuide.html` — manual command grants now match
  runtime (pkill *, networksetup *, dscacheutil *, killall -HUP mDNSResponder).
- Tests: `MacDnsHardeningTests` +3 (non-zero apply keeps state/no flush, non-zero
  restore keeps sentinel, RestoreStranded heals on retry) → 11 total.

## Verification gate
- [x] Core + App build -c Release: 0 errors
- [x] MacDnsHardeningTests 11/11; MVM characterization unchanged (private-method
      edit → no public-surface drift)
- [x] r4 (3cc8f13) CI fully green before commit (rule #11)
- [-] Core/App-only; live DNS effect verified on Mac via SSH after ship
      (scutil 172.19.0.1 during VPN; disconnect restores; failed-restore keeps
      sentinel) — owner: me, not user's regression.

## Risk
LOW. Pure success-checking + a marker-file swap; no new external behavior when
everything succeeds (the happy path is byte-identical). Failure paths are now
honest instead of silently-broken. Default DnsLeakLockdown stays user-gated.

## Outcome
Status: PASS (shipped r5). Live Mac SSH verify of the DNS flow pending (post-ship).
Follow-up: r6 = MacFirewallManager pf kill-switch (separate ship, brick-risk).
