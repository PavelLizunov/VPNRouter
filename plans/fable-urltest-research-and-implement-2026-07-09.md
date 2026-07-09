# Fable task — VPNRouter: verify the audit corpus, decide the architecture, and start implementing the URL-test / verification trust-boundary

You are Fable, working directly in the VPNRouter repository (`C:\Project\VPNRouter`,
GitHub `PavelLizunov/VPNRouter`, current stable **v2.46.0**). This is a deep,
end-to-end task. Take your time and think hard.

## Absolute working constraint — do this YOURSELF

**Work personally, in your own single context. Do NOT spawn, delegate to, or
switch to subagents/workflows/child agents for any part of this** — not for
reading, not for verifying, not for planning, not for implementing. No fan-out.
You read the sources yourself, you reason yourself, you write the code yourself,
you run the tests yourself. One continuous line of work, start to finish. If you
feel the urge to "launch an agent to check X", instead just check X yourself.

## What this is about

The user and GPT ran several repository-audit passes on VPNRouter and saved the
findings. The single most important theme is the **URL-test / "Auto" server
selection trust boundary** ("urltest"): VPNRouter currently proves a server with
one generic HTTP `urltest` probe (`generate_204`), and treats that as "the
server works". The audit shows this is wrong — especially from Russia, where a
host can answer ping / SSH / TCP while the actual VPN protocol (VLESS/Reality,
XHTTP, AWG, HY2, TUIC) is blocked by DPI/TSPU or an ASN/subnet policy, and where
"connected" does not mean the blocked target (YouTube, Discord, etc.) is
reachable.

Your job: verify all of this yourself against the current source, decide the
architecture for real verification, and **start implementing it**.

## Inputs — in priority order (text is authoritative)

1. **The audit corpus text.** Prefer the text of these documents over any
   assumptions. It exists in two places — use whichever you can read; they are
   the same content:
   - **Local, pre-cleaned:** `plans/audit-import-2026-07-09/` in this repo.
     Start with `01-audit-vector-map-batch1.md` (the master map — it contains the
     priority order, the urltest vector, the RU-ASN/TSPU block vector, the
     blocked-target canary vector, and the external prior-art comparison with
     real upstream issue links).
   - **Google Drive (source of record):** folder
     `https://drive.google.com/drive/u/0/folders/1K0mHmkoFTIoBjAzxE_TYYy7Rqqyubzzi`
     with subfolders **Roadmaps** (audit vector map + verification matrix + P0/P1
     implementation handoffs), **Research** (macOS privilege model: exec summary
     / remediation checklist / full notes), **Architecture Decisions** (AI
     Decisions Log). If you have Drive access, read the docs there for the full,
     freshest text (use the markdown/plain-text export so you get clean text).
2. **The current repository source at HEAD.** The corpus is dated 2026-07-09
   against `v2.46.0-r36`; the repo is now `v2.46.0` stable. **Trust current
   source over the notes** — the audit itself says several old assumptions were
   already fixed. Re-verify every claim you intend to act on by reading the
   actual file.
3. Repo rules: `CLAUDE.md`, `AGENTS.md`, `docs/REVIEW_AGENT_PROMPT.md` (the
   invariants), `plans/OPEN-DEFECTS.md`, `VPNRouter.Core/CLAUDE.md`,
   `VPNRouter.App/CLAUDE.md`.

## Phase 1 — Verify (yourself)

Read the corpus, then confirm each relevant claim against current source. In
particular, personally open and read:
- `VPNRouter.Core/Services/ConfigGenerator.cs` + `VPNRouter.Core/Models/VlessConfig.cs`
  — how the `urltest` outbound is emitted (url / interval / tolerance /
  interrupt_exist_connections), and how AutoSelect builds the same-protocol pool.
- The server-test pipeline: `VPNRouter.Core/Services/FreeConfigs/FreeConfigTester.cs`,
  `FreeConfigDeepVerifier.cs`, `VlessDeepVerifier.cs` (if present), and the
  server-test result model — what phases/states exist today, and where all
  failures collapse into `TlsFailed`/`Timeout`.
- The Subscribe/Servers UI + connection-stats VM:
  `VPNRouter.App/Views/Pages/SubscribePage.axaml`,
  `VPNRouter.App/ViewModels/MainWindowViewModel.ConnStats.cs`,
  `ServerViewModel.cs` (the r5 protocol chips + r36 intent selector already
  exist — build on them, don't duplicate).
- `VPNRouter.Core/Services/ConnectionIntentScorer.cs` + `HealthCheck.cs` (r30-r33
  advice engine) — there is already an advice/intent layer to extend.
Correct any stale claim in writing before you rely on it.

Deliverable of Phase 1: a short, honest "confirmed / corrected / still-hypothesis"
list for the urltest vector and its dependencies (RU-block, canary, DeepVerify
phases), each with the exact `file:line` you verified.

## Phase 2 — Plan (yourself)

Write an implementation plan to `plans/urltest-verification-plan-2026-07-09.md`
(brief template per `.claude/skills/phase-task-launcher` / methodology §2: Why /
What / How / Verification gate / Risk). Scope it to the verification
trust-boundary, sequenced by the corpus priority:

```
P0: RU protocol/subnet block diagnostics (phased server-health model)
P1: URL-test / Auto trust-boundary wording + expose selected member + last-test age
P1: blocked-target canary layer (multi-canary, safe-default via-VPN, redacted)
```

Keep it incremental — each step must build clean and add tests before the next.

## Phase 3 — Architecture decision (yourself)

Record ONE architecture decision (ADR-style) to
`plans/adr-urltest-verification-2026-07-09.md`: how the new verification layer is
shaped. At minimum decide:
- The **phased server-health result model** — replace the collapse-to-`TlsFailed`
  with explicit phases: DNS resolve, TCP connect, TLS/camouflage handshake, proxy
  handshake, proxied HTTP GET, proxied UDP/QUIC/app-profile — and states like
  `ProtocolHandshakeBlockedLikely`, `ProviderSubnetHighRisk`, `OnlyControlWorks`,
  `LikelyCensorshipBypassFailed`. Put the pure logic in Core so it's
  CI-testable with zero network (mirror `SplitTunnelPolicy` / classifier style).
- Where it plugs into the existing DeepVerify + AutoSelect + the r36 HealthCheck
  advice engine (extend, don't fork).
- ASN/provider metadata source + local cache, with the hard rule: **never upload
  or log subscription URLs / secrets** (reuse `DiagnosticsRedactor`).
- What stays Core (pure, tested) vs App/Android (UI + platform probes).

## Phase 4 — Start implementing (yourself)

Begin with the highest-value, lowest-risk slice that the plan puts first —
almost certainly the **pure phased server-health classifier in Core** (the
`ProtocolHandshakeBlockedLikely` / `ProviderSubnetHighRisk` decision logic) with
xUnit tests, since it needs no network and unblocks everything else. Then wire it
into the server-test result model + Auto ranking, then the UI wording. TDD:
write the failing pinning test first, then the minimal implementation.

You do NOT need to finish the whole feature — you need to have really started it:
a clean, tested first slice committed, with the plan + ADR in place and the next
slices queued.

## VPNRouter invariants you must not break (verify against the repo, don't assume)

- **Fail-open** for the true-split driver; **fail-CLOSED** for leak protection —
  keep that asymmetry.
- Never treat a generic URL/ping/HTTP/TCP/SSH probe as app-specific verification
  (that is the whole point of this task).
- `process_name` matching is case-sensitive — no `ToLowerInvariant()`.
- Subscription→VLESS: every `ConfigGenerator.Generate` caller must `Resolve`
  first (silent-leak invariant).
- No secrets / subscription URLs in logs, telemetry, diagnostics, or commits;
  reuse `DiagnosticsRedactor`; redact URL query/path unless a canary needs it.
- Cross-platform: Core stays pure net8.0; Windows-only via
  `[SupportedOSPlatform("windows")]` / `#if PLATFORM_WINDOWS`.
- New non-trivial logic gets tests. Commit messages: `type(scope): subject`
  (<=72 chars) + `Co-Authored-By` trailer; never `--no-verify`. Push both remotes
  (`origin`=GitHub, `forgejo`); there is no `github` remote.
- **Live/UI verification is windows-brat (192.168.0.106) over WinRM only — NEVER
  the dev box** (see `.claude/skills/post-ship-mcp-verify` STOP banner). But most
  of Phase 4's first slice is pure Core + unit tests, which run locally fine.
- Do not cut a stable release. This is feature work on `main`; ship candidates
  (`-rN`) only if asked, and a stable cut needs an explicit user command.

## Reporting

At the end, report in text (no separate agent): (1) Phase-1 confirmed/corrected
list with file:line; (2) the plan + ADR file paths; (3) exactly what you
implemented (files, tests, commit hashes); (4) what is queued next; (5) any live
verification still owed on windows-brat. Keep it factual and grounded in the
code you actually touched.
