---
name: post-ship-mcp-verify
description: After every successful ship of a -rN candidate, run this skill to actually verify the new binary works on the user's machine. Downloads the freshly-shipped ZIP from GitHub Release, installs over the existing dev-VM install at `C:/Program Files/VPNRouter/app/`, launches it, walks through the changed pages via mcp__vpnrouter-test__* tools, screenshots each verification point, tails `vpnrouter*.log` for errors, and produces a PASS/FAIL report. Auto-triggered after `ship-rolling-candidate` completes. DO NOT skip even for "tiny" changes — past sessions shipped 12 candidates without local verification and the user caught it via screenshots. This skill makes "I forgot to test" impossible.
when: After `ship-rolling-candidate` finishes Step 8 (Mac+Linux CI green, 12-14 assets present), but BEFORE reporting completion to the user. Also run manually when user says "verify last ship" or "check if r19 actually works".
---

# Post-Ship MCP Verification — actually launch + click + check

VPNRouter ships rolling candidates rapidly. Tests pass in CI. CI fan-out
goes green. The release page shows 14 assets. Then we declare victory
without ever launching the binary.

**That's the failure mode.** Sessions 2026-05-25 r7..r18 shipped 12
candidates without local MCP verification. User caught a real XAML
binding bug only because he checked the commits page himself. The skill
exists to make this impossible to skip.

## When this skill fires

- **Auto-trigger**: at the end of `ship-rolling-candidate` Step 8 (after
  CI fan-out complete + asset count verified). Chained via the skill's
  description so the agent invokes it automatically as the natural next
  step.
- **Manual trigger**: user says "verify last ship", "check if rN works",
  or any phrasing that implies post-ship live test.
- **Re-trigger after bugfix**: shipped a fix for a verification failure?
  Run again after the new ship lands.

## The 6 phases

Each phase has a script for the deterministic parts and a checklist for
the interactive ones. Don't skip phases.

### Phase 1 — CI gate

```bash
powershell -ExecutionPolicy Bypass -File tools/verify-last-commit-ci.ps1
```

Exit 0 → proceed. Exit 1/2/3 → STOP, surface the failure to the user
before attempting any local verification. There's no point testing a
binary if the source it was built from has a red commit.

### Phase 2 — Download + install + launch

```bash
powershell -ExecutionPolicy Bypass -File .agents/skills/post-ship-mcp-verify/scripts/post-ship-install-launch.ps1 -Version "2.X.Y-rN"
```

The script does ALL the non-interactive setup:

1. Read `VPNRouter.Core/AppVersion.cs` to confirm the version expected.
2. Stop any running `VPNRouter.*` process (kill before file write).
3. `gh release download vX.Y.Z-rN --pattern "VPNRouter-vX.Y.Z-rN-win.zip"` to `.r-publish/`.
4. Verify the SHA256 against the `.sha256` sidecar.
5. Extract over `C:/Program Files/VPNRouter/app/` (requires the dev-VM to
   already have the install dir from a prior install).
6. `Start-Process 'C:/Program Files/VPNRouter/app/VPNRouter.GUI.exe'`.
7. Wait 6 seconds for the window to appear.
8. Output: success → next phase; failure → surface error.

### Phase 3 — Version + smoke screenshot

Use `mcp__vpnrouter-test__list_windows` to confirm `VPNRouter` window
exists. Take a screenshot of the main window via
`mcp__vpnrouter-test__screenshot`. Visually confirm:

- Window renders (not a blank Avalonia placeholder).
- Title shows correct version (check About page or status footer if the
  title bar doesn't show it).
- No exception dialog visible.

### Phase 4 — Per-feature checklist

Read the release notes for this -rN (in `plans/release-notes-vX.Y.Z-rN.md`)
to identify what changed. Pick the matching checklist from
`references/`:

| Change scope (matched by release notes section header) | Checklist file |
|---|---|
| Zapret (`Zapret*`, `DpiBypass`, probe, cache, hosts) | `references/checklist-zapret.md` |
| TgProxy (`TgProxy*`, `Telegram`, `tg://`) | `references/checklist-tgproxy.md` |
| VPN core (subscriptions, vless, sing-box, TUN) | `references/checklist-vpn-core.md` |
| Network/Settings/Apps page (autostart, lockdown, custom rules) | `references/checklist-network-settings.md` |
| Free Configs (`FreeConfig*`, public pool) | `references/checklist-free-configs.md` |
| Localization-only (Strings.cs members swept) | `references/checklist-localization.md` |

Each checklist enumerates the clicks + assertions to perform via
`mcp__vpnrouter-test__*` tools. Open the chosen file, walk through every
item, screenshot at each step.

**Mix multiple checklists** when one ship touches multiple areas (e.g.
r10 = Zapret cache UI + r17 = ServerTesting labels → run both).

### Phase 5 — Log inspection

```bash
powershell -ExecutionPolicy Bypass -File .agents/skills/post-ship-mcp-verify/scripts/post-ship-collect-logs.ps1
```

Tails the last 200 lines of the most-recent `vpnrouter*.log`, scans for
known-bad patterns (`[ERR]`, `Exception`, `FATAL`, `crashed`, `Failed
to`), surfaces matches with surrounding context. Empty result = good.

### Phase 6 — Report to user

Generate a compact report:

```markdown
## Post-ship verification — v2.37.0-rN

**Binary**: launched successfully (PID NNNN, version 2.37.0-rN).
**Checklists run**: zapret, tgproxy
**Pass**: 7/8
**Fail**: 1 — air-pill missing TgProxyStats text (screenshot @ tmp-rN-airpill.png)
**Log scan**: clean (last 200 lines, no [ERR]/Exception/FATAL).

**Recommended next**: ship r(N+1) with TgProxyStats binding fix, OR
defer if user wants to triage manually first.
```

Surface this to the user as the LAST message in the agent's turn. Don't
proceed to the next code change until user has at least seen the report.

## Failure modes

When this skill detects a problem:

- **Phase 2 script fails** (download / install / launch broken) — STOP.
  Don't pretend the verification succeeded. Surface the script's
  stderr + suggest manual install via `C:/Program Files/VPNRouter/app/`
  inspection.
- **Phase 4 checklist item fails** — screenshot the broken state,
  attach to report. DON'T silently retry — broken UI doesn't fix itself
  with another click.
- **Phase 5 log scan returns matches** — quote the offending lines in
  the report. Some warnings are expected (Bug-r9-G AV-toast, etc.); use
  the checklist's "expected log noise" section to triage.

## What this skill is NOT

- **Not a unit test runner** — use `dotnet test` in the pre-flight gate
  for that. This skill verifies the BINARY's UI behavior, not the code
  units.
- **Not a stable-cut gate** — stable cut requires explicit user
  confirmation per `AGENTS.md` rule #6. This skill's GREEN report is
  just an *enabler* for asking "shall I cut stable?", not the cut
  itself.
- **Not silent** — every run must produce a visible report. Past
  sessions had MCP verify steps that left no record; that's how we ended
  up shipping 12 unverified candidates.

## Anthropic best-practice alignment

Per [Anthropic Skill best practices](https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices)
and the [Skill Creator skill](https://github.com/anthropics/skills/blob/main/skills/skill-creator/SKILL.md):

- **Progressive disclosure**: SKILL.md (this file) under 500 lines holds
  the workflow; scripts in `scripts/`; per-feature detail in
  `references/`. Codex reads only the relevant checklist for the
  current change, not all 6 at once.
- **Pushy description**: the YAML frontmatter explicitly says "DO NOT
  skip even for 'tiny' changes" because past sessions did exactly that.
- **Triggering**: the description mentions "after ship-rolling-candidate
  completes" + manual phrasing variants so the auto-load matches.
- **Bundled scripts** for the deterministic parts (download, install,
  launch, log scan) so the agent doesn't reinvent them per run.

## Cross-references

- `tools/verify-last-commit-ci.ps1` — Phase 1 dependency (CI gate)
- `.githooks/pre-push` — sister enforcement (blocks pushing red)
- `.agents/skills/ship-rolling-candidate/SKILL.md` — upstream skill that
  chains into this one
- `AGENTS.md` rule #1a — "MCP test mandatory after every ship" (the
  policy this skill enforces)
- `AGENTS.md` rule #11 — CI-gate after every push (Phase 1 underpinning)
