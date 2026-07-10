# Ревью зависимостей — 2026-07-09

Полный аудит: NuGet (direct + transitive, 8 проектов), бинарные/нативные
артефакты (sing-box-lx, driver, libbox, zapret, tg-proxy, wgturn, slipstream,
geo, python), CI-пины (actions, Go, dotnet SDK), рантайм-загрузчики.
Инструменты: `dotnet list package --vulnerable --include-transitive /
--outdated / --deprecated` (все green/получены), ручной разбор Android
(локально не рестор-ится из-за global.json 8.0.418), чтение всех updater'ов.

## Вердикт

Экосистема в хорошем состоянии: **0 уязвимых, 0 deprecated** NuGet-пакетов
(включая транзитивные), NuGetAudit включён (mode=all, level=moderate),
Dependabot настроен (weekly, grouped), все GitHub Actions запинены по SHA,
самые опасные артефакты (kernel-драйвер Mullvad, libbox.aar) под жёсткими
sha256-гейтами, self-update верифицируется .sha256-сайдкарами.

Реальные дыры — в **рантайм-загрузчиках стороннего исполняемого кода**
(цепочка TgProxy вообще без верификации, Zapret — только размер) и один
**стратегический дедлайн: .NET 8 EOL 2026-11-10** (~4 месяца).

---

## P1 — стратегическое / исполняемый код без верификации

### P1-1. .NET 8 EOL 2026-11-10 — через 4 месяца
Все desktop-проекты net8.0, `global.json` = SDK 8.0.418, CI = `8.0.x`.
После 10 ноября 2026 — никаких security-патчей рантайма. .NET 10 (LTS) GA
с ноября 2025; Android уже на net10.0-android36.0, Service уже тянет
M.E.Hosting 10.0.8 с nuget.org (т.е. GA-пакеты доступны и работают).
**Действие**: запланировать миграцию TFM net8.0 → net10.0 на осенний цикл
(до EOL). Бонусом закрывается P3-1 (Android NU1102) и разнобой
"8.0-пакеты у Core vs 10.0-пакеты у Service".

### P1-2. TgProxyUpdater: python-embed + PyPI wheels — ноль верификации
`TgProxyUpdater.cs`:
- Качает `python-3.12.7-embed-amd64.zip` с python.org — **без хэша**.
  3.12.7 = октябрь 2024; с тех пор вышло >=4 security-релиза 3.12.x
  (3.12 в security-only фазе, поддержка до окт 2028). Т.е. на машины
  пользователей ставится Python с известными CVE.
- Качает wheels `pycparser` / `cffi` / `cryptography` с PyPI:
  версия = **latest на момент запроса** (не пиновано), digest
  **не проверяется** — хотя тот же самый PyPI JSON (`urls[].digests.sha256`)
  уже содержит sha256 каждого файла. Проверка = ~5 строк.
- Паттерн `cp312` у cffi жёстко связан с PythonVersion — при бампе Python
  на 3.13 нужно синхронно менять паттерн (сейчас это неявно).

**Действие**: (a) бампнуть PythonVersion на актуальный 3.12.x;
(b) читать `digests.sha256` из уже полученного JSON и сверять после
скачивания wheel; (c) для python.org zip — запинить sha256 константой
рядом с PythonVersion (обновляется при бампе версии).
Это исполняемый код, который запускается у пользователя — приоритет выше,
чем любые NuGet-бампы.

## P2 — верификация загрузок (executable под admin)

### P2-1. ZapretUpdater — latest release, проверка только размера
`ZapretUpdater.cs`: качает **последний** релиз `Flowseal/zapret-discord-youtube`
(winws.exe и пр. — запускается с admin-правами), верификация = только size
из GitHub API. Осознанный trade-off (авто-свежие стратегии обхода), но
компрометация апстрим-аккаунта = payload у всех пользователей.
**Действие-минимум**: зафиксировать trust root в `tools/native-deps.md`
(сейчас там одна строка "upstream auto-resolved at runtime" без анализа).
**Опция**: known-good tag pin в релизе VPNRouter + явное подтверждение
пользователем при апгрейде на непроверенный тег (version-check flow уже есть).

### P2-2. WgturnUpdater — механизм sha256 есть, но мёртвый
`WgturnUpdater.cs`: `expectedSha256` протянут через весь путь и проверяется
(строки 391-399), но `ResolveAssetFor` всегда возвращает `null` — "upstream
does not currently publish sidecar checksum files". Апстрим — **наш собственный
репо** (PavelLizunov/wgturn-core), а build-пайплайн VPNRouter уже умеет
генерить `.sha256`-сайдкары.
**Действие**: публиковать `.sha256` в релизах wgturn-core и заполнить
`expectedSha256` — вся клиентская часть уже написана.

### P2-3. Upstream sing-box скачивается без хэша (build-время)
- `build.ps1` (строки 353-371): непинованный по хэшу
  `sing-box-1.13.14-windows-amd64.zip` — только для локальных
  не-Upload сборок (release-путь требует lx-core, собранный из пинованных
  коммитов, — там ок).
- `build-linux.yml` (строка ~108): `libcronet.so` вытаскивается из upstream
  tar.gz **в release-пайплайне** — тоже без sha256.

Контраст: mullvad-драйвер в том же build.ps1 — образцовый (commit-pin +
жёсткий sha256-гейт + checksums-сайдкар). **Действие**: одна pinned-hash
константа на версию sing-box в обоих местах (~3 строки каждое); бампается
вместе с версией.

## P3 — гигиена пинов / дрейф

### P3-1. Android NuGet.config костыль (NU1102) устарел — убрать
`VPNRouter.Android/NuGet.config` добавляет фиды `dotnet-experimental` +
`dotnet-public` поверх nuget.org. Проверено сегодня:
`Microsoft.NETCore.App.Runtime.Mono.android-arm64` **10.0.0–10.0.9 лежат
GA на nuget.org**, CI уже на SDK 10.0.301 (GA). Сам файл говорит "remove
when SDK 10.0.x ships GA".
**Действие**: удалить экстра-фиды (оставить чистый nuget.org), прогнать
Android-рестор (Mac-хост или CI). Эффект: (a) уходит
dependency-confusion поверхность (мульти-фид без packageSourceMapping);
(b) Android возвращается в Dependabot (dependabot.yml сам просит:
"Re-add the Android project here once it moves to a GA .NET");
(c) `dotnet list package` снова видит Android.

### P3-2. Android-зеркало пакетов Core разъехалось
`VPNRouter.Android.csproj` объявляет "Mirror what's declared in
VPNRouter.Core.csproj", но:

| Пакет | Core | Android mirror |
|---|---|---|
| Serilog | 4.0.0 | 3.1.1 |
| Serilog.Sinks.Console | 6.1.1 | 5.0.0 |
| YamlDotNet | 15.3.0 | 15.1.2 |
| Avalonia (vs App 12.0.4) | — | 12.0.3 |

Компилируется, но: Android собирает **исходники Core** против других
версий. Любое использование API Serilog 4.x / YamlDotNet 15.3 в Core
пройдёт desktop-CI и молча сломает Android-сборку (которая CI-ограничена).
**Действие**: выровнять 4 пина под Core/App. Дешёвая правка, страхует
от невидимого класса поломок.

### P3-3. tools/native-deps.md — инвентарь сильно устарел
Файл — заявленный "inventory + bump procedure" нативных зависимостей, но:
- sing-box указан "upstream **1.13.10** (desktop)" — реально desktop
  релизится с **sing-box-lx** (база 1.13.13, пины c7a2592e/0c0c10b5 +
  3 build-патча), а upstream-пин в build.ps1 = **1.13.14**;
- отсутствуют вообще: mullvad split-tunnel driver (единственный, у кого
  образцовый гейт!), slipstream-client, wgturn-cli, libcronet, geo
  .srs (GeoDataDownloader), python-embed + 3 PyPI wheels.
**Действие**: переписать таблицу инвентаря по фактическому состоянию
(источник: build.ps1 [6/9]-[6c/9], build-singbox-lx.ps1, updater'ы).

### P3-4. Vecc.YamlDotNet.Analyzers.StaticGenerator 15.1.2 vs YamlDotNet 15.3.0
Комментарий в Core.csproj утверждает "pinned to match the main YamlDotNet
15.1.2 pin above" — но main-пин уже 15.3.0. Работает, однако генератор и
рантайм из разных патч-веток. При следующем YamlDotNet-бампе двигать парой
(у обоих есть 18.1.0).

## P4 — мелочь CI

- `test-windows-update.yml`: Go **1.21** (EOL, нет патчей тулчейна) для
  сборки GUI-стаба; bump до "1.25" как в build-mac/linux.
- `setup-go` запинен на **два разных SHA** (40f1582b в build-mac/linux vs
  4a360112/v6.4.0 в test-windows-update) — унифицировать на новый.

## Outdated NuGet (не срочно, 0 CVE)

Скан `--outdated` по desktop-проектам:

| Пакет | Сейчас | Latest | Примечание |
|---|---|---|---|
| Serilog (Core) | 4.0.0 | 4.3.1 | CLI/Service уже на 4.3.0 — унифицировать |
| Serilog.Sinks.File | 5.0.0 | 7.0.0 | 2 мажора; смотреть breaking notes |
| YamlDotNet (+Vecc) | 15.3.0/15.1.2 | 18.1.0 | 3 мажора; двигать парой, есть StaticContext-генератор |
| Spectre.Console / .Cli | 0.49.1 | 0.57.2 / 0.55.0 | линии версий у пары разошлись апстримом |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.2.2 | 3.2.4 | патчи |
| Microsoft.Win32.SystemEvents, System.Management | 8.0.0 | 10.0.9 | осознанно держать 8.x до миграции на .NET 10 |
| Microsoft.Extensions.Hosting(+WindowsServices) | 10.0.8 | 10.0.9 | патч |
| Avalonia (5 пакетов App + 2 Headless в Tests) | 12.0.4 | 12.1.0 | минор; бампать все 7 синхронно + Android 12.0.3 туда же |
| AvaloniaUI.DiagnosticsSupport | 2.2.2 | 2.2.3 | Debug-only, не шипится в Release ✓ |
| SkiaSharp | 3.119.4 | 4.150.0 | **НЕ бампать отдельно** — Avalonia 12.x пинит 3.x (NU1605) |
| Microsoft.NET.Test.Sdk / coverlet.collector | 17.14.1 / 6.0.4 | 18.7.0 / 10.0.1 | test-only |
| xunit.v3 3.2.2 + runner 3.1.5 | — | — | актуальны ✓ |

Рекомендация: один batch-коммит "minor/patch bumps" (Serilog 4.3.1,
TraceEvent 3.2.4, M.E.Hosting 10.0.9, Avalonia 12.1.0 x8) отдельным -rN
с полным прогоном; мажоры (YamlDotNet 18, Sinks.File 7, Spectre 0.5x) —
по одному и только при поводе. Dependabot и так предлагает weekly.

## Статус-таблица бинарных зависимостей

| Компонент | Пин | Верификация | Статус |
|---|---|---|---|
| sing-box-lx (Win/Mac/Linux release) | коммиты c7a2592e + 0c0c10b5, база 1.13.13 | git commit-id + build из исходников | ✓ |
| mullvad-split-tunnel driver | commit cc0affb2 + sha256 на 3 файла | жёсткий гейт, fail build | ✓ эталон |
| libbox.aar (Android) | tag tooling-libbox-singbox-1.13.10 + sha256 | жёсткий гейт в CI | ✓ (ядро 1.13.10 — rotation deferred, известно) |
| upstream sing-box 1.13.14 (dev-builds, libcronet) | версия | нет хэша | P2-3 |
| slipstream-client | локальная сборка из исходников | bundled, не качается | ✓ (пина апстрима нет — задокументировано) |
| VPNRouter self-update | GitHub Releases | .sha256 сайдкар, проверяется | ✓ (best-effort: без сайдкара — только size) |
| Zapret (winws) | latest release | только размер | P2-1 |
| tg-ws-proxy + Python + wheels | latest / 3.12.7 | нет | P1-2 |
| wgturn-cli | latest release | код есть, hash всегда null | P2-2 |
| geo .srs (SagerNet rule-set) | mutable branch | нет (data-only, не исполняется) | приемлемо |

## Что уже хорошо (не трогать)

- NuGetAudit в Directory.Build.props: mode=all, level=moderate.
- Dependabot: nuget по 5 директориям + github-actions, weekly, grouped
  minor/patch, мажоры отдельными PR.
- Все workflow-actions запинены по commit SHA (не по мутабельным тегам).
- CodeQL-workflow активен.
- Serilog.Sinks.Console в Core — не мёртвый груз: App и PoolAggregator
  пишут в консоль через транзитивную ссылку.
- AvaloniaUI.DiagnosticsSupport вырезается из Release через
  IncludeAssets=None.

## Кандидат на удаление

- `Serilog.Extensions.Logging` 10.0.0 в VPNRouter.CLI.csproj — ни одного
  использования M.E.Logging/AddSerilog в CLI-коде (grep чистый); к тому же
  транзитивно приезжает из Service (Serilog.Extensions.Hosting). Удалить,
  собрать, подтвердить.

## Сводный план действий (по убыванию ценности)

1. [P1-2] TgProxy: bump Python 3.12.x + sha256 для zip и wheels из PyPI digests.
2. [P2-2] wgturn-core: публиковать .sha256 сайдкары, заполнить expectedSha256.
3. [P3-1] Убрать Azure-фиды из Android NuGet.config, вернуть Android в Dependabot.
4. [P3-2] Выровнять Android-зеркало пакетов (Serilog 4.x, Console 6.1.1, YamlDotNet 15.3.0, Avalonia 12.0.4).
5. [P2-3] Pinned sha256 для upstream sing-box zip (build.ps1) + libcronet tar.gz (build-linux.yml).
6. [P3-3] Переписать tools/native-deps.md по фактическому инвентарю.
7. [Cleanup] Удалить Serilog.Extensions.Logging из CLI; batch minor-bump NuGet.
8. [P4] Go 1.21 → 1.25 + унификация setup-go SHA.
9. [P1-1] Осенью: миграция net8.0 → net10.0 (до EOL 2026-11-10).

Пункты 1-8 — каждый в размере одного маленького коммита, совместимы с
rolling -rN циклом. Пункт 9 — отдельный план.
