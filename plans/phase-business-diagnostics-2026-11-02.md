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

1. Build: BLOCKED pending exact implementation SHA in shared Release CI.
2. Tests: BLOCKED pending exact implementation SHA; Windows PowerShell 5.1 execution on
   synthetic fixtures plus applicable full CI. No SDK installs or local builds.
3. Docs: PASS for source scope; employee/admin instructions and Business guide
   describe local collection, upload format, limitations and old-pilot compatibility.
4. Review: PASS for source inspection with correctness, security and test-coverage
   reviewers; all six confirmed findings recorded and fixed. Windows runtime pending.
5. UI: N/A for application UI; this is a separate support launcher. Windows
   collector runtime must be exercised in CI without live VPN or employee logs.
6. Characterization: N/A; no mechanical split or public ViewModel API change.

## Outcome

- Status: PARTIAL; implementation and source review complete, Windows CI pending.
- Initial Windows CI: 99f241cf, run 41443085094, shared Release solution build
  passed; Business suite 20 passed, 1 failed, 1 intentionally skipped. The failed
  test stops at fixture junction creation before calling the collector. Replace
  provider-resolved New-Item with literal quoted mklink paths and repeat exact-SHA
  CI; the actual collector/sender scripts are unchanged by this fixture correction.
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
- Git: brief committed as 69a87d30; all 11 baseline checks passed before edits.
- Cleanup: no remote worker or live VPN scenario started.
