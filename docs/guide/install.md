# Install and download

Part of the [VPNRouter README](../../README.md).

## Install

<table>
<tr>
<td width="80" align="center">🐧<br><b>Linux</b></td>
<td>

```bash
curl -fsSL https://vpn.ninitux.com/install.sh | sudo sh
```
Debian / Ubuntu / Mint / Pop / elementary. Adds the signed apt repo, installs `vpnrouter`, sets up passwordless VPN via POSIX capabilities. Updates: `sudo apt upgrade`.
</td>
</tr>
<tr>
<td align="center">🍎<br><b>macOS</b></td>
<td>

```bash
brew install --cask pavellizunov/vpnrouter/vpnrouter
```
Apple Silicon. Auto-strips Gatekeeper quarantine. First launch prompts once for sudoers setup, then passwordless. Updates: `brew upgrade --cask vpnrouter`.
</td>
</tr>
<tr>
<td align="center">🪟<br><b>Windows</b></td>
<td>

```powershell
iwr -useb https://vpn.ninitux.com/install.ps1 | iex
```
Windows 10/11 x64. Auto-elevates via UAC. Registers Start Menu + Add/Remove Programs. Updates: re-run the same command. Uninstall: Settings → Apps → VPNRouter.

Prefer a regular setup wizard? Download `VPNRouter-Setup-v{version}.exe` from [Releases](https://github.com/PavelLizunov/VPNRouter/releases). It asks for administrator rights, installs to `Program Files`, adds a Start Menu entry and an uninstaller; desktop icon, autostart, the background service and Defender exclusions are optional checkboxes, all off by default. The installer is not code-signed, so Windows SmartScreen may warn: compare its hash with the `.sha256` file first.
</td>
</tr>
<tr>
<td align="center">🤖<br><b>Android</b></td>
<td>

```
Download VPNRouter-v{version}-android-arm64.apk from Releases
```
Android 6.0+ (API 23), ARM64. Install the APK outside the Play Store. Supports QR scanning, subscription paste and an in-app update prompt. Permissions cover VPN operation, network state, notifications, app enumeration, APK installation, camera and power management; see the [Android manifest](../../VPNRouter.Android/AndroidManifest.xml).
</td>
</tr>
</table>

Prefer manual install? See [**Manual download**](#manual-download) below for ZIPs / DMG / AppImage / deb / tar.gz.

## Manual download

For desktop installation commands and Android APK instructions, see [Install](#install). Download the latest stable build from [Releases](https://github.com/PavelLizunov/VPNRouter/releases/latest); rolling candidates are listed under [all releases](https://github.com/PavelLizunov/VPNRouter/releases).

Published version tags are permanent source references; older candidate downloads may be retired separately. See [tag and release retention](../../docs/tag-retention-policy.md) for preservation rules and service exceptions.

| File | Platform | What it is |
|---|---|---|
| `VPNRouter-v{version}-win.zip` | 🪟 Windows | Full installer (first install) |
| `VPNRouter-update-v{version}-win.zip` | 🪟 Windows | DLL-only update (if you're already on a recent version) |
| `VPNRouter-Setup-v{version}.exe` | 🪟 Windows | Setup wizard (Inno Setup, not code-signed): installs the same files as the install zip, with an uninstaller and optional autostart/service checkboxes |
| `VPNRouter-*-win.zip.sha256` | 🪟 Windows | SHA256 companion file — auto-updater verifies the download against this before extracting (v2.15.8+) |
| `VPNRouter-v{version}-mac.dmg` | 🍎 macOS | Drag-install DMG (Apple Silicon) with `InstallGuide.html` for one-time sudoers setup |
| `VPNRouter-v{version}-mac.zip` | 🍎 macOS | Raw `.app` bundle (for manual install) |
| `VPNRouter-v{version}-linux-amd64.deb` | 🐧 Linux | Debian/Ubuntu package (desktop entry + `setcap` for passwordless TUN; no systemd service). Install: `sudo dpkg -i <file>.deb` |
| `VPNRouter-v{version}-linux-x86_64.AppImage` | 🐧 Linux | Portable single-file build. `chmod +x`, run, no install needed |
| `VPNRouter-v{version}-linux.tar.gz` | 🐧 Linux | Raw tarball (for manual install or packaging into other formats) |
| `VPNRouter-v{version}-android-arm64.apk` | 🤖 Android | Signed ARM64 APK, API 23+. Built and signed by `build-android.yml` for every release tag, then published at Releases and [`vpn.ninitux.com/android`](https://vpn.ninitux.com/android). An in-app updater delivers future APKs. |
| `*.sha256` companion files | All | SHA256 hash sidecars — auto-updater + CI integrity check verify before extracting. Every binary above ships with a `<file>.sha256` sidecar (Windows `*-win.zip` + `*-update-win.zip` + `*-Setup-*.exe`, macOS `*-mac.dmg` + `*-mac.zip`, Linux `*.deb` + `*.AppImage` + `*.tar.gz`). Compare `sha256sum <file>` on Linux, `shasum -a 256 <file>` on macOS, or `Get-FileHash -Algorithm SHA256 <file>` on Windows with the 64-character hash in its sidecar. Some sidecars contain only the hash and cannot be used directly with `sha256sum -c`. |

The scheduled pool job publishes this separate artifact when it succeeds:

| File | What it is |
|---|---|
| [`free-pool-latest/pool.json`](https://github.com/PavelLizunov/VPNRouter/releases/tag/free-pool-latest) | Public VLESS configurations and GeoIP metadata; pool size varies. Consumed by the in-app Free Configs tab. |

Run `VPNRouter.App.exe` as Administrator on Windows (required for TUN adapter + ETW process monitor + Firewall rules). On macOS, follow the in-DMG `InstallGuide.html` for the one-time sudoers entry that lets TUN come up without a password prompt each time. On Linux, the `.deb` applies `setcap cap_net_admin,cap_net_bind_service` to the bundled sing-box so TUN comes up without root or a password (no systemd service is installed); an unsandboxed read-only `AppImage` falls back to a host `pkexec` password prompt. AppImages wrapped in bubblewrap or a user namespace (including NixOS `appimageTools.wrapType2`) cannot acquire permission to create the host TUN interface even when `getcap` shows the file capability. Use a native distro package outside that sandbox.

## Requirements

- **Windows 10/11 x64** — Administrator rights (TUN, firewall, ETW)
- **macOS 12+** — Apple Silicon (arm64). Intel is not currently packaged. First-run sudoers setup required (guided)
- **Linux x86_64** — kernel 5.6+ (TUN/wireguard), `glibc` 2.31+. Tested on Ubuntu 22.04 / 24.04 and Debian 12. `nftables` (`nft`) for the kill switch.
- **Android 6.0+** (API 23+), ARM64. Uses Android's `VpnService` API (no root required). Camera permission is only requested when scanning a QR code.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — bundled in the desktop installer
- A VLESS+Reality server, or use the Free Configs tab for a public one
