# Business diagnostics without a running application

- Owner: VPNRouter Business follow-up session
- Branch: dsh/business-pilot
- Base: 4a0c83df470c6c291bb5a450103dbd8b4d8487eb
- Roadmap: N/A; owner-requested pilot support
- Risk: MEDIUM; diagnostic files may contain credentials and employee data.
- Blast radius: Business support scripts, Business installer, focused Windows tests,
  Business CI artifact and support documentation. VPN routing is outside scope.
- Rollback: revert this follow-up; existing tray export remains available.

## Why

The pilot already exports redacted diagnostics through its tray, but that action
is unavailable when the application cannot start. The owner selected diagnostic
ZIP delivery and subsequently requested support for a custom upload endpoint.

## What

Provide a standalone Windows PowerShell 5.1 collector and double-click launcher.
It must work with the already delivered pilot, collect bounded allowlisted log
tails and crash reports, redact secrets, and create a local ZIP without starting
VPNRouter or changing the connection, DNS, routes, firewall or services. Do not
copy raw configuration, subscriptions, binaries, packet captures or arbitrary
files into the report. Include warnings for missing or unreadable inputs.

Include the helper in future Business installers and deliver the same scripts
separately for the existing pilot. Keep the ordinary installer unchanged.
User-initiated upload may target an owner-provided HTTPS endpoint, with verified
TLS, bounded requests, no redirects and a retained ZIP on failure. A real URL,
request format and server/deployment scope were initially pending. The owner
confirmed they will provide a ready HTTPS endpoint. Support a documented raw
ZIP POST, but do not deploy a receiver or claim production upload verification.

## How

1. Commit this brief on the accepted Business branch and verify exact-head CI.
2. Implement the smallest independent collector using built-in Windows tools.
3. Test synthetic secrets, quoted/multiline values, bounded input, missing data,
   hostile filenames, output failures and absence of connection side effects.
4. Add Business-only installer entry points and employee instructions.
5. Review with independent correctness, security and test-coverage lenses.
6. Run exact-SHA Windows CI and deliver the standalone support files.

## Verification

1. Build: PASS; shared Release solution build in Windows job 125272329306,
   run 41443951195. PR head e4257c30, tested merge 7fdbf70839e4c1d780263e4a875159e7c421cd03.
2. Tests: PASS; all 12 new collector/sender cases execute in Windows PowerShell
   5.1. Business suite 21 passed, 1 intentionally skipped (private-config test).
   Applicable full Windows CI: 3429 passed, 21 skipped; Linux 3260 passed,
   78 skipped (run 41443951208). No SDK installs or local builds.
3. Docs: PASS for source scope; employee/admin instructions and Business guide
   describe local collection, upload format, limitations and old-pilot compatibility.
4. Review: PASS for source inspection with correctness, security and test-coverage
   reviewers; all six confirmed findings recorded and fixed. Windows CI verifies
   their runtime regressions; junction fixture correction also passes.
5. UI: N/A for application UI; this is a separate support launcher. Windows
   collector runtime must be exercised in CI without live VPN or employee logs.
6. Characterization: N/A; no mechanical split or public ViewModel API change.

## Outcome

- Status: PASS for standalone collection and the documented upload client.
  Production endpoint integration remains unverified until its URL is supplied.
- Initial Windows CI: 99f241cf, run 41443085094, shared Release solution build
  passed; Business suite 20 passed, 1 failed, 1 intentionally skipped. The failed
  test stops at fixture junction creation before calling the collector. Replace
  provider-resolved New-Item with literal quoted mklink paths and repeat exact-SHA
  CI; the actual collector/sender scripts are unchanged by this fixture correction.
- Final CI: all 11 applicable checks green at e4257c30. The private installer job
  is intentionally skipped; no new personalized installer was built or released.
- Delivery: `VPNRouterBusiness-Support.zip`, 7412 bytes, SHA256
  `892f2c596afb24997082a1e17c3c8102b740a2d0e14f734001406126fbde5bae`,
  downloaded from the standalone artifact in run 41443951195. Its source receipt
  identifies the exact PR merge 7fdbf70839e4c1d780263e4a875159e7c421cd03, whose
  second parent is e4257c30. All four files match the reviewed branch sources
  after line-ending normalization; no extra files/configuration are present.
  Archive and SHA sidecar are retained in `Downloads/VPNRouterBusiness-Support-2026-11-02/`.
- Changed files: independent PowerShell 5.1 log collector/sender plus CMD launcher,
  Business-only Start-menu entry, standalone CI artifact, employee/admin instructions
  and Windows regression fixtures. No application engine or consumer UI changes.
- Source checks: `git diff --check` and local PyYAML workflow parsing pass. No
  SDK installed and no builds, packaging, full tests or app runs on this workstation.
- Review: business_support_correctness, business_support_security and
  business_support_test_review; findings in OPEN-DEFECTS prefixed BUSINESS-SUPPORT.
  Fixed UTF-8 boundaries, legacy elevation logs, generic URI/SID masking, spaced
  profile names, artifact workflow paths and opened-stream size limits.
- Test limits: fake HTTP transport covers request bytes/headers and non-2xx
  refusal, not real TLS, redirects, timeout or production receiver persistence.
- Baseline: PR #646 head matches the accepted base; all applicable baseline
  checks passed, and the private build job is intentionally skipped on PR CI.
- Endpoint: owner will provide a ready HTTPS endpoint; no server implementation
  or deployment is requested. Actual production upload verification awaits its URL.
- Git: brief 69a87d30; implementation 99f241cf; fixture correction e4257c30,
  all pushed to the accepted Business branch and PR #646. This receipt changes
  documentation only; the delivered artifact remains the verified e4257c30 source.
- Cleanup: no remote worker or live VPN scenario started. Task-only Git bridge
  repository and bundle were removed from the control plane and workstation.
