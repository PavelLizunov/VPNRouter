---
name: post-ship-mcp-verify
description: MANDATORY after every rolling ship (-rN). Verifies the shipped Windows binary ONLY on the fixed remote test VM windows-brat (100.115.182.0 / WINBRAT) through tools/brat-verify.ps1 over WinRM — remote brat only, fail-closed. SHA256-checks the release ZIP, deploys + launches on brat, walks release-note checklists with semantic UIA and screenshots on brat, scans brat logs, reports PASS/FAIL. Never installs, launches, drives, screenshots, or reads app logs on the local dev box. If VM/WinRM/identity is unavailable — STOP, no fallback.
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
Phases run in order; any phase 1–5 failure → STOP, surface exact stderr,
do not report success.

## 1. CI gate

```powershell
powershell -ExecutionPolicy Bypass -File tools/verify-last-commit-ci.ps1
```

Exit 0 → proceed. Exit 1/2/3 → STOP.

## 2. VM readiness + identity

```powershell
powershell -ExecutionPolicy Bypass -File tools/testvm-control.ps1 -Action ensure-ready
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action identity
```

`ensure-ready` probes the fixed Tailscale WinRM endpoint first and, only if
that is unreachable, starts VM 100 via Proxmox and waits for WinRM on
100.115.182.0:5985. `identity` must print `Verified identity: WINBRAT @
100.115.182.0`. Missing `.testpc-cred-192.168.0.106.xml` → the error prints
the one-time setup command; STOP until it exists. Timeout/mismatch → STOP.

## 3. Artifact + SHA256 (fail-closed)

The exact pair `VPNRouter-v$v-win.zip` + `VPNRouter-v$v-win.zip.sha256`
must exist in the repo root. If either is absent, download exactly those
two assets:

```powershell
gh release download "v$v" --pattern "VPNRouter-v$v-win.zip" --pattern "VPNRouter-v$v-win.zip.sha256"
```

Then verify — mismatch or missing sidecar is a HARD STOP, never deploy an
unverified ZIP:

```powershell
$expected = (Get-Content "VPNRouter-v$v-win.zip.sha256" -Raw).Trim().ToLower()
$actual   = (Get-FileHash -Algorithm SHA256 "VPNRouter-v$v-win.zip").Hash.ToLower()
if ($expected -ne $actual) { throw "SHA256 MISMATCH: expected=$expected actual=$actual — STOP" }
```

## 4. Deploy + launch on brat

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action deploy -Version $v
```

Stops the old app on brat, installs the verified ZIP over
`C:\Program Files\VPNRouter\app`, relaunches the GUI on brat's desktop.

## 5. Baseline screenshot

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action screenshot -LocalOutput "artifacts/brat-verify/$v/baseline.png"
```

Visually confirm: window rendered (not a blank placeholder), version
footer/About matches `$v`, no exception dialog. Never commit screenshots.

## 6. Release-note checklists — semantic UIA + screenshots only

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

Mix multiple checklists when one ship touches multiple scopes. Walk every
item; screenshot each state change into `artifacts/brat-verify/$v/`.

UI interaction is only this command plus `-Action screenshot`:

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action uia -Name "<exact RU name>" -ControlType <Button|CheckBox|ListItem> [-UiaOperation <Inspect|Invoke|Toggle|Expand|SetValue>] [-Value <text>]
```

- Semantic selectors only (Name/AutomationId/ControlType). Use exact Name
  strings from current XAML/`Strings.cs` (RU locale) as listed in the
  checklists. Never invent selectors; never add product XAML from this skill.
- `Inspect` (default) asserts presence and prints Name/AutomationId/
  IsEnabled — assert before mutating. "Pattern unsupported" → the Inspect
  assertion still passed; record the actuation gap, continue with screenshots.
- No stable selector → screenshot, assert visually, record "selector
  hardening = future work".
- UIA/screenshot require a logged-on interactive session on brat; the
  script fails closed otherwise.

**End-to-end rule (AGENTS.md #13):** walk the FULL user scenario to the
element reported, not "tab rendered": (a) invoke the target element,
(b) check ALL interactive elements in its scope, (c) screenshot the bottom
of the viewport, (d) confirm the exact strings a user could be looking for.

## 7. Remote logs

```powershell
powershell -ExecutionPolicy Bypass -File tools/brat-verify.ps1 -Action logs -LogWindowMinutes 120
```

Scans recent timestamped entries in the newest `vpnrouter*.log` under
`C:\ProgramData\VPNRouter\logs` ON BRAT for `[ERR]` / `Exception` /
`FATAL`; exit 1 prints the hits. Historical failures outside the verification
window are ignored. Triage hits only against the checklist's "known benign
noise" section; anything else is a FAIL. Missing log dir/file or no recent
timestamped entries fails closed.

## 8. Report

Compact PASS/FAIL as the LAST message of the turn:

```markdown
## Post-ship verification — v$v — PASS|FAIL

**Target**: WINBRAT @ 100.115.182.0 (identity verified).
**Binary**: VPNRouter-v$v-win.zip — SHA256 verified, deployed + launched.
**Checklists**: zapret 4/4, tgproxy 3/4 (pass/total per checklist).
**Failures/blockers**: none | <item + exact stderr or quote>.
**Screenshots**: artifacts/brat-verify/$v/baseline.png, <others>.
**Log scan**: clean | N hits: <quoted lines>.
**Next**: ship r(N+1) with <fix> | triage first.
```

Core-only ships with no UI surface: label "Core-only / not UI-testable"
explicitly instead of faking a green.

## Standing rules

- **No stable-cut authorization.** PASS is one readiness condition only;
  cutting `vX.Y.Z` stable requires the user's explicit command
  ("cut" / "ok" / "promote") — AGENTS.md rule #6.
- Re-run the whole skill after shipping a fix for a verification failure.
- `tools/brat-verify.ps1` is the ONLY driver this skill uses; the only
  other scripts are `tools/testvm-control.ps1` (phase 2) and
  `tools/verify-last-commit-ci.ps1` (phase 1).
