# VPNRouter — Linux Port Research

**Date**: 2026-04-20
**Baseline**: v2.20.6 (Stable Latest), Windows + macOS shipped.
**Goal**: понять что нужно для Linux-версии, какие риски, что уже работает,
что нужно написать заново, и составить план релиза.

---

## TL;DR — хорошие новости

**Большая часть кода УЖЕ cross-platform**. Mac-путь в кодовой базе на
самом деле Unix-путь:
- `AppPaths.ResolveDataDir()` — имеет Linux-ветку (`~/.config/vpnrouter/`) ✓
- `PlatformServices.CreateProcessScanner` — `#if !PLATFORM_WINDOWS` →
  `MacProcessScanner`, который под капотом делает `ps -eo pid,ppid,comm` —
  **работает и на Linux** ✓
- `PlatformServices.CreateMonitorFactory` — `MacProcessMonitor` — polling
  через `Process.GetProcesses`, **cross-platform** ✓
- `PlatformServices.CreateFirewallFactory` — `NullFirewallManager` — no-op
  на не-Windows, **подходит** ✓
- `SingBoxManager.LaunchProcess` уже запускает `/usr/bin/sudo sing-box run`
  в macOS-ветке — **тот же путь для Linux** ✓
- Avalonia 11.3 официально поддерживает Linux, Skia backend, DBus tray.

**Требует работы**:
1. **Build script** — `build-linux.sh` (аналог `build-mac.sh`)
2. **Upstream sing-box для Linux** (скачать через GitHub Releases)
3. **UpdateChecker platform suffix** — добавить `-linux`
4. **UpdateChecker ApplyUpdate** — Linux ветка (сейчас есть только Win + Mac)
5. **Distribution format** — AppImage + .tar.gz или .deb?
6. **Privilege elevation** — `pkexec` вместо `sudo` (GUI auth agent)
7. **TrayIcon** — проверить на GNOME/KDE, install instructions
8. **Autostart** — `.desktop` файл в `~/.config/autostart/`
9. **Что выключить на Linux**: Zapret (Windows-only DPI bypass), TgProxy
   (зависит от Python-embeddable который мы грузим только для Windows),
   Windows Service (systemd-аналог — отдельный проект на потом)

---

## Детальный audit кода

### Уже работает (cross-platform engine layer)

| Файл | Linux статус | Комментарий |
|---|---|---|
| `VPNRouter.Core/AppPaths.cs` | ✅ | Linux-ветка на `~/.config/vpnrouter/` уже есть |
| `VPNRouter.Core/Platform/macOS/MacProcessScanner.cs` | ✅ | `ps -eo pid,ppid,comm` работает |
| `VPNRouter.Core/Platform/macOS/MacProcessMonitor.cs` | ✅ | polling через Process |
| `VPNRouter.Core/Platform/macOS/NullFirewallManager.cs` | ✅ | no-op |
| `VPNRouter.Core/Services/SingBoxManager.cs` | ✅ | sudo-путь для не-Windows |
| `VPNRouter.Core/Services/ConfigGenerator.cs` | ⚠️ | `utun99` для macOS; Linux нужно `tun0`/`vpnrouter0` |
| `VPNRouter.Core/Services/DnsFlusher.cs` | ⚠️ | Есть Win + Mac. Linux нужно добавить (systemd-resolved: `resolvectl flush-caches`) |
| `VPNRouter.Core/Services/HostsManager.cs` | ✅ | `/etc/hosts` и на Linux тот же путь |

### Надо выключить или адаптировать

| Компонент | Действие на Linux |
|---|---|
| **Zapret DPI bypass** | Windows-only (winws.exe — Cygwin). Linux-пути: nftables с прямой DPI манипуляцией, или форк подобного проекта. В этом релизе — **выключить UI**. Если нужно — отдельный проект v2.22.x. |
| **TgProxy (Telegram)** | Зависит от Python embeddable, который мы тянем из `python.org/ftp/python/.../embed.zip` — Windows-only. На Linux `python3` системный, но процесс-аргументы те же. В этом релизе — **выключить UI**. |
| **Windows Service** | systemd-эквивалент через user-unit. В первом Linux-релизе — **без службы**: пользователь запускает app вручную или через .desktop autostart. |
| **ETW ProcessMonitor** | `MacProcessMonitor` (polling) уже используется для не-Windows. ✓ |
| **WindowsDnsHardening** | `#if PLATFORM_WINDOWS` guard — уже скрыта ✓ |

### Надо написать

| Компонент | Что |
|---|---|
| `build-linux.sh` | dotnet publish -r linux-x64 → AppImage или .tar.gz |
| `UpdateChecker.PlatformSuffix` | добавить Linux: `-linux` или `-linux-x64` |
| `UpdateChecker.ApplyUpdateLinux` | ditto-like путь (rsync/cp + chmod +x) |
| `Icon .desktop` | freedesktop.org launcher для menu entry |
| `Permissions policy` | Запуск VPN: либо через `pkexec` (GUI auth), либо `setcap cap_net_admin+ep` на sing-box binary при первом запуске |
| `Linux autostart` | `.desktop` в `~/.config/autostart/` |

---

## Elevation strategy на Linux

### Вариант A — pkexec (предпочтительный)

[pkexec](https://gist.github.com/sstavar/d273b6e4a8323b045c2f5b2c95b45c21) —
часть PolicyKit, диалог ввода пароля через GUI auth-agent (GNOME/KDE/LXQt
ставят его по умолчанию). Работает так же как macOS-sudo — одноразовая
auth, затем sing-box запущен как root.

```bash
pkexec /opt/vpnrouter/sing-box run -c /home/user/.config/vpnrouter/config/current.json
```

Плюсы: стандартно, UX совпадает с macOS.
Минусы: на headless/server Linux нет polkit-agent'а — на desktop
окружениях всё есть.

### Вариант B — setcap (пассивный)

При первом запуске сделать:
```bash
sudo setcap 'cap_net_admin,cap_net_bind_service=+eip' /opt/vpnrouter/sing-box
```

Потом sing-box запускается обычным пользователем без elevation каждый раз.
[Baeldung объясняет подробно](https://www.baeldung.com/linux/set-modify-capability-permissions).

Плюсы: один разовый `sudo`, дальше без пароля.
Минусы: требует `setcap` утилиты (не во всех distro по дефолту), и
обновления sing-box (auto-update) должны re-apply capability после
перезаписи файла.

### Вариант C — systemd user service + ambient capabilities

Самый чистый, но отдельный проект. [Arch wiki подтверждает](https://bbs.archlinux.org/viewtopic.php?id=283485):
```ini
[Service]
AmbientCapabilities=CAP_NET_ADMIN
CapabilityBoundingSet=CAP_NET_ADMIN
DeviceAllow=/dev/net/tun
```

Использовать на v2.22+. В первом Linux-релизе — pkexec.

### Рекомендация: **pkexec в v2.21.0**, setcap как fallback для power-users.

---

## Distribution format

### AppImage — рекомендуется

Один исполняемый файл, runs anywhere, self-contained. Пользователь:
```bash
chmod +x VPNRouter-v2.21.0.AppImage
./VPNRouter-v2.21.0.AppImage
```

Плюсы: универсально (Ubuntu, Debian, Fedora, Arch), не требует
установки, auto-updater может просто перезаписать файл.

Минусы: не интегрируется в меню приложений без доп. шагов (нужен
`.desktop` и AppImage menu integration), иконка не появляется без
ручной регистрации.

### .deb — второй вариант

[Avalonia docs](https://docs.avaloniaui.net/docs/deployment/debian-ubuntu)
приводят рецепт:
- `/usr/lib/vpnrouter/` — бинарь + asset'ы
- `/usr/bin/vpnrouter` — launcher (bash stub)
- `/usr/share/applications/vpnrouter.desktop` — menu entry
- `/usr/share/pixmaps/vpnrouter.png` — иконка
- Dependencies: `libx11-6, libice6, libsm6, libfontconfig1`

Плюсы: стандартная установка через `apt install ./vpnrouter.deb`, меню +
иконка автоматически, auto-updater через apt или наш свой.

Минусы: Ubuntu/Debian only (для Fedora/Arch нужно .rpm и PKGBUILD).

### Рекомендация для v2.21.0: **AppImage** (universal) + `.tar.gz` (power users).  
`.deb`/`.rpm` — в v2.22+ если будет спрос.

---

## Tray icon на Linux

Avalonia 11.3 [TrayIcon](https://docs.avaloniaui.net/controls/navigation/trayicon)
поддерживает Linux через DBus StatusNotifierItem + libayatana-appindicator
fallback. Что работает:
- **KDE Plasma** — работает из коробки ✓
- **GNOME** — требует extension [AppIndicator and KStatusNotifierItem Support](https://extensions.gnome.org/extension/615/appindicator-support/) ⚠️
- **XFCE / MATE / Cinnamon** — работает через встроенный indicator plugin ✓

Мы показываем в release notes что GNOME-пользователям надо поставить
extension. Или добавить проверку в app startup + toast "Install AppIndicator extension to see tray".

---

## sing-box Linux binary

SagerNet публикует [release assets](https://github.com/SagerNet/sing-box/releases)
для Linux x64/arm64. Мы используем same flow как в Windows — скачиваем
готовый бинарь при первом запуске через наш `ZapretUpdater`/auto-download
механизм (или включаем в install-архив).

Размер: ~25 MB. Включить в AppImage — норм. Включить в update archive —
тоже норм (download один раз, потом лежит в `~/.config/vpnrouter/bin/`).

Теги сборки нужны те же: `with_utls,with_clash_api,with_quic`.

---

## Что выключить в UI на Linux

Добавляем новый helper: `OperatingSystem.IsLinux()`.

- **Zapret tab** — скрыть (это Windows-specific DPI bypass через winws.exe)
- **TgProxy tab** — скрыть (Python embeddable — Windows-only)
- **Windows Service управление** — скрыть (у нас нет systemd-unit управления)
- **"Start with Windows" checkbox** в Simple mode → переименовать в
  "Start with session" или "Enable autostart", реализовать через
  `~/.config/autostart/vpnrouter.desktop`

---

## Обновление UpdateChecker

```csharp
private static readonly string PlatformSuffix =
    OperatingSystem.IsMacOS()   ? "-mac"
    : OperatingSystem.IsLinux() ? "-linux"
    :                              "-win";
```

`ApplyUpdateLinux`:
- Extract archive
- Overwrite binaries (pure `File.Copy` на Linux работает для не-запущенных
  файлов; для запущенного `.AppImage` надо приём аналогичный macOS
  ditto — staging + move — если пользователь запустил AppImage напрямую).
- `chmod +x` на новый бинарь (тут File.Copy теряет execute bit как и на
  macOS; наш bash-helper macOS-style или прямой `File.SetUnixFileMode`).
- Перезапуск через `Process.Start("vpnrouter")` если `.deb` установка,
  или `Process.Start("/path/to/VPNRouter.AppImage")` для AppImage.

Детали потом в v2.21.1 — первый релиз обойдётся без auto-update (юзер
скачивает новый AppImage вручную).

---

## План релиза

### v2.21.0 — первая Linux версия (BETA)
Сфокусированный minimal-viable-Linux-release.

**Что включено**:
- `build-linux.sh` генерирует `VPNRouter-v2.21.0-linux.AppImage` +
  `VPNRouter-v2.21.0-linux.tar.gz`.
- sing-box-linux-amd64 включён в archive (+ `chmod +x` при extract'е).
- UpdateChecker знает `-linux` suffix (чтобы auto-updater на mac/win не
  подхватил Linux-assets по ошибке).
- Elevation через `pkexec` в `SingBoxManager.LaunchProcess` Linux-ветке.
- UI-таб'ы Zapret + TgProxy скрыты на Linux.
- Autostart checkbox пишет `.desktop` в `~/.config/autostart/`.
- README-инструкция для GNOME про AppIndicator extension.

**Что НЕ включено**:
- .deb/.rpm пакеты — отдельно.
- Auto-update на Linux (ручное скачивание).
- systemd service / boot autostart.
- Linux-native Zapret-эквивалент.
- Linux-native Telegram proxy.

### v2.21.1 — доработки по фидбеку
- Auto-updater ApplyUpdateLinux.
- .deb рецепт (опционально).
- Если на GNOME tray не видно без extension — детект + warning.

### v2.22.0 — systemd + расширенные features
- systemd user service для autostart-при-загрузке.
- nftables-based Zapret replacement (если есть спрос).

---

## Сложность

| Задача | Оценка |
|---|---|
| `build-linux.sh` + AppImage packaging | 2-3 часа |
| UpdateChecker `-linux` suffix | 15 минут |
| `OperatingSystem.IsLinux()` guards в UI + скрытие Zapret/TgProxy табов | 1 час |
| pkexec launch на Linux в SingBoxManager | 30 минут |
| Autostart через `.desktop` | 1 час |
| README + install instructions | 30 минут |
| Testing на живой Linux VM | 2-3 часа |
| **ИТОГО v2.21.0 BETA** | **7-10 часов** |

Без live Linux VM для тестирования ship-овать рискованно — построим
но сможем только проверить что билдится + что запускается на CI
Linux runner. Реальный smoke test = нужен человек с Linux или
виртуалка.

---

## Рекомендация

Готов собрать v2.21.0 BETA. Вопросы до старта:
1. **У тебя есть Linux VM / машина для smoke-теста**, или шипуем blind?
2. **AppImage или .tar.gz первым?** Я за AppImage — universal.
3. **pkexec элевация или setcap?** Я за pkexec — меньше шагов для
   конечного юзера.
4. **Zapret + TgProxy скрыть или показывать disabled с tooltip'ом**
   "Windows only"? Я за скрыть.

---

## Источники

- [Avalonia Desktop Linux](https://docs.avaloniaui.net/docs/deployment/linux)
- [Avalonia Debian/Ubuntu packaging](https://docs.avaloniaui.net/docs/deployment/debian-ubuntu)
- [Avalonia Linux platform notes](https://docs.avaloniaui.net/xpf/platforms/linux)
- [Avalonia TrayIcon](https://docs.avaloniaui.net/controls/navigation/trayicon)
- [Export Avalonia App to Ubuntu](https://dev.to/chami/export-avalonia-app-to-linux-ubuntu-step-by-step-guide-40id)
- [sing-box installation](https://sing-box.sagernet.org/installation/package-manager/)
- [sing-box TUN config](https://sing-box.sagernet.org/configuration/inbound/tun/)
- [Linux capabilities for TUN (Arch Forum)](https://bbs.archlinux.org/viewtopic.php?id=283485)
- [Linux setcap explained (Baeldung)](https://www.baeldung.com/linux/set-modify-capability-permissions)
- [pkexec self-elevating scripts](https://gist.github.com/sstavar/d273b6e4a8323b045c2f5b2c95b45c21)
- [Capabilities man page](https://man7.org/linux/man-pages/man7/capabilities.7.html)
- [systemd + .NET service example](https://gist.github.com/antoniocampos/2b6f81e923012a17671384b7b35a7ed5)
- [GNOME AppIndicator extension](https://extensions.gnome.org/extension/615/appindicator-support/)
- [Solving tray icon on Arch Linux with Avalonia](https://dev.to/stipecmv/touch001-solving-tray-icon-and-minimalize-ui-problem-on-arch-linux-with-c-in-avalonia-1f2g)
