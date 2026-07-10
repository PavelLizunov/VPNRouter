# Goal: v2.47.0-r8 — фиксы по итогам глубокого ревью пост-cut доработок

**Дата**: 2026-07-10. **Автор ревью**: Claude (Fable), вручную, без субагентов.
**Скоуп ревью**: все 60 коммитов `v2.46.0..HEAD` (urltest-цепочка v2.46.1-r1/r2,
security-batch, macOS P0.3, driver integrity, TgProxy verify, cleanup, SignPath CI,
Android e2e, рефакторы #4/#7). Прочитаны диффы + окружающий код всех продуктовых
файлов. CI на всех коммитах зелёный; AppVersion=тегу (r7); один in-flight prerelease.

**Цель**: закрыть найденные дефекты одним кандидатом v2.47.0-r8 (F1+F2 обязательны,
F3 + мелочи — тем же коммитом или следом), затем — процессные гейты перед stable.

---

## F1 (P1) — отмена deep-теста юзером клеймит сервер «заблокированным» на 12 часов

### Симптом
Юзер запускает «Deep verify all», жмёт Cancel. Серверы, которые в этот момент были
в HTTP-фазе пробы, получают красный вердикт «Хост доступен, но VPN-протокол не
прошёл проверку» (+ RU-block warning в тултипе), вердикт пишется в
`cache/server_health.json` и R5-фильтр исключает эти серверы из Auto-pool (urltest)
на 12 часов (`ServerHealthStore.FreshTtl`). Рабочие серверы наказаны за отмену.

### Root cause (проверенная цепочка)
1. `VPNRouter.Core/Services/DeepVerifyProbe.cs:91` — `ProbeViaSocksAsync` ловит
   `TaskCanceledException` и возвращает `(false, 0, "http timeout")`, НЕ отличая
   отмену внешнего `ct` (юзер/overall-бюджет) от собственного `HttpClient.Timeout`.
2. `VPNRouter.Core/Services/VlessDeepVerifier.cs:316-320` — `!httpOk` →
   `DeepVerifyResult.Failed(httpErr, DeepVerifyFailurePhase.ProxiedHttp)`.
   Верхний `catch (OperationCanceledException) when (ct.IsCancellationRequested)`
   (строка 355, фаза Cancelled) не срабатывает — исключение проглочено в п.1.
3. `ServerHealthPhaseMapper.FromDeepVerify`: `ProxiedHttp` → `ProxiedHttpControl=Fail`.
4. При свежем quick-probe (`TcpConnect=Pass`) `ServerHealthClassifier.Classify` →
   `ProtocolHandshakeBlockedLikely`.
5. `ServerViewModel.RecomputeHealthVerdict` → `ServerHealthStore.Record` →
   `ConfigGenerator` R5 дропает сервер из Auto-pool.

Кнопка Cancel существует: `MainWindowViewModel.ServerTesting.cs:323/339`
(`_serverDeepCts.Cancel()`, toggle «Test all» / «Cancel»). Окно попадания — до 8 с
HTTP-фазы на сервер при 5 параллельных; сценарий реальный.

До этого цикла ошибка была безобидной строкой в UI; typed-фазы + store подняли цену.

### Fix strategy
В `DeepVerifyProbe.ProbeViaSocksAsync` добавить ПЕРЕД `catch (TaskCanceledException)`:

```csharp
catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
```

Тогда OCE поднимается в `VerifyAsync`, и существующие верхние обработчики корректно
классифицируют: юзер-отмена → `Cancelled` (маппер → пустые фазы → inconclusive, «!»),
overall-бюджет → `Timeout`. `MeasureBandwidthViaSocksAsync` уже делает ровно так —
привести пробу к той же схеме.

Примечание: overall-бюджет (12s) во время HTTP останется server-meaningful
(`Timeout` → `ProxiedHttpControl=Fail`) — это осознанная семантика, не менять.

### Tests
- Unit на `ProbeViaSocksAsync` через отменённый ct (fake/loopback-listener, отменить
  во время запроса) — ожидание: `OperationCanceledException` наружу, НЕ `"http timeout"`.
- Пин цепочки: `DeepVerifyResult(Cancelled)` → `FromDeepVerify` → пустые фазы →
  `Classify(quick Tcp=Pass + deep пусто)` == `TcpOpenProtocolUntested` (НЕ Blocked).
  Половина уже покрыта ServerHealthPhaseMapperTests — добавить недостающий кейс.

---

## F2 (P1/P2) — endpoints-only кастомный конфиг: Validate пропускает, Inject генерит битый JSON

### Симптом
Кастомный конфиг, где прокси — wireguard/AWG **endpoint** (без proxy-outbound;
официальный конструкт sing-box 1.11+, типичный экспорт AmneziaWG), проходит
`Validate` (фикс b4cc2609 научил распознавать endpoint как egress), но при
подключении sing-box падает FATAL «outbound not found» (или конфиг режет
LeakProtection). До b4cc2609 юзер получал внятную ошибку валидации; теперь
«валидно» → opaque-падение при старте — ровно тот класс, против которого коммит
боролся.

### Root cause
`VPNRouter.Core/Services/CustomConfigInjector.cs:523` — `FindProxyOutboundTag`
сканирует только `outbounds` (selector → urltest → первый proxy-like) и для
endpoints-only конфига возвращает несуществующий `"custom-proxy"`. Inject пишет его
в `route.final` (full/exclude, строка ~214) и в route-rules (split) → битый JSON.

### Fix strategy
В `FindProxyOutboundTag` перед финальным fallback `"custom-proxy"`: если
`config["endpoints"]` содержит `JsonObject` с `type == "wireguard"` — вернуть его
тег (через `ResolveOrAssignProxyTag`, mutation-семантика та же). Первый endpoint
достаточен (мультиэндпоинт — вне скоупа).

Проверить глазами смежное: `DetectTcpUdpSplit` (вернёт (tag,tag) — ок, endpoint не
selector), `EnsureSynthesizedRemoteDns`/`InjectDnsRules` с proxyTag = endpoint-тег —
DoH detour на endpoint-тег валиден для sing-box.

### Tests
`CustomConfigInjectorForkGateTests`: positive Inject для `PlainWireGuardEndpointConfig`
(и AWG-варианта с `OverrideAwg=true`) — ассерты: (a) `route.final`/rules НЕ содержат
`"custom-proxy"`, (b) содержат тег endpoint'а (`"proxy"` в фикстуре). Идеально —
прогнать результат через `sing-box check` integration-паттерн (skip если бинаря нет).

---

## F3 (P2) — CI-гейт версий Android сравнивает только core, без -rN

### Root cause / риск
`.github/workflows/build-android.yml` (cd997933): `core(AppVersion) == core(тега)` —
суффиксный дрифт (AppVersion `2.47.0-r6` при теге `v2.47.0-r7`) проходит гейт.
`versionName` APK при этом форсируется тегом (`-p:VpnRouterVersion`), а ВНУТРЕННИЙ
`AppVersion.Version` (им живёт in-app update-check) остаётся старым → класс урока
v2.25.0-r1→r2 (вечный «есть обновление» / update-loop). Rule #5 требует полного
совпадения включая -rN.

### Fix strategy
В шаге «Validate AppVersion.cs matches release version» заменить сравнение core на
полное: `[ "$APPVER" != "$VERSION" ] && fail`. `core()` можно убрать. Проверка
versionName/versionCode в sign-android.yml уже полная — не трогать.

---

## F4 (P3, батч мелочей — один cleanup-коммит)

1. `ServerHealthStore.cs:150` — `File.Delete(path); File.Move(tmp, path)` →
   `File.Move(tmp, path, overwrite: true)` (атомарнее, короче; убирает окно потери
   файла при краше между Delete и Move).
2. `ServerViewModel.RecomputeHealthVerdict` — фоновый `ProviderKey.ResolveAsync`
   фиксирует verdict в замыкании; при медленном DNS (до 3 с) устаревший вердикт может
   перезаписать более свежий Record. Фикс-минимум: в continuation перечитать
   `GetFreshRecord` и записывать key, только если verdict в store совпадает с
   зафиксированным (или Record-перегрузка «update key only»).
3. `AppSettingsSane.cs` — doc-коммент `EnsureSane` («Walk the AppSettings tree...»)
   прилеплен над `GenerateClashApiSecret` (двойной `<summary>`); вернуть его на
   `EnsureSane`.
4. `VpnRouterService.java` (`joinPrefixes`) — C#-стиль `/// <summary>` в Java-файле →
   обычный `//` или javadoc.
5. `IUpdateSource.cs` — осиротевший хвост комментария «Play Console publishing flow
   lands in Phase 4.» после удаления буллета PlayStoreSource — удалить строку.
6. `VlessDeepVerifier.ProbeCanariesViaSocksAsync` doc — обещанного «skipped entirely
   when under ~5s of overall budget remains» в коде нет. Либо убрать фразу из дока
   (канарейки безопасно отменяются overall-бюджетом → Unknown), либо реализовать skip.
7. `FreeConfigDeepVerifier` — остался flat 1500ms SOCKS-bind wait; r4-масштабирование
   по concurrency (`WarmupPerConcurrencySlack`) применено только к VlessDeepVerifier.
   На слабом железе free-configs могут ложно «didn't bind». Портировать тот же
   `EffectiveSocksBindWait` (у FreeConfigDeepVerifier есть свой MaxConcurrency).
8. `AndroidSingBoxRuntime.ClashApiSecret` doc — «AndroidApp sets this right after
   settings load» не соответствует коду (ставится только из
   `AndroidConfigBuilder.BuildConfigJson`). Класс сейчас нигде не инстанцируется —
   поправить док (или поставить секрет и при app init, если класс оживёт).

---

## Процессные гейты (НЕ код, но блокируют stable v2.47.0)

- [ ] **macOS P0.3 live kill-9 verify через приложение.** Транскрипт в ba36ba88 —
  ручная проверка МЕХАНИЗМА pfctl (INERT/CARRIER/FLUSH). Секция «Mandatory live
  kill-9 verify» (`plans/macos-p0.3-pf-anchor-corrected-design-2026-07-10.md`,
  шаги 1-7: full-tunnel + block-on-fail через VPNRouter, kill -9, curl public FAIL /
  LAN OK, disable → egress restored, anchor пуст, без незапланированных перезагрузок
  pf.conf; dead-man guard обязателен) — НЕ выполнена и по плану обязательна до
  stable. Mac: slovn@192.168.0.246 / tailscale 100.116.97.112.
- [ ] **Закоммитить `VPNRouter.Android/global.json`** (пин SDK 10.0.301 для локальной
  Android-сборки) — сейчас untracked, воспроизводимость сборки зависит от машины.
- Принятое поведение (НЕ дефект, зафиксировать в голове): пустой exclude-список
  больше не армит глобальный kill-switch на Linux/macOS (13279d46) — осознанный
  trade-off, глобальный дроп в exclude-режиме убил бы и исключённые приложения.

## Что ревью подтвердило как корректное (не переделывать)

Clash-secret сквозная цепочка (3 платформы, wire-compat, user-authored mismatch
заведён как P2); kill-switch intent (един production call-site,
StartupPipeline.cs:1092); ApplyAsync под lifecycle-гейтом (дедлока нет, host —
только геттеры/события); AutoFailover P1.5 (persist после подтверждённого старта +
откат); urltest Core (pure, fail-open в R5-фильтре — никогда ниже 1 члена; «локальная
поломка != вердикт серверу» выдержано, включая bandwidth-timeout r2); AWG deep-verify
parity (реальный BuildAmneziaWgEndpoint, UnsupportedByVerifier на не-lx);
LeakProtection AWG endpoint validation; TgProxy fail-closed (PyPI digests + пин
python.org); lx HEAD-пины (полные 40-hex); .sys integrity (fail-open диагностика);
P1.4/P1.8; спиннер; ERROR_SERVICE_DISABLED; installer verify-after-Add-MpPreference;
AV-снапшот диагностики (read-only); sign-windows.yml (INERT до enrollment,
fail-fast); Android protect()-фикс (device-verified) + e2e гейты T3.5/T3.6, T12-T15
(прогнаны на телефоне, 3 фазы PASS).

## Acceptance

- [x] F1: отмена батч-deep-теста не порождает `ProtocolHandshakeBlockedLikely`
  (d03412c6; юнит-пины DeepVerifyProbeCancellationTests + mapper e2e guardrail
  зелёные; live на brat: 4 полных deep-прогона по 21 серверу — ноль красных
  клейм, inconclusive-серверы с quick-Pass показывают нейтральное «TCP открыт,
  VPN-протокол не проверен». NB: сам UI-cancel оказался недостижим — кнопка
  disabled while running, ЗАВЕДЕНО как новый P2 в OPEN-DEFECTS, target r9).
- [x] F2: endpoints-only WG/AWG кастомный конфиг: Validate PASS → Inject ссылается
  на тег endpoint'а (d03412c6; 2 positive-Inject пина зелёные; «custom-proxy»
  отсутствует в выводе).
- [x] F3: build-android гейт — полное сравнение `AppVersion == VERSION` (582bad0d).
- [x] F4: батч мелочей закоммичен (5df37f41), тесты зелёные (242 таргетных).
- [x] Ship v2.47.0-r8: тег + prerelease + notes, 14 desktop assets, Mac/Linux CI
  зелёные, commit CI зелёный, v2.46.0 восстановлен Latest, r7 удалён,
  post-ship-verify на brat выполнен (запуск, версия e2c5192a, smoke, deep-прогоны,
  лог-скан clean). Две новые pre-existing находки live-verify занесены в
  OPEN-DEFECTS (dead deep-cancel button; transient «Подключено через службу»
  при probe-спавнах на машине с чужим TUN).
- [ ] macOS kill-9 gate — перед cut stable (отдельная сессия с Mac-доступом).

## Оценка

F1 — 2 строки + 2 теста (~30 мин). F2 — ~10 строк + 2-3 теста (~1 ч). F3 — 3 строки
YAML (~10 мин). F4 — ~1 ч батчем. Ship-цикл r8 — стандартный. Риск низкий: F1/F2 —
точечные, покрываются пинами; F3 — CI-only.

## Связь

- `plans/OPEN-DEFECTS.md` — F1/F2 занести при закрытии (или сразу как resolved-строки).
- `plans/urltest-verification-deferred-risky-2026-07-09.md` — R1-R6 контекст цепочки.
- `plans/macos-p0.3-pf-anchor-corrected-design-2026-07-10.md` — kill-9 gate.
- `plans/cut-stable-checklist.md` — live-update gate перед stable (rule 6f).
