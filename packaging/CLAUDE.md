# packaging/

Per-platform install/uninstall scripts + repo manifests. Цель: единый
one-liner UX на 3 платформах.

## Быстрая проверка

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseToolingContractTests|FullyQualifiedName~HelperCmdParserGuardTests|FullyQualifiedName~PostShipVerifierContractTests"
```

## Layout

```
apt-repo/
  install.sh           ← Linux Debian/Ubuntu one-liner: detects distro, adds GPG key + apt source, installs vpnrouter
  distributions        ← reprepro distributions config
  vpnrouter-apt-public.asc ← apt repo signing public key
  README.md            ← apt repo / reprepro notes
linux/
  postinst             ← .deb postinst: setcap cap_net_admin,cap_net_bind_service=+eip /opt/vpnrouter/sing-box
  postrm               ← .deb postrm: kill running sing-box + VPNRouter.App
  vpnrouter-update-helper  ← re-applies setcap after cp (xattrs не переживают cp)
  VPNRouter.sh         ← launcher wrapper
  vpnrouter.desktop    ← .desktop entry
  com.vpnrouter.update.policy ← polkit policy (legacy pkexec path)
  README.txt
windows/
  install.ps1          ← Windows one-liner: UAC self-elevate, sha256 verify, install + Start Menu + ARP entry
  uninstall.ps1        ← matching uninstall: clean install dir, Start Menu shortcut, HKLM Uninstall key
  repair.cmd           ← hosted self-repair tool (vpn.ninitux.com/repair.cmd): full reinstall + service reset
winget/
  manifests/p/PavelLizunov/VPNRouter/<version>/   ← winget submission: version + installer + locale.en-US YAMLs
  README.md            ← submission process
android-page/
  index.html           ← vpn.ninitux.com/android APK download page (published to gh-pages)
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
единственный закоммиченный манифест `2.27.2` (не bump'ался). Блокер по README:
нет dedicated GitHub-аккаунта под fork microsoft/winget-pkgs + PAT для авто-PR.

## Release-артефакты — ровно 16 файлов

Каждый release должен иметь **14 desktop-файлов** для 3 desktop-платформ
(каждый artifact + `.sha256` companion):
```
VPNRouter-v{V}-win.zip                 ← install (~50 MB) + .sha256
VPNRouter-update-v{V}-win.zip          ← lite update (~3 MB) + .sha256
VPNRouter-v{V}-mac.dmg                 ← + .sha256
VPNRouter-v{V}-mac.zip                 ← raw .app + .sha256
VPNRouter-v{V}-linux-amd64.deb         ← + .sha256
VPNRouter-v{V}-linux-x86_64.AppImage   ← + .sha256
VPNRouter-v{V}-linux.tar.gz            ← + .sha256
```
= 7 artifacts × 2 = 14 desktop-файлов. `build-android.yml` собирает и
подписывает ARM64 APK (`VPNRouter-v{V}-android-arm64.apk` + `.sha256`), поэтому
полный контракт любого текущего release — **16** файлов. Недостающий platform
workflow можно перезапустить через `workflow_dispatch`, но stable не cut'аем,
пока post-ship gate не подтвердит точный набор из 16 файлов.
