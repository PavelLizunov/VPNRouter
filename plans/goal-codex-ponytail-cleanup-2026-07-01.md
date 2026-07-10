# GOAL (Codex): ponytail cleanup — вырезать мёртвый код и незавершённые абстракции

## Триггер
Repo-wide ponytail-аудит (2026-07-01, `PONYTAIL_AUDIT.md` в корне) нашёл ~1 960
строк мёртвого/дублирующего кода + 1 лишнюю зависимость. Ничего из этого не меняет
поведение — это чистые удаления и дедуп. Самый крупный кусок: **целый Free-Configs
`Stages/`-пайплайн (~1 190 строк) собран, покрыт тестами и никуда не подключён** —
UI ходит в `FetchPoolAsync` напрямую. Эта goal сводит находки в исполняемый план,
упорядоченный по риску.

**Природа работы:** удаление, а не добавление. Ни одна фаза не добавляет фич,
не меняет генерацию конфига, не трогает поведение в рантайме. Если удаление
что-то ломает — значит код был НЕ мёртвый; SKIP + доложить, не «чинить».

## Границы / НЕ трогать (жёстко)
- **AWG-ядро (v2.45.0-r8) и `tools/build-singbox-lx.ps1`** (3 патча) — не касаться.
- **Test-seam интерфейсы** (`IProcessRunner`, `IFileSystem`, `ISettingsStore`,
  `IHttpClient`, `IProcessHandle`, `ISingBoxApi`, `IProfileSource`, …) — оставить,
  это осознанные mockable-швы, НЕ YAGNI.
- **sing-box JSON DTO** (`VPNConfig.cs`), YAML/STJ source-gen контексты
  (`AppJsonContext`, `YamlStaticContext`) — wire-контракт, не трогать.
- **Cross-platform / fail-closed** (firewall, kill-switch, DNS-hardening) — load-bearing.
- **Хуки и CI-гейты** (pre-commit/pre-push/commit-msg, `verify-*.ps1`,
  `check-open-p0.ps1`) — методология, оставить.
- **Правило проекта:** `AppVersion.Version` совпадает с тегом; push в оба remote;
  без emoji; каждая фаза — отдельный коммит (bisectable); pre-commit hook держит
  build+tests. Это cleanup-серия — версию бампаем один раз в конце (`-r9`), а не
  за фазу.

## Definition of done (acceptance)
1. `dotnet build VPNRouter.sln -c Release` — 0 errors на каждом фазовом коммите
   (Android: `-p:EnableAndroidTarget=true` там, где менялся Android).
2. Полный `dotnet test` зелёный (минус env-baseline `VpnEngine*Lifecycle`
   ProgramData-locks и минус удалённые вместе со `Stages` 8 тестов).
3. Ни одного изменения поведения: сгенерированный `current.json` для VLESS и AWG
   **байт-идентичен** до/после (зафиксировать golden JSON до Phase 1, сверить после
   каждой фазы). Характеризационный хэш `MainWindowViewModel` не двигается.
4. Каждый `delete:` перед удалением **re-verified grep'ом** по всему дереву
   (App/CLI/Service/Android/Core, ИСКЛЮЧАЯ `VPNRouter.Tests`, `plans/`,
   `.claude/worktrees/`, `bin/`, `obj/`): 0 живых вызовов. Есть вызов — SKIP + лог.
5. `-1` NuGet-зависимость (`ZXing.Net`) — только если Android собирается без неё.
6. Итог: ~-1 960 строк, отчёт по факту (сколько реально вырезано vs предполагалось).

## Фазы

### Phase 0 — golden snapshot (страховка, не удаление)
Сгенерировать и сохранить `current.json` для одного VLESS- и одного AWG-профиля
(через существующий тест-харнес / `ConfigGenerator.Generate`). Это эталон для
DoD #3 — после каждой последующей фазы пересобрать и `diff` = пусто.

### Phase 1 — keystone: удалить мёртвый `FreeConfigs/Stages/` пайплайн (~1 190 строк)
Grep-подтверждено: `IFreeConfigStage`/`Stages` не используются в App/CLI/Service/
Android; `FreeConfigAggregator.RefreshAsync` не имеет прод-вызовов (UI использует
`FetchPoolAsync` + `Cache.Load` + события). Удалить:
- `FreeConfigAggregator.RefreshAsync` + `RunWithRetryAsync` (~145 строк).
- `IFreeConfigStage.cs` целиком (интерфейс + `StageRetryPolicy`/`StageRetry`/
  `StageContext`/`StageResult` — фреймворк «Phase 4 will load from yaml», мёртв).
- `Services/FreeConfigs/Stages/*.cs` (`FetchStage`/`ParseStage`/`GeoIpStage`/…).
- Дубль `ParseStage.BuildId` уходит вместе со `Stages`; каноничная копия остаётся
  в `FreeConfigAggregator.BuildId` — убедиться, что на неё никто не ссылался через
  `ParseStage.BuildId`.
- 8 stage-only тестов, покрывающих ТОЛЬКО удаляемое (проверить: не тестируют
  общий helper; если тест смешанный — вынести живую часть, не удалять её).
- Gate: build 0 errors, полный test-run зелёный (за вычетом 8 удалённых),
  golden `current.json` diff пуст (Free-Configs генерацию это не трогает вообще).

### Phase 2 — grep-verified dead-code deletes (нулевой риск, один коммит на пункт)
Для каждого: re-grep 0 живых вызовов → удалить. Не сгруппировано в один коммит,
чтобы каждый был отдельно откатываем.
- `VPNRouter.Android/QrCodeDecoder.cs` (115 строк, 0 вызовов; live-скан на
  `zxing-android-embedded`). Затем **убрать `ZXing.Net` PackageReference**
  (`VPNRouter.Android.csproj:151`) и собрать Android — если NU-ошибок нет, `-1` dep;
  если зависит что-то ещё — вернуть reference, оставить только удаление кода.
- `VPNRouter.App/Converters.cs:101-135` — `ActionToChipColorConverter` (21 строка,
  0 XAML/`.cs`-ссылок; заменён `ActionToTokenBrushConverter`).
- `VPNRouter.App/SimpleInputDetector.cs:20-22` — `SmpInputKind.Vless` alias (0 живых).
- `VPNRouter.Core/Services/SubscriptionUserInfo.cs:19` — `RemainingBytes` (только тесты).
- `VPNRouter.Android/AppListLoader.cs` — поле `AppEntry.Icon` (пишется, не читается;
  UI биндит `IconBitmap`) → передавать `icon` прямо в `GetOrConvert`.
- `VPNRouter.Android/AppIconCache.cs:124` — `GetCached`/`Clear`/`Count` (0 вызовов).
- `VPNRouter.Android/Controls/StatusCard.cs` — `IsWarn` третье состояние (всегда
  `false`): убрать `_dotWarn`, `IsWarnProperty`, ветку в `SyncDots` → двухсостоянийно.
- `tools/live-test-r1.ps1` (202 строки, хардкод несуществующего worktree-пути, 0 вызовов).
- `tools/build-singbox.ps1` (134 строки, вытеснен inline-загрузкой в `build.ps1` +
  `build-singbox-lx.ps1`; проверить, что `build-linux.yml`/README на него не завязаны
  реально, а не в комментарии-надгробии).
- Gate: build + full tests зелёные; Android-коммиты собирать с Android-таргетом.

### Phase 3 — незавершённые миграции: свернуть shim'ы (нужен rename вызовов)
Выше нуля по риску — трогает ~9 прод-вызовов. Отдельный коммит на пункт.
- **`PlaceholderGuard` → `PlaceholderDefense`**: заголовок файла сам говорит «new
  code should call `PlaceholderDefense` directly; forwarder will be removed».
  Переименовать ~9 прод-вызовов на `PlaceholderDefense`, удалить
  `PlaceholderGuard.cs`. Тесты, гонявшие через shim, перенаправить на прямой тип
  (не удалять покрытие — оно проверяет живую защиту от placeholder-кредов).
- **`PlaceholderDefense.LayerX_*`**: `LayerD.IsPlaceholderEntry` /
  `Layer6.InspectForDeepVerify` — однострочные делегаты в `Inspect(...)`. Схлопнуть в
  публичные методы родителя (оставить `LayerB.TruncateForLog` + `LayerE.*` с телом).
- **`ConfigSanityCheck.FindFirstProxyOutbound`/`InspectOutbound`** — форвардеры к
  `PlaceholderDefense.LayerE_*`; после схлопывания Layer'ов заменить на прямые вызовы.
- Gate: `PlaceholderGuardTests`/`ConfigSanityCheck`/`LeakProtection*` зелёные;
  placeholder-защита сохранена (это security-path — покрытие не ослаблять).

### Phase 4 — stdlib/dedup трим (behavior-adjacent, тесты обязательны)
- `VPNRouter.Core/Platform/Unix/MacDnsParsers.cs:157` — `LooksLikeIpAddress` →
  `System.Net.IPAddress.TryParse(s, out _)`. Сиблинг `DeriveDnsTarget` НЕ трогать
  (ему нужен split по `/prefix`). Прогнать `MacDnsParsers`-тесты.
- `VPNRouter.Android/AppIconCache.cs:56` — ручной LRU (Dictionary+LinkedList+lock)
  → `ConcurrentDictionary<string,Bitmap>` (иконки стабильны per-package; eviction-cap
  не load-bearing на ~100 записях). Только если это упрощает, а не переусложняет.
- `VPNRouter.CLI/Commands/StartCommand.cs:317-343` — `BuildDryRunSources` дублирует
  `ProfileSourceFactory.Create`; dry-run звать фабрику. `StartCommand`-тесты зелёные.
- `build.ps1:60` / `build-linux.ps1:18` — параметр `-GitHubRepo` никто не
  переопределяет → инлайнить константу `PavelLizunov/VPNRouter`.
- `tools/smoke-update.ps1:145` — мёртвые шаги 5-6 (самопризнанные «нет DataDir-хука»)
  → оставить статик AppVersion+ZIP-проверку (шаги 1-4), убрать dead-branch.
- `tools/check-methodology.sh:59` — warn-only мета-тесты #2/#4/#7/#9 не завязаны на
  хук → удалить их или свернуть в единственную реальную проверку.
- (опц.) `build.ps1:226-298` — пять prune-циклов свернуть в один, если готовы
  потерять per-категорию MB-репорт. Минор, можно пропустить.
- Gate: build + full tests; golden `current.json` diff пуст (Phase 4 не трогает
  генерацию, но сверить обязательно — `MacDnsParsers` близко к DNS-пути).

### Phase 5 — финализация
- Бамп `AppVersion.Version` → `2.45.0-r9`.
- `PONYTAIL_AUDIT.md`: отметить, что вырезано / что SKIP (с причиной).
- Ship по `ship-rolling-candidate` (build+CI зелёные, 14 assets, Windows lx-ядро
  через `-SingBoxPath publish/sing-box-lx.exe` — НЕ забыть, это ловушка r8).

## Вне scope Codex (housekeeping — делает user/оператор, не код-правки)
Не для Codex-коммитов, но входит в общий cleanup:
- `git worktree remove` трёх `.claude/worktrees/*` (3.6 ГБ; `funny-jang-fc7848`
  имеет 1 неслитый коммит — проверить перед удалением).
- `rm` устаревших `VPNRouter-*v2.45.0-r6/r7-win.zip` + `android-r3-build.log` в корне.
- Архивировать pre-v2.44 планы в `plans/archive/2026/` (332 файла).
- Решить судьбу `.codex/` и самого `PONYTAIL_AUDIT.md`.

## References
- `PONYTAIL_AUDIT.md` (корень) — источник, 22 находки с `path:line`.
- `plans/CLAUDE.md` — конвенция планов. `VPNRouter.Tests/CLAUDE.md` — где тесты.
- Проверка «мёртвости» Stages выполнена в этой сессии grep'ом (0 вызовов в
  App/CLI/Service/Android; UI → `FetchPoolAsync`).
