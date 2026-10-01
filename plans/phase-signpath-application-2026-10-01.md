# SignPath Foundation application package (docs only)

## Why

Windows releases are unsigned, which causes antivirus false positives and SmartScreen warnings
(`docs/code-signing-signpath-runbook.md`). The signing workflow is prepared but enrollment is owner-blocked. The owner
said to apply to every free program ("подавай заявки везде где это бесплатно"). The SignPath Foundation terms
(https://signpath.org/terms) ask for a "Code signing policy" on the project home page and a clear account of system
changes, uninstall, privacy and eligibility, and the application needs identity and a captcha that only the owner can give.

## What

- `README.md` and `README.ru.md`: a "Code signing policy" section before Credits with the status (not signed yet, applying),
  the required sentence "Free code signing provided by SignPath.io, certificate by SignPath Foundation", what is signed
  (own binaries only, third-party components excluded), the team roles (the owner is the only maintainer, stated with the
  AI-assisted and bot contributions), the privacy policy (link to `PRIVACY.md` and a one-line summary) and a link to the
  system-change list.
- `docs/code-signing-application.md`: ready-to-paste answers (project, GPL-3.0, public repository, CI build from source,
  release process, what would be signed, roles, privacy), the complete Windows system-change table with how each change is
  removed (firewall rules, DNS values, drivers, service, autostart, Defender exclusions, shortcuts), the uninstall story
  after I2 and I3, and the honest list of risks: the DPI-bypass add-on under "no hacking tools", Defender exclusions,
  missing first-run notice, two `NOTICE.md` entries without a license name, low reputation (4 stars, single maintainer),
  the bundled sing-box fork that `sign-windows.yml` currently expects to be signed (decision for the owner).
- `docs/AGENTS.md` index line. No code, workflow or release-contract change.

## Verification

- Every fact was read from the repository, the GitHub API or the SignPath terms page on 2026-10-01: license and release
  counts, the `protect-main` ruleset (PR required, checks `test` and `grep`, no force-push), firewall rule prefixes
  (`FirewallManager.cs`), DNS registry values (`WindowsDnsHardening.cs`), the signed-file list (`sign-windows.yml`),
  `NOTICE.md`, `PRIVACY.md`.
- CI: `grep` and `test` (the README contract tests still pass because no screenshot or asset text was touched).
- Not verified: the live application form (embedded, not readable without a browser), multi-factor authentication of the
  owner's accounts, whether upstream sing-box publishes signed Windows builds, the licenses of slipstream-client and
  GeoLite2 data, that SignPath accepts the project (the terms ask for verifiable reputation and the project has 4 stars).

## Outcome

Pull request with the package. The owner submits the application at https://signpath.org/apply after merging it and
confirming the points marked "owner" in the document; nothing was submitted by an agent.
