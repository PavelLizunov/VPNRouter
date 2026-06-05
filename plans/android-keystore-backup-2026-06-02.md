# Android signing keystore — disaster-recovery (READ THIS)

**Status:** UPDATED 2026-06-05 — the keystore was **ROTATED** from the old
debug-DN key to a proper `CN=VPNRouter` key (details below). Originally an action
doc (2026-06-02) for product-gap-audit **#142**. This is the single
highest-impact irreversible Android failure mode.

## Why this matters (the one-sentence version)

If the signing keystore is lost, **you can never push another update to anyone
who already installed the app** — Android refuses to update an app with a
different signature, so a new key = a brand-new, separate app and every existing
user is stranded on their current version forever.

## What signs our APKs today  (ROTATED 2026-06-05 → proper DN)

- **Keystore alias:** `vpnrouter`  ·  PKCS12  ·  RSA-2048  ·  valid → 2056-07-17
- **Cert (SHA-256):** `6e50af0f51ff021db30883eca9fd5d4e66c18d7d64fab3b1b346942bf045a221`
- **Cert DN:** `CN=VPNRouter, O=VPNRouter, C=RU` (proper DN — no longer the
  debug-DN cosmetic label).
- **Where it lives:** GitHub Actions secrets `ANDROID_KEYSTORE_BASE64` +
  `ANDROID_KEYSTORE_PASSWORD` (rotated 2026-06-05), **plus** offline backups:
  `C:\Users\vboxuser\vpnrouter.keystore` (dev VM) and
  `Z:\vpnrouter-keystore-backup\` (host shared folder). The store password lives
  in `vpnrouter-keystore-credentials.txt` alongside each backup — **NOT in git,
  NOT in this doc**.

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

GitHub Actions secrets are **write-only** — neither you, nor CI logs, nor the API
can read a secret's value back out. So the secret can *use* the keystore (in CI)
but can never *return* it. **If the only copy is the secret, the keystore is
effectively already lost** — you just won't find out until the day you need it.

Therefore the **original keystore file on disk is the only recoverable copy**,
and it MUST be backed up offline.

## What to back up (3 things)

1. The keystore file itself — `vpnrouter.keystore` (JKS). It was generated
   locally one-time when `ANDROID_KEYSTORE_BASE64` was first set (2026-05-19).
   Find it on the machine where you created the secret.
2. The **store password** (= `ANDROID_KEYSTORE_PASSWORD`).
3. The key alias (`vpnrouter`) and, if different from the store password, the
   key password.

## Backup procedure (do this once, today)

1. **Locate the original file.** On the machine that created the secret:
   ```bash
   # common spots; search broadly if not here
   ls -la ~/vpnrouter.keystore ./vpnrouter.keystore 2>/dev/null
   find ~ -iname '*.keystore' -o -iname '*.jks' 2>/dev/null | head
   ```
2. **Verify it's the right one** — its cert must match what CI ships:
   ```bash
   keytool -list -v -keystore vpnrouter.keystore -alias vpnrouter | grep -i "SHA-?256\|Owner"
   # SHA256 fingerprint (strip colons) must == c3fc0ceab00a0b8b...c8d3d37a
   # Owner must == CN=Android Debug, O=Android, C=US
   ```
3. **Store ≥2 offline copies**: e.g. an encrypted USB drive + your password
   manager's secure-file vault. Put the password in the password manager entry
   alongside (NOT in the same plaintext file as the keystore).
4. **If you CANNOT find the original file** (only the secret exists): you must
   **rotate now while you still can re-derive nothing** — generate a fresh
   keystore, back it up first, then replace both secrets. Because there are
   essentially no installed users yet except your own test phone, a rotation
   today costs one reinstall; a rotation *after* you have real users is a
   flag-day that strands them. Generate:
   ```bash
   keytool -genkeypair -v -keystore vpnrouter.keystore -alias vpnrouter \
     -keyalg RSA -keysize 2048 -validity 10000 \
     -dname "CN=VPNRouter, O=NiniTux, C=RU"   # proper DN this time
   # then: base64 the file -> set ANDROID_KEYSTORE_BASE64; set ANDROID_KEYSTORE_PASSWORD
   gh secret set ANDROID_KEYSTORE_BASE64 < <(base64 -w0 vpnrouter.keystore)
   gh secret set ANDROID_KEYSTORE_PASSWORD
   ```
   (After a rotation, re-sign + re-upload the current release's APK so the live
   download matches the new key.)

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
  `plans/android-ci-distribution-roadmap-2026-05-31.md`.
