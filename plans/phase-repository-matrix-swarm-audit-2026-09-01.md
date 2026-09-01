# Phase — whole-repository matrix swarm audit

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/repository-matrix-audit`
**Accepted base**: `origin/main` at `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
**Roadmap ref**: owner-approved whole-repository cleanup audit
**Effort**: multi-session
**Risk**: HIGH
**Blast radius**: read-only inspection of all tracked zones; this audit branch changes plans/ledgers only; product fixes use separate task branches
**Rollback**: revert audit-document commits or close the audit PR; each later fix has its own rollback

## Why

VPNRouter has 1,443 tracked files across desktop, Android, Core, platform networking, tests, packaging, release tooling, and a large historical plan corpus. A broad audit must avoid both blind spots and mass false positives. The owner approved an explicit matrix, two independent swarm iterations per leaf, source verification by the lead, and implementation only after triage.

## What

- Map every tracked production, tooling, test, documentation, and asset zone to a primary audit leaf.
- Run two independent iterations for every leaf, with at least three independent reviewers per iteration.
- Inspect code and tests, consult primary upstream sources where an external contract matters, and look for correctness, security/privacy, lifetime/concurrency, fail-closed routing, compatibility, test gaps, duplication, dead code, and oversized files.
- Source-verify every candidate finding before accepting it.
- Record confirmed findings before implementation; refute duplicates and unsupported claims explicitly.
- Implement confirmed work only in separate scoped task branches and PRs.

## Repository matrix

| ID | Category | Subcategory | Primary scope | Iteration A | Iteration B | Lead verdict |
|---|---|---|---|---|---|---|
| CR-1 | Core runtime | Config, routing, DNS | `ConfigGenerator`, `CustomConfigInjector`, `LeakProtection`, sing-box config models | 3/3 complete | pending | pending |
| CR-2 | Core runtime | Lifecycle, health, failover | `VpnEngine*`, `SingBoxManager*`, `HealthMonitor`, `AutoFailover*`, startup pipeline | 3/3 complete | pending | pending |
| CR-3 | Core runtime | Subscriptions and protocols | subscription/VLESS resolvers, URI parsers, transport schema and selection | 3/3 complete | pending | pending |
| CR-4 | Core runtime | Free configs and emergency paths | `Services/FreeConfigs/`, emergency channel, related orchestration | 3/3 complete | pending | pending |
| PN-1 | Platform/network | Windows | TUN/split driver, firewall, process monitoring, service/launcher boundaries | 3/3 complete | pending | pending |
| PN-2 | Platform/network | Linux | Linux platform adapters, DNS/firewall/process behavior and Linux packaging scripts | 3/3 complete | pending | PN-2-4 confirmed P0; others pending |
| PN-3 | Platform/network | macOS | macOS adapters, DNS/firewall/process behavior, app packaging/update paths | pending | pending | pending |
| PN-4 | Platform/network | Android | `VPNRouter.Android/`, Android Core adapters, VPN service/runtime/storage | pending | pending | pending |
| CL-1 | Clients | Avalonia ViewModels | `VPNRouter.App/ViewModels/`, commands, state, cancellation, bindings | pending | pending | pending |
| CL-2 | Clients | Avalonia views and accessibility | views, styles, resources, navigation, narrow-layout/accessibility contracts | pending | pending | pending |
| CL-3 | Clients | CLI and Windows service | `VPNRouter.CLI/`, `VPNRouter.Service/`, IPC/coexistence and lifecycle | pending | pending | pending |
| CL-4 | Clients | Localization, settings, migrations | Core/App/Android strings, settings loading/saving/migration and config examples | pending | pending | pending |
| SU-1 | Security/update | Updater, installer, extractor | update sources/checker, helper, install scripts, archive handling | pending | pending | pending |
| SU-2 | Security/update | Diagnostics, secrets, logging | scrubbers, exporters, crash paths, logs and outward-facing errors | pending | pending | pending |
| SU-3 | Security/update | Local API, auth, permissions | Clash API, local listeners, privilege boundaries, file/IPC permissions | pending | pending | pending |
| BR-1 | Build/release | CI workflows and hooks | `.github/`, `.githooks/`, repository policy gates | pending | pending | pending |
| BR-2 | Build/release | Packaging and native dependencies | `packaging/`, build scripts, native payload pins/checksums/licenses | pending | pending | pending |
| BR-3 | Build/release | Release feeds and verifiers | release/update-feed tools, BRAT/post-ship verification and asset contracts | pending | pending | pending |
| QA-1 | Quality/architecture | Tests and characterization | `VPNRouter.Tests/`, test discovery, determinism, boundary coverage | pending | pending | pending |
| QA-2 | Quality/architecture | Large files and coupling | oversized source/XAML/scripts, extraction seams, partials and dependency direction | pending | pending | pending |
| QA-3 | Quality/architecture | Dependencies, dead code, duplication | project graphs, one-use abstractions, wrappers, obsolete flags and repeated logic | pending | pending | pending |
| QA-4 | Quality/architecture | Docs, contracts, plans | active docs, AGENTS contracts, samples/profiles, stale/current-state divergence | pending | pending | pending |

## How

1. Generate a tracked-file inventory, top-level/extension counts, largest-file list, and an unclassified-path report.
2. For each category, run Iteration A with three independent roles per leaf: correctness/data-flow, security/fail-closed/lifetime, and tests/compatibility/upstream.
3. Run Iteration B with fresh workers and adversarial counterexamples, duplication/complexity, boundary/fuzz scenarios, and independent upstream checks.
4. Persist bounded structured results: severity, title, evidence, reproduction/test, upstream URL when applicable, proposed fix, cost, and risk.
5. Lead reopens every cited file/line, checks existing tests and prior defects, reproduces mechanically where possible, and labels each candidate `confirmed`, `refuted`, `duplicate`, or `measurement-gated`.
6. Add source-confirmed findings to the durable audit report and `plans/OPEN-DEFECTS.md` before implementation.
7. Group confirmed fixes by coherent risk surface. Each group starts from current `origin/main`, gets its own brief, regression/characterization evidence, bug-hunt, PR, and exact-head CI.
8. Optionally send up to three source-confirmed high-risk clusters through Ouroboros for independent acceptance-criteria verification; Ouroboros does not own architecture or merge authority.

### Audit output contract

```yaml
finding:
  matrix_id: CR-1
  iteration: A
  lens: correctness
  severity: P0|P1|P2|P3
  title: bounded string
  evidence: [path:line]
  reproduction_or_test: bounded string
  upstream_sources: [primary URL]
  proposed_fix: bounded string
  cost: S|M|L
  risk: low|medium|high
  reviewer_confidence: high|medium|low
lead_verdict:
  status: confirmed|refuted|duplicate|measurement-gated
  evidence: bounded string
  implementation_ref: optional branch/PR
```

### Tests written

- Audit phase: N/A; it changes documentation only and validates matrix completeness mechanically.
- Every accepted bug fix must add a regression that fails on the accepted base.
- Every god-file extraction must preserve a pre-change characterization surface exactly.

### Verification approach

The audit uses tracked-path accounting, two independent swarm iterations, primary-source checks where applicable, and lead source verification. Product changes are not accepted from audit prose alone; their separate PRs must pass focused tests, the full discovered suite, independent review, and exact-head CI.

## Verification gate

- [ ] **Gate 1 — Coverage**: all tracked zones are classified; no production/tooling/test path is silently omitted.
- [ ] **Gate 2 — Two-iteration swarm**: every matrix leaf has at least three independent Iteration A and three independent Iteration B reports.
- [ ] **Gate 3 — Evidence quality**: external claims cite primary current sources; every accepted code claim has exact source evidence and a reproduction/test or bounded rationale.
- [ ] **Gate 4 — Lead verification**: every candidate is source-verified and classified; duplicates and hallucinations are removed.
- [ ] **Gate 5 — Durable findings**: confirmed defects are recorded before implementation with severity, target, and status.
- [ ] **Gate 6 — Safe handoff**: implementation work is split into scoped task branches/PRs with applicable build/test/review/characterization gates; no merge, release, tag, deploy, or install occurs without owner authority.

## Outcome

Pending matrix execution, two complete swarm iterations, source verification, triage, and scoped implementation handoffs.
