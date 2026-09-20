# Phase: Omarchy component license evidence

Owner approved the displayed Micro-Spec on 2026-09-20 14:09 Europe/Moscow.
Accepted base: d5a8ee70e9dffacf277c485bec1b9d24d3fd1ac5, continuation on
existing dsh/omarchy-plugin-2026-09-17 branch and draft PR #296.
Why: staging package currently records incomplete notices; a filename search
cannot establish correspondence between shipped components and upstream terms.
Risk: license evidence, bounded XML/path parsing and supply-chain attribution.
Rollback: revert task-owned source changes; no installed state exists.

## 1. Intent & Invariants

- Extend existing package with license/notice/source evidence for actual shipped
  Serilog, YamlDotNet, .NET apphost, sing-box and embedded dependencies, Cronet.
- Preserve authors' notices; do not substitute generic license templates.
- Keep runtime versions/features and production runtime-policy unchanged.
- No install, privileges, root helper, network changes or binary publication.
- Missing evidence remains explicit. This is not a legal conclusion or release
  authorization; stop before changing a runtime pin or rebuilding Chromium.

## 2. Interface / Data Contract

- Per-component record: version/revision, associated payload paths, authoritative
  source, evidence-text hashes and verification state.
- Derive NuGet components from actual publish/restore metadata; validate .nuspec
  identity/version, bounded XML, safe license file paths and SPDX declarations.
- External texts use reviewed fixed revisions and SHA-256, never arbitrary URLs
  extracted from dependency metadata.
- Trace native binary/source/notice relationship and separately record Cronet
  revision disagreement between selected fork and library donor archive.
- Empty found-file lists cannot imply completeness. Incomplete or unverified
  components remain unresolved and prevent declaring complete distribution data.

## 3. Verification Checklist

- [x] Managed license source/payload-version correspondence verified; native uncertainties explicit.
- [x] Tests cover license file/expression, missing notice, wrong hash/version and
      escaping paths; exercise implementation functions, not duplicate logic.
- [x] Inspect final package evidence, not only a source notices directory.
- [x] Non-root package rebuild on approved omarchy-test; existing tests green.
- [x] Independent review passed; task commit/push and exact-SHA CI receipt follow.
- [x] Report distinguishes resolved records from remaining evidence/release gates.

## Execution and gates

Extend packaging/arch only, tests and owning README, plus task evidence/ledger.
Use Python standard library and existing package inspector; no license-service
framework. Bounded source research for native dependencies, no heavy native build.

1. Build: PASS non-root package assembly and final archive inspection, no installation.
2. Tests: PASS 121 tests locally and on omarchy-test; four isolated red/green regressions.
3. Documentation: README and bounded provenance research updated; see verification report.
4. Review: scoped_verified after independent security/acceptance fixes; native exclusions remain.
5. UI/live: N/A; source and isolated packaging only.
6. Integration: PASS manifest-to-payload checks; exact-SHA CI follows task push.

Outcome: scoped delivery, not complete distribution licensing. Serilog, YamlDotNet
and apphost map to exact notice texts. Native source-tree notices are a partial
superset; Go embedded notices, corresponding sources, Chromium linked credits and
Cronet ABI/source-build provenance remain open. Execution stays unauthorized.
See `omarchy-license-verification-2026-09-20.md` for immutable build evidence.
