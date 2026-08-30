# packaging zone guidelines

`packaging/` contains per-platform installer scripts, package manifests, and repository setup files to ensure unified installation UX.

## Scope and structure

- `windows/`: Installation (`install.ps1`), uninstallation (`uninstall.ps1`), and self-repair (`repair.cmd`) scripts.
- `linux/`: Debian/Ubuntu package maintainer scripts (`postinst`, `postrm`), update helper (`vpnrouter-update-helper`), desktop entries, and launcher wrapper.
- `apt-repo/`: APT repository setup script (`install.sh`), reprepro configuration, and public signing key.
- `winget/`: Package manifests (`manifests/`) and submission guidelines for winget.
- `android-page/`: Landing page (`index.html`) for direct APK downloads.

## Critical patterns

- SHA256 sidecar verification: All installer scripts must download and verify matching `.sha256` companion files.
- Linux passwordless TUN: `.deb` packages apply `setcap cap_net_admin,cap_net_bind_service=+eip` on sing-box binary via `postinst` and `vpnrouter-update-helper`.
- macOS NOPASSWD: the drag-and-drop DMG does not modify sudoers. On first privileged app setup, `VPNRouter.App` uses an administrator-approved `osascript` flow to create `/etc/sudoers.d/vpnrouter` for sing-box execution.
- Release asset contract: 16 total release files (14 desktop platform artifacts/sidecars + 2 Android artifacts/sidecars).

## Zone checks

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseToolingContractTests|FullyQualifiedName~HelperCmdParserGuardTests|FullyQualifiedName~PostShipVerifierContractTests"
```
