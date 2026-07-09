# Research goal (Fable): бесшовное авто-обновление + code-signing для VPNRouter (Windows desktop)

Дата: 2026-07-09
Автор задачи: Pavel (через Fable-сессию 2026-07-09)
Режим исполнителя: **личный research, БЕЗ субагентов**. Работай сам, копай широко и
глубоко, приводи источники, не доверяй памяти по версиям/API — сверяйся с актуальной
документацией. На выходе — сравнительный анализ + рекомендация + поэтапный план, НЕ
имплементация кода (это следующий шаг после одобрения).

---

## 0. Что это за задача (в двух строках)

Пользователи (в т.ч. на канале `experimental`) видят, что «VPNRouter пропадает после
перезагрузки». Root cause НАЙДЕН (см. §3): это окно применения авто-обновления — приложение
на секунды исчезает из системного трея во время `stop → xcopy → relaunch`. Нужен research:
**как сделать обновление бесшовным (без исчезновения из трея), надёжным (не через хрупкий
helper.cmd/xcopy), корректным по UX (не дёргать сразу на автозапуске после ребута), и как
закрыть смежную проблему — неподписанные бинарники, которые провоцируют ложные срабатывания
антивирусов.**

---

## 1. Проект и стек (контекст, который у тебя не будет по умолчанию)

- **VPNRouter** — process-based split-tunnel VPN-роутер для Windows / macOS / Linux / Android.
  Solo-dev проект (ограниченный бюджет — учитывай стоимость решений).
- Стек desktop: **.NET 8 + Avalonia 12**, движок — **sing-box** (на Windows/Mac/Linux
  бандлится форк **sing-box-lx** для AmneziaWG + XHTTP; ломать его bundling нельзя).
- Windows-инсталляция: **self-contained publish** (много DLL, не single-file) в
  `C:\Program Files\VPNRouter\app\`. Состав: `VPNRouter.App.exe` (Avalonia GUI),
  `VPNRouter.CLI.exe`, `VPNRouter.Service.exe` (опциональная Windows-служба),
  `sing-box.exe` / `sing-box-lx.exe`, `driver/` (kernel-драйвер `mullvad-split-tunnel.sys`
  для true-split), `x86/`, `profiles/`, десятки .NET/Avalonia/Skia DLL.
- Данные/конфиг/логи: `C:\ProgramData\VPNRouter\` (config.yaml, logs, cache, bin/sing-box.exe).
- Автозапуск: запись в реестре `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` →
  `VPNRouter.App.exe` (стабильный путь), опционально Windows Service (`VPNRouter`,
  тип `AUTO_START`), плюс флаги `autostart_vpn` / `autostart_ui` в config.yaml.
- Дистрибуция: GitHub Releases (`PavelLizunov/VPNRouter`) — canonical; Forgejo mirror
  (`ssh://git@10.9.1.1:18222`, через AmneziaWG VPN); one-liner домен
  `vpn.ninitux.com` (CNAME → GitHub Pages), APT-repo, Homebrew tap.
- **RU-специфика (важна для дизайна апдейта!):** значительная часть пользователей — из РФ,
  где GitHub и `vpn.ninitux.com` могут быть заблокированы БЕЗ VPN. Классический chicken-and-egg:
  обновление требует сети, которую даёт VPN, который и обновляется. Любое решение обязано
  быть устойчивым к недоступности основного источника (mirror-first / fallback).

## 2. Текущий механизм обновления (прочитай эти файлы в репо перед research'ем)

- `VPNRouter.App/ViewModels/UpdateNotificationViewModel.cs` — `CheckOnStartupAsync()`
  (фоновая проверка на старте, показывает уведомление — НЕ авто-применяет), `CheckManually()`,
  вызов `_updateSource.ApplyAsync(info, extractedDir)`.
- `VPNRouter.Core/Services/UpdateChecker.cs` — ядро. Генерирует **`helper.cmd`** в `%TEMP%`,
  который: ждёт выхода parent PID (до 30с), `Stop-Service`/kill `VPNRouter.Service` +
  `taskkill sing-box.exe`, затем **`xcopy /S /Y /Q /R /I`** из распакованного апдейта ПОВЕРХ
  `C:\Program Files\VPNRouter\app\`, релончит `VPNRouter.GUI.exe`/`App.exe`, self-delete.
  Пишет `%ProgramData%\VPNRouter\logs\update.log` (stop/xcopy/relaunch + `XCOPY_EXIT`).
- `VPNRouter.Core/Services/UpdateBackup.cs` — pre-update снапшот `app/` → `app.bak/`
  (rollback при mixed-version), удаляется после первого здорового запуска.
- `VPNRouter.App/Program.cs` (Main) + `VPNRouter.App/Services/InstallHealthCheck.cs` —
  на старте сравнивает compile-time `AppVersion` в App.exe vs runtime `AppVersion` в Core.dll;
  при рассинхроне (частичный xcopy) → `UpdateBackup.RestoreSnapshot` → иначе
  `SelfRepair.Run` (запускает web `install.ps1` через `iwr | & $tmp`).
- `packaging/windows/install.ps1` — one-liner инсталлятор (тоже используется SelfRepair'ом):
  `$ErrorActionPreference="Stop"`, download+sha-verify ПЕРЕД wipe, extract, Start-Menu, ARP,
  Defender-exclusion (best-effort). Уже поправлен 2026-07-09, чтобы честно проверять exclusion.
- Каналы обновления: **stable** и **experimental** (последний тянет `-rN` prerelease).
- Артефакты релиза: `VPNRouter-vX-win.zip` (full install ~50 MB) + `VPNRouter-update-vX-win.zip`
  (lite update ~3 MB, только меняющиеся файлы) + `.sha256` сайдкары.
- **История хрупкости (обязательно учесть):** v2.31.7 `helper.cmd` имел CMD-parser баг
  (`%SVC_TRIES%` пре-раскрывался внутри parenthesised-блока → «20 was unexpected at this time»)
  → **100% апгрейдов молча ломались ~7 дней**, поймали только по user-reports. Фикс —
  `setlocal EnableDelayedExpansion` + `!VAR!`. Мораль: batch-helper — фундаментально хрупкий
  слой, который нельзя нормально протестировать в CI.

## 3. Наблюдаемая проблема + доказательства (root cause уже установлен)

Пользователь: «VPNRouter удаляется/пропадает после перезагрузки» (Windows). Разбор диаг-бандла
`VPNRouter-diagnostics-20260709-220152.zip` + Defender-события с машины:

- **Не антивирус (для этого юзера):** только Windows Defender; ноль детектов VPNRouter/sing-box;
  exclusion на `C:\Program Files\VPNRouter` + `C:\ProgramData\VPNRouter` стоит с 07.07; файлы
  на месте. Единственные карантины — посторонний `read-screen.ps1` (не наш).
- **Не самоудаление кодом:** в логах приложения НОЛЬ строк repair/rollback/update-fail; app
  здоров, работает из `C:\Program Files\VPNRouter\app\`.
- **Пропадало «сегодня»** (09.07) — с уже стоящим exclusion и целыми файлами → «пропало и
  вернулось» = переустановка, а не удаление.
- **Причина:** юзер на `experimental`; в этот день вышли `v2.46.1-r1`, затем `-r2`. На каждом
  автозапуске после ребута приложение видело новый `-rN`, применяло апдейт → `helper.cmd`
  останавливал app+службу → `xcopy` → релонч → **иконка исчезала из трея на время apply** →
  возвращалась новой версией. Несколько prerelease за день = несколько «пропаданий», ровно
  когда пользователь включал компьютер.
- Итог: это штатная работа апдейтера, но **UX читается как «программа удаляется»**. Плюс сам
  механизм (batch-helper + in-place xcopy + apply прямо на автозапуске) — хрупкий и «дёрганый».

Смежная проблема (для ДРУГИХ юзеров, не этого): бинарники **UNSIGNED**
(`Get-AuthenticodeSignature` = NotSigned), install.ps1-exclusion молча no-op'ится под
Tamper Protection (вкл. по умолчанию) → реальный AV-карантин у части пользователей возможен.
Настоящий фикс — code-signing (внутренняя задача #132).

## 4. Что именно исследовать (пронумерованные вопросы)

### A. Бесшовное применение обновления (главное)
1. Как индустрия обновляет desktop tray-приложения **без исчезновения из трея / без видимого
   рестарта**? Разбери и сравни: **Squirrel.Windows**, **Velopack** (современный наследник
   Squirrel/Clowd.Squirrel, .NET-first), **ClickOnce**, **MSIX + App Installer /
   `.appinstaller` auto-update**, **WinSparkle/NetSparkle**, **Chromium/Omaha-style updater**,
   **WiX Burn / MSI major-upgrade**, собственный side-by-side.
2. Техники atomic-swap вместо `xcopy` поверх работающих файлов: установка новой версии в
   **соседнюю папку** (`app-<version>/`) + переключение симлинка/junction/`current`-указателя;
   `MoveFileEx` с `MOVEFILE_REPLACE_EXISTING`; rename-based swap; «apply on next launch».
   Что из этого даёт по-настоящему незаметный переход для tray-приложения с фоновой службой?
3. Возможно ли и стоит ли **hot-swap без рестарта GUI** (маловероятно для .NET, но проверь:
   что реально делают Velopack/Squirrel — они рестартуют процесс, но делают это мгновенно и
   «прозрачно»; какой у них UX и как они избегают «дыры» в трее).
4. Как корректно обновлять при **запущенной Windows-службе (AUTO_START)** и **загруженном
   kernel-драйвере** (`mullvad-split-tunnel.sys`)? Порядок остановки, замена .sys, требование
   reboot, избегание блокировок файлов.

### B. UX и когда применять
5. Best practices: НЕ применять/не показывать апдейт **сразу на автозапуске после ребута**.
   Разбери паттерны: «update on quit» (Chrome/VS Code), отложенный баннер, тихий фоновый
   download + apply только при явном действии/при следующем чистом выходе, «staged rollout».
6. Как минимизировать частоту дёрганья для канала prerelease/experimental (throttling,
   «не чаще раза в N часов», «не предлагать если апдейт вышел < X минут назад»).

### C. Надёжность (замена batch-helper)
7. Чем заменить `helper.cmd`/`xcopy` как фундаментально нетестируемый слой: отдельный
   updater-EXE (подписанный, минимальный), транзакционная замена, verify-after-apply,
   auto-rollback. Как это делают Velopack/Squirrel (у них apply в отдельном `Update.exe`).
8. Delta/differential updates (у проекта уже есть lite update ZIP ~3 MB) — как индустрия
   считает и применяет дельты (Squirrel/Velopack delta packages, bsdiff, MSIX block-map).

### D. Code-signing (смежное, но критично для «исчезновения»/AV)
9. Варианты подписи для indie/solo-dev в 2026: **OV vs EV** сертификаты, **Azure Trusted
   Signing** (бывш. Azure Code Signing — дёшево, но требования к «verified org»),
   **SignPath.io** (бесплатный тариф для OSS), **Certum/SSL.com open-source** предложения.
   Стоимость, требования, время на получение. Влияние подписи на: ложные срабатывания AV,
   **SmartScreen reputation** (и как её набирают), MOTW.
10. Есть ли смысл в **reproducible/deterministic build** + публикации хэшей как part-mitigation
    пока нет подписи.

### E. RU-устойчивость источника обновлений
11. Как сделать update-source **mirror-first / с fallback**, чтобы обновление работало, когда
    GitHub/`vpn.ninitux.com` заблокированы (Forgejo mirror через сам VPN; собственный
    CDN/зеркало; проверка доступности источников по очереди; обновление ПОВЕРХ уже поднятого
    туннеля). Разбери chicken-and-egg (обновление требует сети, которую даёт обновляемый VPN).

## 5. Инварианты и ограничения (не нарушать в рекомендациях)

- Стек фиксирован: **.NET 8, Avalonia 12, self-contained**. Кроссплатформенность существует,
  но **фокус research — Windows** (Mac = DMG/Homebrew, Linux = .deb/AppImage/APT имеют свои
  апдейт-пути; можно кратко отметить, но не углубляться).
- Нельзя ломать бандлинг **sing-box-lx** (AWG/XHTTP), Windows Service и kernel-драйвер.
- **Solo-dev, ограниченный бюджет** — стоимость (сертификат, инфраструктура, время
  сопровождения) обязательна в оценке. Предпочтительны решения с низким/нулевым денежным
  порогом и малой поддержкой.
- **Инкрементальность:** отдели «что можно улучшить поверх текущего механизма малой кровью»
  (например, atomic side-by-side swap + apply-on-quit) от «полного переезда» (Velopack/MSIX).
- Никаких emoji в коде/доках (правило проекта). Секреты/креды не светить.
- Учитывай, что часть аудитории технически неискушённа (девушка-тестировщица из этого кейса) —
  UX должен быть тихим и не пугающим.

## 6. Требуемый deliverable (что произвести)

1. **Сравнительная таблица** решений (Squirrel / Velopack / MSIX+AppInstaller / ClickOnce /
   NetSparkle / текущий-custom-улучшенный) по критериям: бесшовность (нет дыры в трее),
   атомарность/надёжность, поддержка delta, совместимость с .NET8+Avalonia+self-contained,
   совместимость с Windows Service + kernel-драйвером, требование/удобство code-signing,
   RU-fallback-источники, стоимость, сложность миграции, сопровождение.
2. **Рекомендация** с явным обоснованием (1 основной путь + запасной), разнесённая на:
   - «Быстрые победы поверх текущего механизма» (что внедрить за 1-2 итерации без переезда:
     напр. side-by-side + rename-swap вместо in-place xcopy; apply-on-quit; троттлинг
     prerelease-баннера; verify-after-apply + auto-rollback).
   - «Стратегический переезд» (если оправдан) — с оценкой рисков и объёма.
3. **Code-signing план**: конкретный провайдер под этот проект (с ценой и требованиями),
   ожидаемый эффект на AV/SmartScreen, шаги внедрения в CI (`build.ps1`/GitHub Actions).
4. **RU-устойчивость**: конкретная схема mirror-first источника обновлений.
5. **Источники** — ссылки на доки/репо/issue/статьи по каждому ключевому утверждению
   (не по памяти; сверяйся с актуальным на 2026).
6. Сохрани результат в `plans/research-seamless-update-findings-2026-07-XX.md`; при желании
   заведи ADR-черновик по выбранному подходу.

## 7. Как верифицировать (методология)

- Читай ИСХОДНИКИ проекта (файлы из §2) прежде чем предлагать — не строй рекомендации на
  догадках о текущем механизме.
- Для индустриальных решений — открывай их актуальные доки/репозитории/issues (версии и API
  меняются; не полагайся на знания по памяти, особенно по Velopack/MSIX/Azure Trusted Signing —
  они активно эволюционируют).
- Каждое «X умеет Y» подкрепляй ссылкой.
- Явно отделяй «проверенный факт» от «гипотезы, требующей проверки».
- Не спаунь субагентов; не переключай модель.

## 8. Связанные материалы в репо

- `plans/OPEN-DEFECTS.md` — раздел про «VPNRouter deleted after reboot» (root cause = AV/update,
  оба кодовых вектора опровергнуты 2026-07-09) + Task #132 (code-signing).
- `packaging/windows/install.ps1`, `packaging/windows/uninstall.ps1`, `packaging/windows/repair.cmd`.
- `VPNRouter.Core/Services/Diagnostics/DiagnosticsExporter.cs` — теперь собирает `update.log` +
  `antivirus-integrity.txt` (используй их формат как источник фактов о рантайме).
- История: `CLAUDE.local.md` (release process, rolling -rN), `plans/vpnrouter-release-strategy.md`.
