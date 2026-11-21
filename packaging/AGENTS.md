# packaging zone guidelines

`packaging/` contains per-platform installer scripts, package manifests, and repository setup files to ensure unified installation UX.

## Scope and structure

- `windows/`: Installation (`install.ps1`), uninstallation (`uninstall.ps1`), and self-repair (`repair.cmd`) scripts, plus the Inno Setup installer script `vpnrouter.iss` (with `installer/stop-vpnrouter.ps1`), built by `tools/build-installer.ps1` into `VPNRouter-Setup-vX.Y.Z.exe`. The installer is not yet part of the release asset contract (owner-commanded wiring comes later) and is not signed.
- `linux/`: Debian/Ubuntu package maintainer scripts (`postinst`, `postrm`), update helper (`vpnrouter-update-helper`), desktop entries, and launcher wrapper.
- `apt-repo/`: APT repository setup script (`install.sh`), reprepro configuration, and public signing key.
- `winget/`: Package manifests (`manifests/`) and submission guidelines for winget.
- `android-page/`: Landing page (`index.html`) for direct APK downloads.

## Critical patterns

- SHA256 sidecar verification: All installer scripts must download and verify matching `.sha256` companion files.
- Linux passwordless TUN: `.deb` packages apply `setcap cap_net_admin,cap_net_bind_service=+eip` on sing-box binary via `postinst` and `vpnrouter-update-helper`.
- macOS privileged helper: the drag-and-drop DMG does not modify sudoers and contains no Terminal command. On first Connect, `VPNRouter.App` uses one administrator-approved `osascript` flow to install the root-owned helper, root-owned `sing-box`, manifest and per-uid policy. The only NOPASSWD command is `/Library/PrivilegedHelperTools/com.vpnrouter.privileged` with no arguments; the helper validates the protected directory chain, stages a validated config, binds stop to a root-owned per-uid session record, and requires a per-config Clash secret. `packaging/macos/README.md` owns the contract.
- Release asset contract: 16 total release files (14 desktop platform artifacts/sidecars + 2 Android artifacts/sidecars).

## Zone checks

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseToolingContractTests|FullyQualifiedName~PostShipVerifierContractTests"
```
