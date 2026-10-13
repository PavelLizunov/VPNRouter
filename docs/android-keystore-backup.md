# Android signing keystore — disaster-recovery (READ THIS)

**Status:** UPDATED 2026-06-05 — the keystore was **ROTATED** from the old
debug-DN key to a proper `CN=VPNRouter` key (details below). Originally an action
doc (2026-06-02) for product-gap-audit **#142**. This is the single
highest-impact irreversible Android failure mode.

## What signs our APKs today  (ROTATED 2026-06-05 → proper DN)

- **Keystore alias:** `vpnrouter`  ·  PKCS12  ·  RSA-2048  ·  valid → 2056-07-17
- **Cert (SHA-256):** `6e50af0f51ff021db30883eca9fd5d4e66c18d7d64fab3b1b346942bf045a221`
- **Cert DN:** `CN=VPNRouter, O=VPNRouter, C=RU` (proper DN — no longer the
  debug-DN cosmetic label).

### Verified recovery map (2026-08-02)

The bundle contains the production PKCS12 keystore and its credentials. It is
encrypted with AES-256-CBC/PBKDF2-SHA256 (600,000 iterations); neither the raw
keystore nor a plaintext password is stored in git.

| Host | Encrypted bundle | Recovery key |
|---|---|---|
| Windows dev | `C:\Users\x3d_mutant\.vpnrouter-secure\android-signing\android-signing-backup-20260802.tar.enc` | same directory: `backup-recovery-key.dpapi.xml`, protected by the current Windows user's DPAPI |
| windows-brat | `C:\Users\tester\.vpnrouter-secure\android-signing\android-signing-backup-20260802.tar.enc` | same directory: `backup-recovery-key.dpapi.xml`, protected by tester's DPAPI |
| Mac build host | `/Users/slovn/.vpnrouter-secure/android-signing/android-signing-backup-20260802.tar.enc` | same directory: `backup-recovery-key.local`, mode `0600` inside a mode `0700` directory |

Encrypted bundle SHA-256:
`6055c75b975daec29ee90d680ded2f243a063f0bef0ef19038daddb312ded562`.
Windows and windows-brat copies have protected ACLs; Mac files are mode `0600`.
An actual decrypt-to-temporary-directory recovery on the Mac verified alias
`vpnrouter` and certificate SHA-256 `6e50af0f...45a221`. Temporary GitHub
artifact, recovery secret, and export branch were deleted after verification.

### Previous key (superseded 2026-06-05)
- Old cert `c3fc0cea…d37a`, DN `CN=Android Debug, O=Android, C=US`, store-pass
  `android`. Signed v2.35.x → the first v2.41.0 APK. Archived as
  `vpnrouter-keystore-OLD-debug.keystore` (kept for the record only).
- **Flag-day:** the rotation changed the signing identity, so any install from a
  pre-rotation APK **cannot update in place** — it must be uninstalled +
  reinstalled. The rotation was done deliberately at near-zero install base (test
  devices only), which is the cheapest possible moment. v2.41.0's live APK was
  re-signed with the new key.

## The trap: the GitHub secret is NOT a backup

Therefore a separately recoverable encrypted bundle is mandatory. The verified
2026-08-02 bundle above is the current disaster-recovery source.

## What to back up (3 things)

1. The keystore file itself — `vpnrouter.keystore` (JKS). It was generated
   locally one-time when `ANDROID_KEYSTORE_BASE64` was first set (2026-05-19).
   Find it on the machine where you created the secret.
2. The **store password** (= `ANDROID_KEYSTORE_PASSWORD`).
3. The key alias (`vpnrouter`) and, if different from the store password, the
   key password.

## Versioning (already handled — no action)

`versionCode` is derived monotonically from the release version in
`VPNRouter.Android.csproj` (`major*1_000_000 + minor*1_000 + patch`, e.g.
2.38.2 → 2_038_002), so there is **no separate `version.properties` to maintain**
— bumping `AppVersion`/the release tag is enough. Android `versionName` =
the full version string.

## Cross-references

- `.github/workflows/sign-android.yml` (signs with the secret),
  `build-android.ps1` (local signed build),
  `.github/workflows/CLAUDE.md` (secret inventory),
  `git show 6491be4c:plans/android-ci-distribution-roadmap-2026-05-31.md`.
