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
request format and server/deployment scope are pending the owner's answer;
do not invent a receiver or claim production upload verification.

## How

1. Commit this brief on the accepted Business branch and verify exact-head CI.
2. Implement the smallest independent collector using built-in Windows tools.
3. Test synthetic secrets, quoted/multiline values, bounded input, missing data,
   hostile filenames, output failures and absence of connection side effects.
4. Add Business-only installer entry points and employee instructions.
5. Review with independent correctness, security and test-coverage lenses.
6. Run exact-SHA Windows CI and deliver the standalone support files.

## Verification

1. Build: BLOCKED pending implementation; existing shared Release CI applies.
2. Tests: BLOCKED pending implementation; Windows PowerShell 5.1 execution on
   synthetic fixtures plus applicable full CI. No SDK installs or local builds.
3. Docs: BLOCKED pending implementation and observed outcome.
4. Review: BLOCKED pending a complete diff and independent bug-hunt reviewers.
5. UI: N/A for application UI; this is a separate support launcher. Windows
   collector runtime must be exercised in CI without live VPN or employee logs.
6. Characterization: N/A; no mechanical split or public ViewModel API change.

## Outcome

- Status: PARTIAL; scope recorded before implementation.
- Baseline: PR #646 head matches the accepted base; all applicable baseline
  checks passed, and the private build job is intentionally skipped on PR CI.
- Endpoint: awaiting owner-provided receiver details or a receiver build request.
- Git: brief-only commit pending; no product changes yet.
- Cleanup: no remote worker or live VPN scenario started.
