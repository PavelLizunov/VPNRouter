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

- [ ] Exact license source and payload-version correspondence verified.
- [ ] Tests cover license file/expression, missing notice, wrong hash/version and
      escaping paths; exercise implementation functions, not duplicate logic.
- [ ] Inspect final package evidence, not only a source notices directory.
- [ ] Non-root package rebuild on approved omarchy-test; existing tests green.
- [ ] Independent review, task commit/push, exact-SHA CI verification.
- [ ] Report distinguishes resolved records from remaining evidence/release gates.

## Execution and gates

Extend packaging/arch only, tests and owning README, plus task evidence/ledger.
Use Python standard library and existing package inspector; no license-service
framework. Bounded source research for native dependencies, no heavy native build.

1. Build: PENDING actual package assembly, no installation.
2. Tests: PENDING existing suite and license evidence negative fixtures.
3. Documentation: approved brief recorded; outcome/evidence pending.
4. Review: PENDING independent source/security/test review.
5. UI/live: N/A; source and isolated packaging only.
6. Integration: PENDING manifest-to-payload checks and exact-SHA CI.

Outcome: pending. Prior binary-distribution block remains until evidence is proven.
