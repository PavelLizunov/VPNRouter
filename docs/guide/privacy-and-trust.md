# Privacy, trust and code signing

Part of the [VPNRouter README](../../README.md).

## Privacy & trust

This is a VPN client — you should verify the code before trusting it.

- **Local diagnostics.** Crash reports are written to the app data directory, with best-effort redaction of recognized secret formats before writing. The crash reporter does not upload them automatically. Review any diagnostics before sharing them.
- **Network access.** Updates, subscriptions, public configuration sources and connectivity checks contact their configured services. The selected proxy server handles routed traffic; choose a provider you trust.
- **Credentials.** Settings and generated sing-box configuration contain connection credentials. Protect the app data directory and do not publish configuration files or raw logs.
- **Artifact verification.** SHA256 sidecars check that downloads match the supplied hashes; they do not independently authenticate the publisher. Building from source is supported, but byte-for-byte reproduction of release binaries is not established.
- **Open license.** GPL-3.0 — any fork that distributes a binary must also publish its source.

Found a security issue? Please report it **privately** — see [`SECURITY.md`](../../SECURITY.md). Don't open a public issue for security problems.

## Code signing

The Windows binaries of the releases are **not code-signed** (no Authenticode signature), so Windows SmartScreen or an antivirus may warn about them. Verify every download with the `.sha256` file next to it. Code signing is not planned for now (owner decision, 2026-10-01); [`docs/code-signing-signpath-runbook.md`](../../docs/code-signing-signpath-runbook.md) keeps the steps in case that changes.
