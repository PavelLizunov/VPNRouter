# .github/workflows zone guidelines

`.github/workflows/` contains GitHub Actions workflows for continuous integration, multi-platform builds, release publishing, and automated checks.

## Key workflows

- Platform builds/signing: macOS DMG/ZIP (`build-mac.yml`), Linux DEB/AppImage/tarball (`build-linux.yml`), Android ARM64 APK (`build-android.yml`; `sign-android.yml` is retired and always fails), and Windows SignPath (`sign-windows.yml`).

## Release asset contract

- A complete release requires 18 total assets: 6 Windows (zip, update zip, installer), 4 macOS, 6 Linux, and 2 Android files.
- Every binary release artifact must be accompanied by its `.sha256` sidecar.

## Zone checks

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseToolingContractTests|FullyQualifiedName~PostShipVerifierContractTests|FullyQualifiedName~BratVerifierContractTests"
```
