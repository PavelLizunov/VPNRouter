# Android CI-unblock + distribution roadmap

**Date:** 2026-05-31
**Vector (user-chosen):** unblock Android CI (NU1102) + get to a free,
installable, auto-updating distribution. NOT feature-parity, NOT polish — those
come after users can actually install a CI-built APK.
**Scope:** full multi-phase roadmap.
**Hard constraint:** zero paid services. No Play Store ($25), no paid signing.
APK signing is already free (keystore secret). Distribution must be free
(self-hosted on the existing `vpn.ninitux.com` GitHub Pages + GitHub Releases).

## STATUS (updated 2026-06-02) — Phase A sidestepped; C/D mostly done

Phase A (unblock the full Android build in CI) is **confirmed upstream-blocked**
on every hosted-runner OS — a live `windows-latest` probe hit the same NU1102 as
linux/macos (.NET 10 withdrew the host Mono runtime pack for all host RIDs; only
the `mono.android-arm64` *target* pack has 10.0.x). A1 (GA .NET 10) hasn't
landed; A2 (self-hosted Mac runner) is a security surface deferred. So instead of
unblocking the full build in CI, we **split build from signing**:

- **Build** the APK *unsigned* locally on the warm-cache Windows VM
  (`build-android.ps1`, or a no-signing-props `dotnet publish`). NU1102 only
  bites a *clean* restore, never the warm VM — same model as the Windows desktop
  ZIP, which is also built locally.
- **Sign** in CI via the new **`.github/workflows/sign-android.yml`** — it
  decodes `ANDROID_KEYSTORE_BASE64` / `ANDROID_KEYSTORE_PASSWORD` (the secrets
  that already sign our APKs), runs `zipalign` + `apksigner` (no .NET at all →
  no NU1102), verifies, and uploads `VPNRouter-v<V>-android.apk` + `.sha256` to
  the release. The unsigned staging asset (`-android-UNSIGNED.apk`, which the
  install-page regex deliberately does NOT match) is removed afterward.

This keeps the signing identity in CI (consistent in-place updates) without
anyone needing the write-only keystore secret on a local machine.

**Done:** versionCode now monotonic from `-Version` (csproj, commit 09de5d5);
`build-android.ps1` local signed/unsigned build; in-app updater already shipped
(`AndroidApp.AutoUpdate.cs` / `SideloadSource` — matches `VPNRouter-v*-android.apk`
+ `.sha256`); `vpn.ninitux.com/android` install page (commit 8ba6e4e, deploys on
next release event); `sign-android.yml` sign-only workflow.

**Remaining:** produce + upload the first signed APK for v2.38.2 (unsigned build
in flight on the VM → upload as `-UNSIGNED` → run `sign-android.yml`); keystore
offline-backup doc (Phase C); self-hosted F-Droid (Phase D, deferred secondary);
fold into release flow + docs (Phase E).

## Grounded current state (verified 2026-05-31)

- **Device loop is LIVE**: phone `54499112209` is attached to the Mac build host
  (`slovn@192.168.0.246`, macOS 15.5), `adb devices` sees it over SSH. So
  build -> `adb install` -> launch -> connect -> verify on real hardware works
  today.
- **TFM**: `VPNRouter.Android.csproj` = `net10.0-android36.0`,
  `SupportedOSPlatformVersion=23.0` (Android 6+), RIDs arm64/arm/x64/x86.
- **No `global.json`** — the .NET SDK version floats; CI installs .NET 8 + a
  .NET 10 preview (10.0.300) per `build-android.yml`.
- **NU1102 root** (MEMORY + task #75): the .NET 10 *preview* SDK's workload
  manifest references a Mono runtime pack
  (`Microsoft.NETCore.App.Runtime.Mono.*` = 10.0.x) that is not on any public
  NuGet feed -> `NU1102` on every restore on GitHub-hosted runners.
  `build-android.yml` tag-push trigger has been gated to `workflow_dispatch`
  only since v2.35.0-r16. A `10.0.100` SDK pin was tried and did NOT fix it.
- **libbox.aar** integrity is already hard-gated by SHA256 in CI (task #133,
  commit 817a9ed). Local build recipe: `tools/build-libbox-aar.sh`.
- **APK signing** is already wired: `ANDROID_KEYSTORE_BASE64` +
  `ANDROID_KEYSTORE_PASSWORD` secrets, signed in the `dotnet publish` step.
- **Prior art** (consult, do not duplicate): `plans/phase5-android-net10-*.md`,
  `plans/phase6-ci-android-yml-2026-05-18.md`, `memory/vpnrouter-android-port.md`.

## Phase A — Unblock CI (NU1102). HIGHEST priority; blocks D.

The whole vector dies here if not solved. Evaluate three strategies, pick in
order of cheapness, stop at the first that works.

### A1 — Move to GA .NET 10 (try FIRST)
It is 2026-05; .NET 10 GA'd ~Nov 2025. GA runtime packs ARE on public NuGet, so
the preview-only Mono pack that causes NU1102 should disappear on a GA SDK.
- Confirm a GA `.NET 10` + GA `android` workload exist (check on the Mac and on
  a GitHub-hosted runner).
- Add a **`global.json`** pinning the GA SDK (deterministic restore; currently
  the SDK floats, which is itself a latent flake source).
- Bump `build-android.yml` `Setup .NET` to the GA channel; drop the preview pin.
- Re-run `dotnet restore` for `VPNRouter.Android.csproj` on a hosted runner;
  NU1102 should be gone.

### A2 — Self-hosted Mac runner (fallback; robust + free)
The Mac already builds Android fine locally (its preview packs are installed),
so it never hits NU1102. Register it as a GitHub Actions **self-hosted runner**
and route the Android job to it.
- SECURITY (public repo): self-hosted runners must NOT execute fork-PR code.
  Gate the Android job to `push`/`workflow_dispatch` by the owner only; never
  `pull_request` from forks. Prefer an ephemeral/just-in-time runner the user
  starts for a release rather than a standing daemon.
- Pro: uses an asset we already have; bypasses the SDK feed problem entirely.
- Con: the Mac must be online for an Android CI run; a standing runner is a
  security surface. Mitigated by ephemeral + owner-only gating.

### A3 — Vendor the missing runtime pack (last resort)
Host `Microsoft.NETCore.App.Runtime.Mono.*` on a custom feed (GitHub Packages or
a committed local feed) so restore resolves it. Hacky, ongoing maintenance —
only if A1 and A2 both fail.

**Acceptance (A):** `build-android.yml` produces a signed APK on **tag push**
(re-enable the gated trigger), green, with the libbox SHA gate intact. APK
installs on device `54499112209`.

## Phase B — Real-device verification loop. Start NOW, in parallel.

Does not depend on Phase A — it can run off a local Mac build immediately, and
becomes the verification backbone for every later phase (the Android analog of
the desktop `post-ship-mcp-verify` skill).

- Script `tools/android-device-test.sh`: SSH to the Mac, build (or pull the CI
  APK), `adb -s 54499112209 install -r <apk>`, launch the activity, grant the
  VpnService consent, tail `adb logcat` for `[ERR]`/exceptions/native crashes.
- Smoke scenario: launch -> add a test VLESS subscription (use the Virtual
  Penguin test sub from the handoff) -> Connect -> confirm the OS VPN key icon +
  a real egress change (per-layer `current.json` pulled from the device +
  `singbox` log, since egress-IP alone is unreliable in the double-VPN test env).
- Output: PASS/FAIL + logcat excerpt, same shape as the desktop verify.

**Acceptance (B):** one command produces install -> launch -> connect ->
PASS/FAIL on the attached phone. Re-runnable for every change below.

## Phase C — Signing, versioning, provenance (all free).

- Confirm CI emits a **signed release APK** (keystore secrets already exist);
  fail the build if unsigned.
- **`version.properties`** automation: `VERSION_CODE` +1 + `VERSION_NAME` from
  the release tag, kept in lockstep with `AppVersion` (so Android version ==
  tag, mirroring desktop rule #5).
- **Keystore disaster-recovery doc**: losing the keystore = can never update an
  already-installed app (new signature = new app). Document an offline backup of
  the keystore + password. This is the single highest-risk Android-distribution
  failure mode and it is free to mitigate.
- Optional free provenance: GitHub build-provenance attestation on the APK (the
  free remnant of the parked #132 signing task — fits here at zero cost).

**Acceptance (C):** every CI APK is signed with the release keystore, carries a
tag-matching version, and the keystore backup procedure is documented.

## Phase D — Distribution (free channels). The payoff.

- **GitHub Releases**: attach the signed APK to each release (already a release
  asset shape; restore it to the auto-published set once A is green).
- **`vpn.ninitux.com/android`** install page (GitHub Pages, same infra as the
  APT repo): direct APK link + a QR code for phone-side install + a short
  "allow unknown sources" guide.
- **Self-hosted F-Droid repo** (the key free auto-update channel): generate an
  fdroid repo (`fdroidserver`) hosting the signed APK at
  `vpn.ninitux.com/fdroid`; users add that repo URL to the F-Droid client and
  get **automatic updates** with no Google account, no Play Store fee. Reuses
  the existing Pages deploy.
- **Android in-app updater**: desktop has `UpdateChecker` (reads the GitHub
  Releases API). Audit whether Android has an equivalent; if not, add a minimal
  one: check latest APK version -> download -> launch the package-installer
  intent (`REQUEST_INSTALL_PACKAGES`). Scope confirmed during execution.
- Official F-Droid (their repo builds it) = stretch goal; self-hosted first.

**Acceptance (D):** a user can install the signed APK from `vpn.ninitux.com`
(direct + QR) AND subscribe to the self-hosted F-Droid repo for auto-updates;
in-app update path verified on device `54499112209`.

## Phase E — Release-flow integration + docs.

- Fold Android back into the rolling-rN + stable flow: re-enable the tag
  trigger, include the APK in the published asset set, auto-publish on stable.
- Update `CURRENT_STATE.md` (Android now CI-built + distributed), `README.md` /
  `README.ru.md`, `packaging/CLAUDE.md` (Android install section), and the
  `ship-rolling-candidate` / `cut-stable` skills to cover the Android asset.
- Keep the libbox tooling-release retention noted in `tools/native-deps.md`.

**Acceptance (E):** a normal `vX.Y.Z` cut ships a signed, installable,
auto-updating Android APK without manual local steps; docs match reality.

## Sequencing

1. **Phase B first/parallel** — stand up the device loop now (local Mac build),
   so everything else is verifiable on real hardware. Cheap, immediate.
2. **Phase A** — the crux; try A1 (GA .NET 10) then A2 (Mac runner).
3. **Phase C** — signing/version/keystore-backup before we hand APKs to users.
4. **Phase D** — distribution (Pages page + F-Droid repo + in-app updater).
5. **Phase E** — fold into the standard release flow + docs.

## Risk / cost notes

- A2 (self-hosted runner) on a **public** repo is the main security risk —
  owner-only + ephemeral, never fork-PR.
- Keystore loss (Phase C) is the highest-impact irreversible failure — back it
  up before any wide distribution.
- Token cost: Phase A diagnosis and Phase D F-Droid setup are the heaviest;
  Phase B is cheap and high-leverage — do it first.
- This roadmap spends NO money. The only thing money would buy (Play Store
  reach, OS-level Authenticode/notarization) is explicitly out of scope.

## Cross-references

- `plans/phase6-ci-android-yml-2026-05-18.md` (prior CI attempt)
- `plans/product-gap-audit-2026-05-30.md` (#132 signing parked; provenance note)
- `tools/build-libbox-aar.sh`, `tools/native-deps.md` (libbox pin)
- `.github/workflows/build-android.yml`, `.github/workflows/CLAUDE.md`
- `memory/vpnrouter-android-port.md`, `memory/testing_double_vpn_layers.md`
