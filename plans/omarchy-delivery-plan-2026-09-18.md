# Omarchy implementation delivery plan

Status: preparation only; no staging, commit, push, default-branch change or merge.
Two independent Gemini inventory lanes completed; coordinator source-checked
critical conclusions. This is not product acceptance or a full security review.

## Verified inventory

Main delivered HEAD: b8bde92c99448c6321396fcaf14f9a9f6f1eae93, draft PR296.
Task branch: dsh/omarchy-plugin-2026-09-17 in both repositories.

Main implementation allowlist (58 files before additional curated evidence):
- VPNRouter.Core/VPNRouter.Core.csproj
- VPNRouter.Core/Models/AppConfig.cs
- VPNRouter.Core/Services/ConfigGenerator.Dns.cs
- VPNRouter.Core/Services/TunOwnershipLock.cs
- VPNRouter.Core/Services/VpnEngine.cs
- VPNRouter.Core/Services/LinuxTunOwnership.cs
- VPNRouter.Tests/ConfigGeneratorRemoteRuleSetGuardTests.cs
- VPNRouter.Tests/ServiceAppCoexistenceTests.cs
- VPNRouter.Tests/StartupPipelineTests.cs
- VPNRouter.Tests/VpnEngineStartAsyncSeamTests.cs
- VPNRouter.Headless/: 36 currently enumerated source/project/contract files.
- VPNRouter.Headless.Tests/: 7 currently enumerated source/project files.
- VPNRouter.sln
- .github/workflows/test.yml
- plans/omarchy-protocol-v1.md
- plans/phase-omarchy-plugin-2026-09-17.md
- plans/OPEN-DEFECTS.md

Before staging directories, re-enumerate exact paths and reject build products,
new/unreviewed files and symlinks. Do not interpret this allowlist as permission
to publish unreviewed future contents. Exclude unrelated .dsh audits,
.dsh/performance-autoresearch, proxy_sources_2026-09-13.json, vpn_issue_report.md,
ignored diagnostics and all generated payloads.

Plugin: coordinator independently compared every snapshot blob against working
files. Snapshot af58704fda0db088c07a591817c6b7f52bebb27a has 45 files:
43 byte-identical, README.md and README.ru.md differ; 3 extra local plan files.
Source equivalence does not establish secret safety, executable-mode equivalence
or full product acceptance. Recheck modes and sanitation before staging.

Plugin initial implementation allowlist (45 files):
- .gitignore, .github/workflows/test.yml, LICENSE, AGENTS.md, manifest.json
- README.md, README.ru.md
- setup, bin/vpnrouter-headless
- BarWidget.qml, Panel.qml, Service.qml
- lib/I18n.js, lib/Model.js, lib/Protocol.js
- locales/en.json, locales/ru.json
- ui/AppsView.qml, ui/Button.qml, ui/CustomConfigView.qml,
  ui/DiagnosticsView.qml, ui/FreePoolView.qml, ui/Header.qml, ui/HeroCard.qml,
  ui/IconButton.qml, ui/Navigation.qml, ui/ProfilesView.qml, ui/RulesView.qml,
  ui/ServersView.qml, ui/SettingsView.qml, ui/StatusBadge.qml,
  ui/SubscriptionsView.qml, ui/TextField.qml, ui/Toggle.qml
- tests/test-packaging.py, tests/check-published-backend.py,
  tests/test-ui-contract.js, tests/test-ui-i18n.js, tests/test-ui-model.js,
  tests/test-ui-protocol.js, tests/test-ui-service.js, tests/qml-harness.qml,
  tests/qml-load-test.qml, tests/qml-mock-host.qml, tests/qml-test-runner.sh

Hold plugin plans/implementation.md, plans/review-packaging.md and
plans/review-qml.md for historical-claim/sanitation review, not automatic staging.
Main evidence reports are also review-required, not categorically discarded:
retain durable evidence and valid cross-links in a curated delivery report.
Unapproved privilege proposals may remain explicitly labeled proposals; their
existence is not approval and does not itself make documentation forbidden.

## Corrections to worker recommendations

1. Reject worker claim that Core has zero pkexec/privilege code:
   SingBoxManager.Lifecycle.cs:915-929 directly selects capabilities or pkexec.
   No new approved privileged broker exists; these are different claims.
2. Reject plugin four-commit sequence as currently proposed: test-ui-contract.js
   requires all 20 QML files, manifest and related resources. Packaging tests
   also consume the repository fixture. Publishing tests ahead of consumers can
   create known-red intermediate commits. CI expects packaging, runner and Node
   files together. Do not omit CI to disguise incomplete commits.
3. Reject treating scoped Core tests as proof for a newly split six-file Core
   commit: important ownership/DNS/profile regressions live in Headless.Tests,
   which that split omits. Each selected commit needs its own exact-tree checks.
4. Reject '0 network calls' as observed packet evidence for rule-set tests:
   seeded fresh files cover current cache-hit paths, but no network capture or
   namespace denial proved universal network isolation.
5. Correct inventory lane's stale phase-report line references; unresolved CI
   route text actually occurs at phase lines253-259 before correction.

## Coherent proposed delivery blocks

A. Optional small standalone main block:
   ConfigGeneratorRemoteRuleSetGuardTests.cs plus its concise evidence/outcome
   update. It depends on existing AppPaths and cache APIs, not new Linux flock.
   Validate exact isolated tree before commit; known green full candidate
   6c7fc82c passed6, red9957faca failed before generation. Do not assume candidate
   evidence alone proves an extracted partial commit.

B. Integrated main backend block:
   Remaining Core changes, 3 lifecycle fixture changes, all 36 Headless and
   7 Headless.Tests files, solution, CI, protocol and current outcome/ledger.
   Keep together so new Core behavior ships with its regression runner and CI.
   This is larger but coherent; a finer split requires separating test dependencies
   and more independent validation, not arbitrary directory commits.

C. Plugin implementation block:
   After an approved bootstrap base exists, commit all 45 interdependent files
   together, minus any files already supplied identically by the baseline.
   Curated historical evidence can follow separately without altering product.

For EACH block: inspect staged exact allowlist/diff/modes/secrets, verify its
exact tree, conventional commit, immediate task-branch push, existing/new draft
PR, canonical PowerShell verifier on WINBRAT for that SHA, wait for actual CI.
Do not accumulate known-red commits or treat commit-tree snapshots as delivery.
The full pre-handoff regression/security gates still remain; draft delivery does
not imply installability, protection parity or release authority.

## Evidence to reuse only while relevant bytes remain unchanged

- Backend5edc14bf: 36 groups twice (41 lifecycle checks), expanded76 Core tests.
- Core6c7fc82c: isolated rule-set class6 pass; later comment-only change.
- Plugin0461e82d: five Node suites; Qt positive12/0 and failing-assertion11/1.
- Pluginaf58704f: exact-count Qt12/0; df98d5d1 missing-test11/0 rejected.
- Packagingf3b1e1a: 38 tests. Consumer1712eb08: unchanged9 / Linux canary10.
- Specb8bde92c: canonical verifier4 green/0 tolerated/0 pending/0 red, exit0.

Latest full Core gate is incomplete. Runtime path/pinned binary, privileged
protection and actual host UI/dataplane acceptance remain unresolved. No live
installation, root changes, shell restarts, merges, tags or releases authorized.

## Empty remote / PR base decision

Observed via GitHub API: public plugin remote, size0, default_branch metadata main,
branches[]. A draft PR targeting main cannot be created until that ref exists.
No direct main push or default-branch change is authorized by the current plan.
Do not push an unrelated orphan implementation then manufacture ancestry later.

Recommended explicit owner decision: initialize main with a minimal baseline
(.gitignore and GPL LICENSE only; no plugin manifest claiming nonexistent entry
points, no partial implementation), then base the task branch on that same
commit while preserving all working files. Owner may perform initialization or
explicitly authorize the exception. This is a proposal, NOT an action taken.
Alternative non-main review base needs an explicit PR-target policy decision;
it should not be described as equivalent to the canonical main-target workflow.
Main VPNRouter delivery can progress independently of this plugin bootstrap.

Next bounded work: validate and deliver block A through exact-SHA CI; separately
obtain the plugin bootstrap decision before any remote initialization.
