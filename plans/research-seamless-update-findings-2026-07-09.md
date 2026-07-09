# Findings: бесшовное авто-обновление + code-signing для VPNRouter (Windows)

Дата: 2026-07-09 · Исполнитель: Fable (личный research, без субагентов)
Бриф: `plans/research-seamless-update-and-signing-2026-07-09.md`
Метод: чтение исходников проекта + сверка индустрии по актуальным докам (2026), с источниками.

---

## 0. TL;DR / рекомендация

**Не переезжать на готовый фреймворк — они не подходят под архитектуру VPNRouter.** Все
«красивые» апдейтеры (Velopack, Squirrel) работают ТОЛЬКО в per-user `%LocalAppData%` и
официально **не поддерживают Program Files**; MSIX **невозможен из-за kernel-драйвера**. А у
VPNRouter — Program Files + Windows-служба (LocalSystem) + `mullvad-split-tunnel.sys`.

**Вместо переезда — взять их проверенные ПАТТЕРНЫ и применять существующей LocalSystem-службой:**

1. **Phase 0 (максимальный ROI, малые усилия): подписать бинарники через SignPath.io Foundation**
   — бесплатно, проект GPL-3.0 + public → проходит. Прямо чинит AV-карантин (причину «удаляется
   после ребута» у неподписанных сборок), запускает набор SmartScreen-репутации, делает апдейтер
   доверенным. Azure Trusted Signing — **вероятно недоступен** (гео-ограничение US/Canada).
2. **Phase 1 (UX, малые усилия, в существующем коде): apply-on-quit + не показывать апдейт на
   автозапуске сразу после ребута + троттлинг prerelease-канала** — чинит саму жалобу
   («пропадает после перезагрузки») почти без риска.
3. **Phase 2 (надёжность, средние усилия): заменить `helper.cmd` + in-place `xcopy` на
   atomic side-by-side swap, применяемый LocalSystem-службой** — устраняет фундаментально
   нетестируемый batch-слой и целый класс mixed-version-багов; окно «иконка пропала» → ~0.
4. **Phase 3 (опционально): mirror-first источник (GitHub → Forgejo → own CDN) + delta-обновления.**

Ключ к инкрементальности: механизм уже за интерфейсами `IUpdateSource` / `IDesktopInstaller`
(`GitHubReleaseSource`, `DownloadAndStageAsync` / `ApplyStagedAsync`) — новый apply-слой
вставляется за `IDesktopInstaller` без переписывания UI/check-слоя.

---

## 1. Ядро проблемы: почему off-the-shelf не подходит

VPNRouter — **привилегированное machine-wide** приложение:
- ставится в `C:\Program Files\VPNRouter\app\` (self-contained .NET, десятки DLL);
- несёт Windows-службу `VPNRouter` (`obj= LocalSystem`, `start= auto` — ServiceInstaller.cs:44-51),
  нужную для TUN + firewall;
- несёт kernel-драйвер `mullvad-split-tunnel.sys` (true-split).

Полированные апдейтеры устроены ровно наоборот — они per-user, чтобы избежать UAC:
- **Velopack**: по умолчанию Setup.exe ставит в `%LocalAppData%\{packId}`; в доках прямо —
  «Currently, neither the updater nor the installer support privileged directories such as
  C:\Program Files, but support for this is planned» ([Velopack Installers docs]).
- **Squirrel.Windows**: тоже per-user `%LocalAppData%`, к тому же по сути заброшен (тот же автор
  Caelan Sayler перевёл всё на Velopack).
- **MSIX + App Installer**: «Applications requiring kernel-mode drivers cannot be completely
  packaged as MSIX; drivers must be deployed separately» ([Turbo.net MSIX limitations]); плюс
  sandbox/low-integrity ограничения на сеть и авто-апдейт. Для VPN с драйвером — **исключено**.
- **ClickOnce**: per-user, без нативных зависимостей/служб/драйверов — не подходит.

Вывод: адаптировать паттерны, а не фреймворк.

---

## 2. Что именно «пропадает после ребута» (подтверждено кодом + диагностикой юзера)

Не удаление и не антивирус (для конкретного юзера Defender чист, exclusion стоит, файлы целы).
Это **окно применения апдейта** на канале `experimental`: автозапуск после ребута →
`CheckOnStartupAsync` видит новый `-rN` → apply → `helper.cmd` останавливает app+службу →
`xcopy /S /Y /Q /R /I` поверх install → релонч. Иконка исчезает из трея на время apply. Несколько
prerelease за день = несколько «пропаданий», ровно когда включаешь компьютер.

Дополнительно `helper.cmd` — **фундаментально нетестируемый** слой (история v2.31.7: CMD-parser
баг `%SVC_TRIES%` в parenthesised-блоке сломал 100% апгрейдов на ~7 дней).

---

## 3. Как индустрия делает бесшовный apply (паттерны, которые берём)

- **Versioned side-by-side + stub launcher** (Squirrel): структура `app-<version>\` рядом; крошечный
  неизменный `StubExecutable.exe` сканирует `app-*` и запускает наивысшую версию. Апдейт ставит
  НОВУЮ версию в соседнюю папку, пока старая работает.
- **`current`-указатель + atomic swap** (Velopack): хранит приложение в `{root}\current\App.exe`;
  апдейт распаковывает рядом и переключает `current`. «On Windows, if any of the files inside
  current are in-use, the folder can not be moved/renamed/deleted» → Velopack убивает процессы,
  держащие папку, и переключает ([Velopack From-Squirrel docs], [velopack#4]).
- **apply-and-restart ~2s, без UAC** (Velopack): `ApplyUpdatesAndRestart` выходит, ставит апдейт,
  релончит за ~2с без UAC — «бесшовность» = не «без рестарта», а «мгновенный тихий рестарт»
  ([velopack.io], [Velopack UpdateManager docs]).
- **Delta через Zstd binary patches** (Velopack): full 63MB → delta 132KB; несколько дельт
  применяются последовательно ([Velopack Delta docs]).

**Адаптация под VPNRouter (machine-wide, службой):**
- Layout: `C:\Program Files\VPNRouter\app-<version>\...` + стабильный stub-launcher
  (`VPNRouter.exe` в корне, никогда не меняется) ИЛИ junction `...\current` → `app-<version>`.
  Автозапуск (HKCU Run) и ярлыки указывают на стабильный stub/`current`, а не на версионный путь.
- Apply: download → verify(sha) → extract в `app-<new>` (side-by-side, старая версия работает) →
  **LocalSystem-служба переключает stub/junction на `app-<new>`** (атомарно, как SYSTEM, без UAC) →
  apply-on-quit ИЛИ мгновенный релонч → `app-<old>` удаляется после первого здорового запуска.
- Никакого in-place перезатирания работающих файлов → нет частичного xcopy, нет mixed-version.
- Rollback = переключить stub/junction обратно на `app-<old>` (держим до healthy launch) — заменяет
  текущий `UpdateBackup` app.bak механизм на более чистый.
- Открытый вопрос для мини-spike (1 день): переключать junction `current` можно и при запущенном
  exe (reparse-point на ИМЕНИ `current` не залочен работающим `app-<old>\App.exe`); подтвердить на
  целевых Windows 10/11 перед имплементацией. Fallback — folder-rename с краткой остановкой app.

Почему службой: служба УЖЕ LocalSystem + AUTO_START → пишет Program Files и переключает указатель
без per-update UAC. Это ровно то, чего Velopack не умеет (per-user), а у нас есть из коробки.

---

## 4. Code-signing (смежное, но это и есть настоящий фикс AV-удаления)

| Вариант | Тип | Цена | Доступность RU/solo | Вердикт |
|---|---|---|---|---|
| **SignPath.io Foundation** | OV (ключ на их HSM) | **бесплатно (OSS)** | да (Foundation в ЕС; критерий — OSS-проект) | **рекомендую** |
| Azure Artifact/Trusted Signing | OV/публичный траст | ~$120/год | **нет для individual вне US/Canada** ([techcommunity]) | заблокирован |
| Коммерческий OV (SSL.com/Certum) | OV | ~$200-500/год | да | fallback если SignPath откажет |
| EV-сертификат | EV | дороже + HSM | да | **не нужен**: с 2024 EV больше не даёт мгновенную SmartScreen-репутацию ([DigiCert], [MS Q&A]) |

- VPNRouter **проходит** на SignPath Foundation: репозиторий PUBLIC, лицензия **GPL-3.0**, есть
  релизы, сборка из исходников верифицируема (билд в CI) ([SignPath OSS], [signpath.org/terms]).
  Требования: публичный код, признанная OSS-лицензия, ручное одобрение каждого релиза, сборка
  верифицируемо из исходников.
- **Важный нюанс про SmartScreen (не переоценивать подпись):** подпись **не** даёт мгновенно
  чистый SmartScreen. Репутация нового OV-thumbprint набирается неделями-годами по объёму чистых
  загрузок; EV с 2024 тоже не байпасит ([MS Learn SmartScreen], [Sectigo]). НО подпись:
  (1) резко снижает ложные AV-карантины (та самая «пропажа после ребута» у неподписанных сборок),
  (2) обязательна как предпосылка набора репутации, (3) делает апдейтер/службу доверенными.
- **Нюанс про sing-box:** SignPath подписывает артефакты, собранные из исходников верифицируемо.
  Наши EXE (App/CLI/Service) — да. `sing-box-lx.exe` собирается в нашем CI из запиненного форка
  (`tools/build-singbox-lx.ps1`) → тоже можно. Upstream `sing-box.exe` (скачиваемый) может не
  подойти под «built-from-source» — тогда либо собирать его в CI, либо оставить неподписанным
  (но неподписанный sing-box.exe в подписанном бандле всё ещё может триггерить AV → лучше собрать).
- Внедрение: подпись в CI (`build.ps1` / GitHub Actions) через SignPath-connector; подписывать
  App/CLI/Service/(lx), инсталлятор и update-payload'ы; ручное approve на релиз — вписывается в наш
  rolling-rN процесс.

---

## 5. UX-паттерны (Phase 1 — чинит саму жалобу малой кровью)

- **Не применять/не показывать апдейт на автозапуске сразу после ребута.** Это точный триггер
  жалобы. Отложить проверку/баннер на N секунд после старта ИЛИ вообще до момента, когда юзер сам
  открывает окно.
- **apply-on-quit по умолчанию** для не-критичных апдейтов (как Chrome/VS Code): скачать в фоне,
  применить при следующем чистом выходе → нет «дыры» в трее во время работы.
- **Троттлинг prerelease:** не переспрашивать чаще раза в N часов; не предлагать апдейт, вышедший
  <X минут назад (защита от дёрганья в момент нашего rolling-rN, как сегодня r1→r2).
- Всё это — правки в `UpdateNotificationViewModel.CheckOnStartupAsync` + новое поле политики; риск
  низкий, характеризационный хэш MVM может сдвинуться (перепинить).
- **Немедленный воркэраунд для текущего юзера:** перевести её канал на `stable` (Настройки →
  Обновления) — перестанет ловить наши `-rN`.

---

## 6. RU-устойчивость источника (Phase 3)

Chicken-and-egg: обновление требует сети, которую даёт обновляемый VPN; GitHub/`vpn.ninitux.com`
в РФ могут быть заблокированы. Решение — **mirror-first `IUpdateSource`**:
- порядок: GitHub (canonical) → **Forgejo mirror** (достижим через сам поднятый туннель) → own
  CDN/Pages-зеркало;
- проверять/качать апдейт **после** поднятия туннеля, а не на голом старте до VPN;
- абстракция `IUpdateSource` уже позволяет добавить composite-source с fallback без ломки check-слоя.

---

## 7. Сравнительная таблица

| Критерий | Velopack | Squirrel.Win | MSIX+AppInstaller | ClickOnce | NetSparkle | **Custom + служба (рек.)** |
|---|---|---|---|---|---|---|
| Бесшовность (нет дыры в трее) | да (per-user) | да (per-user) | частично | нет | нет (свой installer) | **да (junction swap + apply-on-quit)** |
| Атомарность/rollback | да | да | да | слабо | нет | **да (side-by-side + junction)** |
| Delta | да (Zstd) | да | block-map | нет | нет | опц. (есть lite ZIP) |
| .NET8 + Avalonia + self-contained | да | да | да | плохо | да | **да** |
| **Program Files + служба + kernel-драйвер** | **нет** | **нет** | **нет (драйвер)** | нет | н/п | **да (служба=SYSTEM)** |
| Подпись | сам | сам | обяз. | сам | сам | **SignPath** |
| RU-fallback источник | кастом | кастом | нет | нет | appcast | **да (mirror-first)** |
| Цена | 0 | 0 | 0 | 0 | 0 | **0** |
| Миграция | высокая (ломает machine-wide) | высокая | невозможна | — | низкая | **средняя (за IDesktopInstaller)** |
| Сопровождение | низкое | заброшен | среднее | плохое | низкое | среднее |

Вывод: единственный столбец без «нет» в строке machine-wide+служба+драйвер — **Custom + служба**.
NetSparkle решает только check/notify (у нас уже есть через `IUpdateSource`) — низкая ценность.

---

## 8. Поэтапный план (усилия / эффект)

- **Phase 0 — SignPath Foundation signing.** Усилия: S (заявка + CI-интеграция). Эффект: чинит
  AV-удаление у будущих юзеров, запускает репутацию, доверенный апдейтер. Зависимость: одобрение
  SignPath (ручное). Риск: sing-box подпись (см. §4).
- **Phase 1 — UX (apply-on-quit + no-prompt-on-autostart + prerelease throttle).** Усилия: S.
  Эффект: чинит саму жалобу; немедленно + перевод юзера на stable как воркэраунд.
- **Phase 2 — service-applied atomic side-by-side swap.** Усилия: M (spike на junction-swap →
  layout `app-<ver>`/stub → апплай службой → verify+rollback → миграция со старого layout). Эффект:
  убирает `helper.cmd`/xcopy и mixed-version класс багов; окно «пропала иконка» → ~0.
- **Phase 3 — mirror-first source + (опц.) Zstd delta.** Усилия: M. Эффект: RU-устойчивость,
  меньше трафика.

Общая стоимость деньгами: **$0** (SignPath бесплатно, всё остальное — код). Основные затраты —
время + ручное approve релизов в SignPath.

---

## 9. Открытые вопросы для проверки перед имплементацией

1. Junction-repoint `current` при запущенном exe — подтвердить на Win10/11 (мини-spike); иначе
   folder-rename с краткой остановкой.
2. SignPath: пройдёт ли одобрение (GPL-3.0 + public — критериям соответствует, но approve ручной);
   как подписывать бандлируемый sing-box(-lx) (собирать в CI vs оставить неподписанным).
3. Совместимость atomic-swap с обновлением `.sys`-драйвера (драйвер обычно требует reboot; апдейт
   без смены драйвера — не трогать `.sys`; смена драйвера — отдельный флаг «требуется перезагрузка»).
4. Миграция существующих инсталляций со «плоского» `app\` на `app-<ver>\`+stub — one-shot мигратор
   в первом релизе с новым layout.

---

## Источники

- [Velopack](https://velopack.io/) · [Velopack GitHub](https://github.com/velopack/velopack) ·
  [Velopack Installers](https://docs.velopack.io/packaging/installer) ·
  [Velopack Windows](https://docs.velopack.io/packaging/operating-systems/windows) ·
  [Velopack Deltas](https://docs.velopack.io/packaging/deltas) ·
  [Velopack From-Squirrel](https://docs.velopack.io/migrating/squirrel) ·
  [velopack#4 locked-dir](https://github.com/velopack/velopack/issues/4) ·
  [velopack#32 machine-wide](https://github.com/velopack/velopack/issues/32)
- [Squirrel.Windows](https://github.com/Squirrel/Squirrel.Windows) ·
  [Squirrel Update Process (DeepWiki)](https://deepwiki.com/Squirrel/Squirrel.Windows/3.2-the-update-process)
- [MSIX auto-update overview](https://learn.microsoft.com/en-us/windows/msix/app-installer/auto-update-and-repair--overview) ·
  [Turbo.net MSIX limitations](https://www.turbo.net/blog/posts/2025-06-16-understanding-msix-limitations-enterprise-application-compatibility)
- [Azure Trusted/Artifact Signing pricing](https://azure.microsoft.com/en-us/pricing/details/trusted-signing/) ·
  [Trusted Signing for individuals (public preview)](https://techcommunity.microsoft.com/blog/microsoft-security-blog/trusted-signing-is-now-open-for-individual-developers-to-sign-up-in-public-previ/4273554)
- [SignPath for OSS](https://signpath.io/solutions/open-source-community) ·
  [SignPath Foundation terms](https://signpath.org/terms.html) ·
  [OSS Perks SignPath eligibility](https://www.ossperks.com/programs/signpath/check)
- [MS Learn — SmartScreen reputation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation) ·
  [DigiCert — EV no longer bypasses SmartScreen](https://knowledge.digicert.com/alerts/ev-signed-application-showing-microsoft-defender-smartscreen-warnings) ·
  [Sectigo — SmartScreen reputation](https://support.sectigo.com/PS_KnowledgeDetailPageFaq?Id=kA01N000000zFJx)
