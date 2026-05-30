# Security Policy

## Reporting a vulnerability

Please report security vulnerabilities **privately**, never in a public issue.
Disclosing publicly before a fix exists puts users at risk.

- Preferred: GitHub private vulnerability reporting — open
  [Security Advisories](https://github.com/PavelLizunov/VPNRouter/security/advisories/new)
  for this repository and click "Report a vulnerability".
- Alternative: contact the maintainer (Pavel Lizunov,
  https://github.com/PavelLizunov) via the channels on the GitHub profile.

## What to include

- Affected version (the release tag, or Help -> About in the app).
- Platform (Windows / macOS / Linux / Android) and OS version.
- A clear description and, where possible, reproduction steps.
- Relevant log excerpts — but **redact** subscription tokens, VLESS UUIDs,
  Reality keys/short IDs, and other credentials first (see `PRIVACY.md` for
  where those live).

## Supported versions

VPNRouter uses rolling releases; security fixes target the **latest stable**.
Older versions are not maintained — please update first (the in-app updater
pulls the latest stable from GitHub Releases).

## Scope

VPNRouter runs third-party network engines as separate processes (sing-box,
Zapret/winws, a Telegram proxy). Vulnerabilities in those upstream projects
should be reported to their own maintainers. This policy covers the VPNRouter
application code, its auto-update path, and its packaging.
