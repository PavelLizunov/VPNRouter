# .github/workflows/

Current GitHub Actions map. There are 12 repository workflow files, plus the
GitHub-managed Pages deployment workflow.

## Быстрая проверка

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseToolingContractTests|FullyQualifiedName~PostShipVerifierContractTests|FullyQualifiedName~BratVerifierContractTests"
```

## Workflows

| File | Trigger | Purpose |
|---|---|---|
| `build-mac.yml` | `v*` tag, manual | builds Apple Silicon DMG + ZIP, uploads both with SHA sidecars, updates Homebrew on stable |
| `build-linux.yml` | `v*` tag, manual | builds `.deb`, AppImage and tarball with SHA sidecars |
| `build-android.yml` | `v*` tag, manual | builds and production-signs the ARM64 APK, verifies its certificate, uploads APK + SHA |
| `build-free-pool.yml` | every 6 hours, manual | publishes the rolling public-config `pool.json` |
| `publish-apt.yml` | release published, manual | rebuilds the signed APT repository and GitHub Pages installer content |
| `test.yml` | pushes, PRs, tags, manual | Linux regression suite plus Windows characterization and verifier-contract tests |
| `grep-placeholder-fingerprints.yml` | pushes, PRs, manual | enforces the single source for placeholder fingerprints |
| `test-windows-update.yml` | release published, `v*` tag, manual | runs the real Windows update helper end to end in a temporary install |
| `verify-release-integrity.yml` | release published/edited, manual | checks versions and SHA sidecars; missing parallel assets are warnings here and a hard failure in `tools/post-ship-verify.ps1` |
| `codeql.yml` | weekly, manual | advisory C# static analysis; not a stable-cut gate |
| `sign-android.yml` | manual | legacy fallback for signing a locally prepared APK; the normal path is `build-android.yml` |
| `sign-windows.yml` | manual | inert until SignPath enrollment and secrets are configured |

## Release asset contract

A complete release contains exactly 16 files: 4 Windows, 4 macOS, 6 Linux,
and 2 Android files. Every binary artifact has a `.sha256` sidecar. The Android
pair is `VPNRouter-v{V}-android-arm64.apk` and its sidecar.

`verify-release-integrity.yml` can run while the three platform builds are still
uploading, so it does not treat an incomplete set as final. The authoritative
post-ship gate waits for the release workflows and then requires the exact
16-file inventory.

## Secrets

| Secret | Used by |
|---|---|
| `GITHUB_TOKEN` | release uploads and repository dispatches |
| `HOMEBREW_TAP_DISPATCH_TOKEN` | `build-mac.yml` stable Homebrew update |
| `ANDROID_KEYSTORE_BASE64` | Android release signing |
| `ANDROID_KEYSTORE_PASSWORD` | Android keystore and key password |

`libbox.aar` is fetched from the SHA-pinned internal tooling release; the old
`LIBBOX_AAR_BASE64` secret is retired. Rotation details live in
`.github/SECRETS.md`.

## Release sequence

1. The exact release commit is tagged `vX.Y.Z[-rN]`.
2. macOS, Linux and Android builds start from that tag.
3. `build.ps1 -Upload` creates the release and uploads both Windows ZIPs with
   their SHA sidecars.
4. Platform workflows upload the remaining assets. If a workflow reached its
   upload step before the release existed, rerun it manually for the same tag.
5. The exact-SHA CI gate and `tools/post-ship-verify.ps1` must pass. The latter
   requires all 16 canonical assets and verifies both Windows driver bundles.
6. Stable releases additionally update APT and Homebrew.

Never force-update a published stable tag. A prerelease tag may be replaced only
before it is published; otherwise increment `-rN`.

## winget submission

winget publication is manual. Validate the versioned manifest under
`packaging/winget/manifests/` and open a PR to `microsoft/winget-pkgs` after a
stable release. See `packaging/winget/README.md`.
