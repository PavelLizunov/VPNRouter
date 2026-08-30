---
name: post-ship-mcp-verify
description: MANDATORY after every rolling or stable ship. Verifies the shipped Windows binary ONLY on the fixed remote test VM windows-brat (100.115.182.0 / WINBRAT) through tools/brat-verify.ps1 over WinRM — remote brat only, fail-closed. SHA256-checks both Windows ZIPs, requires the exact 16 release assets, deploys + launches on brat, verifies UI state, proxy/HTTPS/UDP dataplane and sanitized lifecycle logs, and reports PASS/FAIL. Never installs, launches, drives, screenshots, or reads app logs on the local dev box. If VM/WinRM/identity is unavailable — STOP, no fallback.
whenToUse: MANDATORY after every rolling or stable ship. Verifies the shipped Windows binary ONLY on the fixed remote test VM windows-brat (100.115.182.0 / WINBRAT) through tools/brat-verify.ps1 over WinRM.
---

> **STOP — read before any install / launch / UI / screenshot / log action.**
> All post-ship work runs ONLY on the fixed test VM **windows-brat
> (100.115.182.0, MachineName `WINBRAT`)** through `tools/brat-verify.ps1`
> over WinRM. Every action re-verifies the WINBRAT identity and fails
> closed on any mismatch.
> **NEVER** install, launch, stop, drive, screenshot, or read VPNRouter
> logs on the dev box (the machine you run on). Do not touch
> `C:\Program Files\VPNRouter` here. No local UI tooling of any kind.
> **If the VM, WinRM, the credential file, or the identity check is
> unavailable — STOP and ask the user. There is NO local fallback.**

# Post-ship verification — remote brat only

`$v` = shipped version (`VPNRouter.Core/AppVersion.cs`, e.g. `2.48.0-r3`).
If the session runs on `harness-test`, coordinate only: execute the controller's
headless build/test work from the exact release SHA on an authorized worker that
passed the read-only resource/SDK/job preflight. Missing tooling is STOP, not
permission to install or use a local VPN/UI fallback.

Phases run in order; any phase 1–5 failure → STOP, surface exact stderr, clean
the exact task-owned artifacts, and do not report success.

## Mandatory executable gate

Run this first after every rolling ship:

```powershell
powershell -ExecutionPolicy Bypass -File tools/post-ship-verify.ps1 -Version $v
```

This single fail-closed command runs the existing `PageScreenshotTests` and
`VisualDiffTests` against secret-free in-memory settings, requires the clean
checkout and CI commit to equal the published release tag, verifies WINBRAT
identity, SHA-verifies and deploys a freshly downloaded published Windows ZIP,
then performs two complete
clean → Connect → TUN Up/Tunnel → selected-proxy + fixed HTTPS+UDP
probe → 30-second hold → Disconnect → TUN absent cycles. Recent lifecycle
logs from before deployment through final cleanup cross the WinRM boundary only
as sanitized event enums and counts. UDP is green only when fixed Cloudflare
STUN responses for 20/64/512/1200/1392-byte requests are valid and the Clash
connection table attributes every exact socket to the
`proxy-udp` (or canonical `proxy`) outbound chain.

Exit 0 plus `"Status":"PASS"` is required before calling the candidate
verified. Any nonzero exit means FAIL/PENDING; do not soften it in prose. The
manual phases below are diagnostic detail and the feature-specific continuation,
not a substitute for this gate. The executable gate never captures the remote
desktop: the configured subscription is secret-bearing. Visual evidence comes
only from the local headless screenshot suite with isolated in-memory settings;
live behavior is asserted through sanitized semantic UIA results.

## 1. CI gate

```powershell
$releaseCommit = gh api "repos/PavelLizunov/VPNRouter/commits/v$v" --jq '.sha'
if ((git rev-parse HEAD) -ne $releaseCommit) { throw 'Checkout does not match release tag.' }
if (git status --porcelain --untracked-files=all) { throw 'Checkout is not clean.' }
powershell -ExecutionPolicy Bypass -File tools/verify-last-commit-ci.ps1 -Commit $releaseCommit -Repo PavelLizunov/VPNRouter -IgnoreSkipped characterization-windows -RequiredSuccess "publish=1,verify=1,test-update=1,test=1,go-test-windows=1,characterization-windows=1" -RequiredWorkflows "Build macOS DMG,Build Android APK,Build Linux AppImage + .deb,Publish APT Repository,Verify Release Integrity,Auto-Update Integration Test (Windows)" -Strict
```

Exit 0 → proceed. Exit 1/2/3 → STOP.

## 2. VM readiness + identity

Before mutation, load the homelab procedure and run the read-only worker
preflight from `docs/test-workers.md`: confirm no conflicting mutable scenario,
then observe identity, CPU/load, available RAM, free disk, and required runtime
tools. Queue on conflict. Do not resize, restart, provision, clean caches, or
change worker/monitoring/DSH configuration.

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action identity
```

`identity` must print `Verified identity: WINBRAT @ 100.115.182.0`. If the local
credential, WinRM, resource preflight, or identity is unavailable/mismatched,
STOP; never substitute a developer machine.

## 3. Artifact + SHA256 (fail-closed)

Always download the exact pair `VPNRouter-v$v-win.zip` +
`VPNRouter-v$v-win.zip.sha256` from the published release into a fresh ignored
evidence directory with overwrite enabled:

```powershell
gh release download "v$v" --pattern "VPNRouter-v$v-win.zip" --pattern "VPNRouter-v$v-win.zip.sha256" --dir "artifacts/post-ship/$v/release" --clobber
```

Then verify the fresh pair and require the repo-root deploy pair to have the
same hash. Mismatch or missing sidecar is a HARD STOP; never deploy an
unverified or merely pre-existing local ZIP.

```powershell
$freshDir     = "artifacts/post-ship/$v/release"
$freshZip     = Join-Path $freshDir "VPNRouter-v$v-win.zip"
$freshSidecar = "$freshZip.sha256"
$rootZip      = "VPNRouter-v$v-win.zip"
$expected     = ((Get-Content $freshSidecar -Raw).Trim() -split '\s+')[0].ToLower()
$rootExpected = ((Get-Content "$rootZip.sha256" -Raw).Trim() -split '\s+')[0].ToLower()
$freshHash    = (Get-FileHash -Algorithm SHA256 $freshZip).Hash.ToLower()
$rootHash     = (Get-FileHash -Algorithm SHA256 $rootZip).Hash.ToLower()
if ($expected -ne $freshHash -or $rootExpected -ne $rootHash -or $freshHash -ne $rootHash) {
    throw "SHA256 MISMATCH: release=$expected/$freshHash deploy=$rootExpected/$rootHash — STOP"
}
```

## 4. Deploy + launch on brat

Manual continuation now owns a mutable scenario. On every phase 4+ PASS or FAIL
exit, run `tools/brat-stability.ps1 -Mode Cleanup` and remove the exact local
`artifacts/post-ship/$v` evidence directory when it is no longer needed. Never
replace this with broad TEMP/cache cleanup.

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action deploy -Version $v
```

Stops only exact canonical VPNRouter paths on brat, stages and hash-compares a
fresh app directory, atomically replaces the prior directory under
`C:\Program Files\VPNRouter\app`, then relaunches the GUI on brat's desktop.

## 5. Live UIA smoke

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "Connect||Подключить" -ControlType Button -UiaOperation Inspect
```

Confirm the launched window exposes the primary action through semantic UIA.
Rendered page layout and fixed viewport sizes are covered by the mandatory
headless `PageScreenshotTests` + `VisualDiffTests` gate.

## 6. Release-note checklists — semantic UIA + headless screenshots

Read the release notes for `$v` (`gh release view "v$v"`, or local
`release-notes-v$v.md` if present) and select every matching checklist:

| Change scope | Checklist |
|---|---|
| Zapret / DPI bypass / probe / strategy cache / hosts | `references/checklist-zapret.md` |
| TgProxy / Telegram / MTProto / tg:// | `references/checklist-tgproxy.md` |
| VPN core: subscriptions, servers, sing-box, TUN | `references/checklist-vpn-core.md` |
| Network / Settings / Apps / autostart / rules | `references/checklist-network-settings.md` |
| Free Configs / public pool | `references/checklist-free-configs.md` |
| Localization-only sweeps | `references/checklist-localization.md` |

Mix multiple checklists when one ship touches multiple scopes. Walk every item.
Remote desktop capture is disabled because the live configuration is
secret-bearing. Add or run a `PageScreenshotTests` case with isolated in-memory
state for every changed page/viewport, and use this command for live behavior:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<exact RU name>" -ControlType <Button|CheckBox|ListItem> [-UiaOperation <Inspect|Invoke|InvokeThen|CheckUpdate|Toggle|Expand|Select|ScrollIntoView|SetValue>] [-Value <text>]
```

- Semantic selectors only (Name/AutomationId/ControlType). Use exact Name
  strings from current XAML and the Core/App localization sources (RU locale) as listed in the
  checklists. Never invent selectors; never add product XAML from this skill.
- `Inspect` (default) asserts presence and prints Name/AutomationId/
  IsEnabled — assert before mutating. "Pattern unsupported" → the Inspect
  assertion still passed; record the actuation gap and fail any checklist item
  that requires the unsupported mutation.
- `Select` uses the native `SelectionItemPattern` for list navigation;
  `ScrollIntoView` uses `ScrollItemPattern` on an already-materialized semantic
  descendant. Both fail closed when the provider exposes no usable pattern.
- No stable selector → FAIL that checklist item; do not replace live behavior
  proof with a screenshot.
- UIA requires a logged-on interactive session on brat; the
  script fails closed otherwise.

**End-to-end rule (AGENTS.md #13):** walk the FULL user scenario to the
element reported, not "tab rendered": (a) invoke the target element,
(b) check ALL interactive elements in its scope, (c) cover the bottom of the
viewport in the isolated headless screenshot, (d) confirm the exact strings a
user could be looking for through UIA or the headless render.

## 7. Remote logs

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action logs -LogWindowMinutes 120
```

Scans recent timestamped entries in the newest `vpnrouter*.log` under
`C:\ProgramData\VPNRouter\logs` ON BRAT for `[ERR]` / `Exception` /
`FATAL`; only sanitized counts leave WINBRAT. Historical failures outside the
verification window are ignored. Any error count is a FAIL. Missing log
dir/file or no recent timestamped entries fails closed.

## 8. Report

Compact PASS/FAIL as the LAST message of the turn:

```markdown
## Post-ship verification — v$v — PASS|FAIL

**Target**: WINBRAT @ 100.115.182.0 (identity verified).
**Binary**: VPNRouter-v$v-win.zip — SHA256 verified, deployed + launched.
**Checklists**: zapret 4/4, tgproxy 3/4 (pass/total per checklist).
**Executable gate**: PASS (page/size diff + 2 VPN/TUN cycles) | FAIL/PENDING.
**Failures/blockers**: none | <item + exact stderr or quote>.
**Screenshots**: isolated headless pages <files>; remote capture disabled.
**Log scan**: clean | N sanitized error classifications.
**Next**: ship r(N+1) with <fix> | triage first.
```

Core-only ships with no UI surface: label "Core-only / not UI-testable"
explicitly instead of faking a green.

## Standing rules

- **No stable-cut authorization.** PASS is one readiness condition only;
  cutting `vX.Y.Z` stable requires the user's explicit command
  ("cut" / "ok" / "promote") under `docs/agent-contract.md`.
- Re-run the whole skill after shipping a fix for a verification failure.
- `tools/brat-verify.ps1` is the only script allowed to perform remote/UI work.
  `tools/post-ship-verify.ps1` and `tools/brat-stability.ps1` are local
  coordinators that delegate every WINBRAT action to it; the remaining helper
  is `tools/verify-last-commit-ci.ps1`.
