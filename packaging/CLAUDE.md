# packaging/

Per-platform install/uninstall scripts + repo manifests. Цель: единый
one-liner UX на 3 платформах.

## Layout

```
apt-repo/
  install.sh           ← Linux Debian/Ubuntu one-liner: detects distro, adds GPG key + apt source, installs vpnrouter
linux/
  postinst             ← .deb postinst: setcap cap_net_admin,cap_net_bind_service=+eip /opt/vpnrouter/sing-box
  postrm               ← .deb postrm: kill running sing-box + VPNRouter.App
  vpnrouter-update-helper  ← re-applies setcap after cp (xattrs не переживают cp)
windows/
  install.ps1          ← Windows one-liner: UAC self-elevate, sha256 verify, install + Start Menu + ARP entry
  uninstall.ps1        ← matching uninstall: clean install dir, Start Menu shortcut, HKLM Uninstall key
winget/
  manifests/p/PavelLizunov/VPNRouter/<version>/   ← winget submission: version + installer + locale.en-US YAMLs
  README.md            ← submission process
```

## Critical patterns

### sha256 sidecar verification
Каждый release artifact уплоадится с `.sha256` companion. install scripts
скачивают оба + сверяют:
```powershell
# install.ps1 — handles PS5.1 Byte[] bug
$shaTmp = Join-Path $env:TEMP "vpnr-install-sha.txt"
Invoke-WebRequest -Uri $shaAsset.browser_download_url -OutFile $shaTmp -UseBasicParsing
$expectedSha = (Get-Content -Raw $shaTmp).Trim().Split()[0].ToLower()
$actualSha   = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLower()
if ($actualSha -ne $expectedSha) { throw "SHA256 mismatch" }
```

**Важно**: НЕ использовать `$response.Content` напрямую — на PS 5.1 это `Byte[]`
для non-text MIME, `[IO.File]::WriteAllText` ломается на конвертации в строку
(стрингифицирует в "35 32 86 80...") — см. v2.28.2 install.ps1 fix.

### Linux passwordless TUN
v2.28.0+ shippит .deb с `postinst`:
```sh
setcap cap_net_admin,cap_net_bind_service=+eip "$SINGBOX"
```
Sing-box запускается как обычный user без pkexec/sudo. Capability проверяется
в `SingBoxManager.HasNetCapability` через `getcap`.

`vpnrouter-update-helper` re-applies setcap после in-app auto-update (xattrs не
переживают `cp`). Зовётся из `UpdateChecker` после распаковки нового билда.

### macOS sudoers NOPASSWD
В DMG лежит `InstallGuide.html` со skript'ом setting up `/etc/sudoers.d/vpnrouter`
NOPASSWD entry для конкретного `sing-box` binary path. После first Connect
(который шлёт sudo-prompt) — следующие Connect без пароля.

### winget submission (manual)
После каждого stable release:
1. `winget validate --manifest packaging/winget/manifests/p/PavelLizunov/VPNRouter/<v>`
2. PR в [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs).

`packaging/winget/README.md` описывает процесс. **Не submitted в core ещё** —
блокировано на нотариазации DMG (Apple Developer ID не куплен).

## Один из 12 release-артефактов

Каждый release должен иметь все **12 файлов** для всех 3 платформ:
```
VPNRouter-v{V}-win.zip                ← install (~50 MB) + .sha256
VPNRouter-update-v{V}-win.zip          ← lite update (~3 MB) + .sha256
VPNRouter-v{V}-mac.dmg                 ← + .zip (raw .app)
VPNRouter-v{V}-linux-amd64.deb         ← + .sha256
VPNRouter-v{V}-linux-x86_64.AppImage   ← + .sha256
VPNRouter-v{V}-linux.tar.gz            ← + .sha256
```
Если меньше 12 — не cut'аем stable. Re-trigger недостающего CI через `workflow_dispatch`.
