# .github/workflows zone guidelines

`.github/workflows/` contains GitHub Actions workflows for continuous integration, multi-platform builds, release publishing, and automated checks.

## Key workflows

- Platform builds/signing: macOS DMG/ZIP (`build-mac.yml`), Linux DEB/AppImage/tarball (`build-linux.yml`), Android ARM64 APK (`build-android.yml`, `sign-android.yml`), and Windows SignPath (`sign-windows.yml`).
- Automation & verification: C# regression suite (`test.yml`), CodeQL (`codeql.yml`), placeholder fingerprint guard (`grep-placeholder-fingerprints.yml`), release integrity (`verify-release-integrity.yml`), public config pool aggregation (`build-free-pool.yml`), APT publishing (`publish-apt.yml`), and Windows update smoke tests (`test-windows-update.yml`).

## Release asset contract

- A complete release requires 16 total assets: 4 Windows, 4 macOS, 6 Linux, and 2 Android files.
- Every binary release artifact must be accompanied by its `.sha256` sidecar.

## Safety and guidelines

- Do not force-update published release tags.
- Follow canonical safety rules in [`docs/agent-contract.md`](../../docs/agent-contract.md).

## Zone checks

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseToolingContractTests|FullyQualifiedName~PostShipVerifierContractTests|FullyQualifiedName~BratVerifierContractTests"
```
