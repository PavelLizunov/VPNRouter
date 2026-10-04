# Установка и загрузка

Часть [README VPNRouter](../../../README.ru.md).

## Установка

<table>
<tr>
<td width="80" align="center">🐧<br><b>Linux</b></td>
<td>

```bash
curl -fsSL https://vpn.ninitux.com/install.sh | sudo sh
```
Debian / Ubuntu / Mint / Pop / elementary. Добавляет подписанный apt-репо, ставит `vpnrouter`, включает passwordless VPN через POSIX capabilities. Обновление: `sudo apt upgrade`.
</td>
</tr>
<tr>
<td align="center">🍎<br><b>macOS</b></td>
<td>

```bash
brew install --cask pavellizunov/vpnrouter/vpnrouter
```
Apple Silicon. Авто-снимает Gatekeeper quarantine. При первом запуске однократно просит пароль для sudoers, дальше passwordless. Обновление: `brew upgrade --cask vpnrouter`.
</td>
</tr>
<tr>
<td align="center">🪟<br><b>Windows</b></td>
<td>

```powershell
iwr -useb https://vpn.ninitux.com/install.ps1 | iex
```
Windows 10/11 x64. Авто-поднимается через UAC. Регистрирует Start Menu + Add/Remove Programs. Обновление: запустить ту же команду повторно. Удалить: Settings → Приложения → VPNRouter.

Нужен обычный мастер установки? Скачайте `VPNRouter-Setup-v{version}.exe` со страницы [Releases](https://github.com/PavelLizunov/VPNRouter/releases). Он просит права администратора, ставит в `Program Files`, добавляет пункт в меню Пуск и деинсталлятор; ярлык на рабочем столе, автозапуск, фоновая служба и исключения Defender — необязательные галочки, по умолчанию все выключены. Установщик не подписан цифровой подписью, поэтому SmartScreen может предупредить: сначала сверьте его хеш с файлом `.sha256`.
</td>
</tr>
<tr>
<td align="center">🤖<br><b>Android</b></td>
<td>

```
Скачайте VPNRouter-v{version}-android-arm64.apk со страницы Releases
```
Android 6.0+ (API 23), ARM64. Установка APK вне Play Store. Поддерживаются сканирование QR, вставка подписки и предложение обновления в приложении. Разрешения нужны для VPN, состояния сети, уведомлений, списка приложений, установки APK, камеры и управления питанием; полный список — в [Android manifest](../../../VPNRouter.Android/AndroidManifest.xml).
</td>
</tr>
</table>

Предпочитаете установку вручную? См. [**Ручная установка**](#ручная-установка) ниже для ZIP / DMG / AppImage / deb / tar.gz.

## Ручная установка

Команды установки desktop-версий и инструкции для Android APK приведены в разделе [Установка](#установка). Последняя стабильная сборка доступна в [Releases](https://github.com/PavelLizunov/VPNRouter/releases/latest), rolling-кандидаты — в [общем списке релизов](https://github.com/PavelLizunov/VPNRouter/releases).

Теги опубликованных версий сохраняются как постоянные ссылки на исходники; файлы старых кандидатов могут удаляться отдельно. Правила сохранения и служебные исключения описаны в [политике хранения тегов и релизов](../../../docs/tag-retention-policy.md).

| Файл | Платформа | Что это |
|---|---|---|
| `VPNRouter-v{version}-win.zip` | 🪟 Windows | Полный установщик (первая установка) |
| `VPNRouter-update-v{version}-win.zip` | 🪟 Windows | Обновление только DLL (если уже на свежей версии) |
| `VPNRouter-Setup-v{version}.exe` | 🪟 Windows | Мастер установки (Inno Setup, без цифровой подписи): ставит те же файлы, что и install-zip, с деинсталлятором и необязательными галочками автозапуска и службы |
| `VPNRouter-*-win.zip.sha256` | 🪟 Windows | Компаньон-файл SHA256 — автоапдейтер проверяет хеш перед распаковкой (v2.15.8+) |
| `VPNRouter-v{version}-mac.dmg` | 🍎 macOS | Drag-install DMG (Apple Silicon) с `InstallGuide.html` для одноразовой настройки sudoers |
| `VPNRouter-v{version}-mac.zip` | 🍎 macOS | Сырой `.app`-бандл (для ручной установки) |
| `VPNRouter-v{version}-linux-amd64.deb` | 🐧 Linux | Пакет для Debian/Ubuntu (desktop entry + `setcap` для passwordless TUN; systemd-сервиса нет). Установка: `sudo dpkg -i <file>.deb` |
| `VPNRouter-v{version}-linux-x86_64.AppImage` | 🐧 Linux | Портативный single-file билд. `chmod +x`, запуск, установка не нужна |
| `VPNRouter-v{version}-linux.tar.gz` | 🐧 Linux | Сырой tarball (для ручной установки или упаковки в другие форматы) |
| `VPNRouter-v{version}-android-arm64.apk` | 🤖 Android | Подписанный ARM64 APK, API 23+. Собирается и подписывается в `build-android.yml` для каждого release-тега, затем публикуется в Releases и на [`vpn.ninitux.com/android`](https://vpn.ninitux.com/android). In-app апдейтер доставляет будущие APK. |
| `*.sha256` для каждого бинарника | All | SHA256-сайдкары рядом с каждым артефактом (Windows `*-win.zip` + `*-update-win.zip` + `*-Setup-*.exe`, macOS `*-mac.dmg` + `*-mac.zip`, Linux `*.deb` + `*.AppImage` + `*.tar.gz`). Авто-апдейтер + CI integrity check проверяют hash перед распаковкой. Сравните результат `sha256sum <file>` на Linux, `shasum -a 256 <file>` на macOS или `Get-FileHash -Algorithm SHA256 <file>` на Windows с 64-символьным хешем из сайдкара. Часть сайдкаров содержит только хеш и не подходит для прямого вызова `sha256sum -c`. |

При успешном выполнении серверное задание публикует отдельный артефакт:

| Файл | Что это |
|---|---|
| [`free-pool-latest/pool.json`](https://github.com/PavelLizunov/VPNRouter/releases/tag/free-pool-latest) | Публичные VLESS-конфигурации и GeoIP-метаданные; размер пула меняется. Потребляется вкладкой Free Configs. |

Запускать `VPNRouter.App.exe` от имени Администратора на Windows (нужно для TUN-адаптера + ETW мониторинга процессов + Firewall-правил). На macOS следуйте инструкции `InstallGuide.html` внутри DMG для одноразовой настройки sudoers, чтобы TUN поднимался без ввода пароля каждый раз. На Linux `.deb` применяет `setcap cap_net_admin,cap_net_bind_service` к встроенному sing-box, чтобы TUN поднимался без root и без пароля (systemd-сервис не ставится); AppImage без песочницы использует системный `pkexec` с запросом пароля. AppImage, обёрнутый в bubblewrap или user namespace (включая NixOS `appimageTools.wrapType2`), не может получить право создать системный TUN-интерфейс, даже если `getcap` показывает capability файла. Используйте нативный пакет дистрибутива вне этой песочницы.

## Требования

- **Windows 10/11 x64** — права Администратора (TUN, firewall, ETW)
- **macOS 12+** — Apple Silicon (arm64). Intel пока не собирается. Нужна одноразовая настройка sudoers при первом запуске (с подсказкой)
- **Linux x86_64** — ядро 5.6+ (TUN/wireguard), `glibc` 2.31+. Протестировано на Ubuntu 22.04 / 24.04 и Debian 12. `nftables` (`nft`) для kill switch.
- **Android 6.0+** (API 23+), ARM64 — использует `VpnService`, root не требуется. Камера запрашивается только для сканирования QR-кода.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — включён в установщик
- Сервер VLESS+Reality, или используйте вкладку Free Configs с публичными серверами
