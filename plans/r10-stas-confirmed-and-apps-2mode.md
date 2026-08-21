# r10 — stas root cause confirmed + Apps Include/Exclude 2-mode (2026-05-11)

Triggered by:
1. stas прислал `current.json` + `config.yaml` — точный root cause найден.
2. User: для Application tab нужно 2 mode — Exclude и Include.

## 1 · Bug-r9-F-actual — root cause (legacy vless.servers shadow-override)

### Что показывают stas's файлы

**`config.yaml`** (актуально 2026-05-11 14:19):
```yaml
app:
  config_mode: generated          # <-- НЕ custom!
  active_subscription_server: de-01 443 Khunrath  # <-- работающий сервер, но игнорируется
  subscriptions:
    - name: simple
      url: https://example.invalid/redacted-test-subscription
      servers: [de-01 443 / de-01 2083 / is-01 443 / is-01 8443 / is-01 8444 / nk-01 8443 / nk-01 2083]  # 7 рабочих
vless:
  server: 195.135.255.216         # <-- LEGACY single-server fields
  port: 443
  uuid: 352714f4-7ecc-4c22-805f-ed5c5239f5bb
  reality:
    public_key: DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU  # <-- идентично нашему Android PlaceholderVlessUri!
    short_id: 78ca7952
  servers:
    - name: khunrath_ln           # <-- placeholder server
      server: 195.135.255.216
    - name: is-01-grpc-test       # <-- another stale entry
      server: 93.95.226.167
  active_server: khunrath_ln      # <-- ACTIVE = placeholder!
```

**`current.json`** (сгенерированный sing-box config):
```json
"outbounds": [
  { "type": "vless", "tag": "proxy",
    "server": "195.135.255.216", "server_port": 443,  // <-- DEAD server
    "uuid": "352714f4-...", "public_key": "DnT9hIvt5QEx...",
    "short_id": "78ca7952", "server_name": "yahoo.com", "fingerprint": "firefox" }
]
```

### Root cause sequence

1. Stas когда-то paste'нул в VLESS direct mode test/sample URI →
   осел в `vless.server` + `vless.servers[0]` (`khunrath_ln`).
2. Reality config совпадает с placeholder Android'а — это
   **тот же test-URI который мы используем в `MainActivity.cs`**
   (DEFCT-005). Скорее всего, stas взял его из public docs / repo /
   обмена.
3. Stas позже добавил subscription `simple` (адрес удалён из репозитория).
4. **`VlessServersResolver` НЕ очистил `vless.servers` от legacy
   entries** — они продолжают сосуществовать с subscription'ом.
5. `vless.active_server = "khunrath_ln"` (старый, никем не сменённый
   при добавлении subscription).
6. `ConfigGenerator` строит `outbound[proxy]` из active = `khunrath_ln`
   = `195.135.255.216` → mortale dead.
7. Subscription'овские серверы (de-01 et al.) загружены в
   `subscriptions[0].servers` но не маршрутируются — никаких outbound
   на 104.194.156.93 / 93.95.226.167 / 194.87.222.111 не создаётся.

### Что значит для уже-сделанного Bug-r9-F-DEFENSIVE (3-in-1)

- **Fix 1** (`CustomConfigInjector` tag policy) — irrelevant для stas
  (он не в Custom Config mode). Защищает от похожей проблемы в custom-mode
  path → keep.
- **Fix 2** (`LeakProtection` IP-mismatch check) — НУЖНО **дополнить
  логику**: сейчас фильтрует через `vless.servers ∪ subscriptions[*].servers`.
  Для stas'а `195.135.255.216` ЕСТЬ в `vless.servers` → НЕ сработает.
  Надо разделить:
  - Когда config_mode=generated И есть enabled subscriptions → check
    IP outbound ∈ `subscriptions[enabled].servers` ONLY. Не учитывать
    `vless.servers` (это legacy).
  - Когда config_mode=generated И НЕТ subscriptions → check IP ∈ `vless.servers`.
  - Когда config_mode=custom → check IP в custom JSON outbounds (отдельный path).
- **Fix 3** (Simple page outbound display) — будет показывать
  `khunrath_ln @ 195.135.255.216` → user в UI сразу заметит несоответствие
  с тем что у него «subscribe» режим. Это catch-all.

### Конкретный Bug-r9-F-fix (новая работа)

**Fix-A — Active-server scope guard** (highest priority).
`VlessServersResolver` (или `ConfigGenerator` upstream) должен:
- Если `config_mode = "generated"` + есть ENABLED subscriptions →
  использовать ТОЛЬКО subscription servers для построения outbounds
  и для resolution `active_server`. Игнорировать `vless.servers` /
  `vless.active_server` полностью (они legacy).
- Если стартовое `vless.active_server` указывает на сервер, которого
  НЕТ в активных subscription servers → выбрать `subscriptions[0].servers[0]`
  по умолчанию + WARN log + persist в config.yaml.

**Fix-B — Migration on settings load** (one-shot cleanup для существующих
пользователей).
`SettingsMigrator` при load:
- Если есть ENABLED subscriptions ∪ active_subscription_server ИЛИ
  config_mode=generated → очистить `vless.servers` от entries что нет
  в subscriptions. Сохранить только те, что user намеренно сохранил
  через `vless direct mode` (если такой режим still доступен).
- Pre-action: вывести в migration log список удалённых entries для
  audit. User увидит в `vpnrouter.log`: "[Migrator] Removed stale vless.servers
  entry: khunrath_ln (195.135.255.216) — not in any enabled subscription".

**Fix-C — UI: пометить legacy entries**
В `ServersPage` (VLESS direct mode sub-tab), если запись `vless.servers[i]`
без соответствия в `subscriptions[*].servers`, окрасить ⚠ + tooltip
"Это запись от прошлой ручной настройки. Если она вам не нужна,
удалите её здесь."

**Fix-D — обновить уже-сделанный LeakProtection check (Fix 2 из r9)**:
расширить scope-aware logic (см. выше).

### Severity overall

**P0 privacy-critical** — stas (и потенциально другие upgrade'нувшиеся
v1.x → v2.x пользователи) могут иметь silent traffic-routing в
placeholder/leak server без визуальной обратной связи.

### Action items

- [ ] Fix-A: `VlessServersResolver` scope guard.
- [ ] Fix-B: `SettingsMigrator` legacy `vless.servers` cleanup.
- [ ] Fix-C: ServersPage UI marker для orphan entries.
- [ ] Fix-D: расширить LeakProtection scope detection.
- [ ] Unit tests на каждый fix с фикстурой воспроизводящей stas'овский config.yaml.
- [ ] Migration test: load `stas-config.yaml` → assert вычищенные `vless.servers` + new active_server = subscription's first.

## 1.5 · F-E — Dead config detect + auto-failover (NEW)

### Триггер
User: "По поводу старта с мертвого конфига нужно что-то придумать в плане
какую-то проверку и переключения на живой".

Сценарий stas'а — uppermost-level паттерн всего класса проблем:
- Юзер уже в плохом состоянии (placeholder/legacy/orphan active_server).
- App стартует, sing-box работает, traffic уходит на dead IP.
- В UI ничего не видно — кажется что VPN OK.

F-A/B/D предотвращают появление этого состояния. **F-E — defensive
runtime layer**: даже если бывшее состояние не вычищено / не покрыто
F-A/B, при старте VPN сами обнаруживаем "мёртвый" сервер и
переключаемся на работающий.

### Двухфазная проверка

**Phase 1 — pre-start (синхронная, до запуска sing-box)**:
проверяем `outbound[proxy]` JSON на паттерны известных placeholder'ов:
- Known-bad public_key list (`DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU` —
  наш Android `PlaceholderVlessUri`, possibly others from public docs).
- Known-bad short_id list (`78ca7952`).
- Known-bad server IP list (`195.135.255.216` для текущего инцидента;
  расширяется со временем).
- Empty / null server / port / uuid → invalid.

Если match — НЕ запускаем sing-box. Возвращаем `DeadConfigDetected`
event с reason. Engine ловит, пытается auto-switch (Phase 3).

**Phase 2 — post-start probe (асинхронная, 15-30s после старта)**:
после запуска sing-box проверяем connectivity:
- Через Clash API: `GET http://127.0.0.1:9090/proxies/proxy/delay?url=http://www.gstatic.com/generate_204&timeout=5000`.
- Если delay >= 5000 или error → probe-failed.
- 2 probe attempts с 5s паузой → если оба fail → Dead.

### Phase 3 — Auto-switch logic

При `DeadConfigDetected` (от Phase 1 ИЛИ Phase 2):

**ConfigMode = generated** + есть enabled subscriptions:
1. Найти текущий `active_subscription_server` (или `vless.active_server`
   если subscription pickel ещё не настроен).
2. Перебрать оставшиеся servers в `subscriptions[enabled].servers` по
   порядку (skip already-tried, skip placeholder-paterns).
3. Для каждого: persist active_server → regenerate config → restart
   sing-box → Phase 2 probe.
4. Если 3 серверa подряд fail → surface UI alert "Все серверы недоступны:
   проверьте подписку или сеть" + остановиться (НЕ зацикливаться).

**ConfigMode = generated** + НЕТ subscriptions (легаси / direct VLESS):
- Если есть `vless.servers[]` с не-placeholder entries → попробовать
  переключиться (пометить текущий active_server как dead и взять
  следующий не-placeholder).
- Если все placeholder / пусто → UI alert "Конфигурация выглядит
  тестовой/некорректной. Откройте Server Settings и подкорректируйте
  данные" + остановиться.

**ConfigMode = custom**:
- Юзер сам paste'нул JSON, мы не вольны менять outbound'ы.
- UI alert "Кастомный конфиг недоступен: первый probe сервера не прошёл.
  Проверьте JSON или используйте subscribe mode".
- Остановиться (не reattempt).

### Implementation skeleton

Новый файл `VPNRouter.Core/Services/ConfigSanityCheck.cs`:

```csharp
public sealed class ConfigSanityCheck
{
    private static readonly HashSet<string> KnownPlaceholderPubkeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU", // Android PlaceholderVlessUri + stas
    };
    private static readonly HashSet<string> KnownPlaceholderShortIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "78ca7952",
    };
    private static readonly HashSet<string> KnownPlaceholderServers = new()
    {
        "195.135.255.216",
        // расширяется как находим
    };

    public PreStartResult CheckBeforeStart(JsonObject singboxConfig) { ... }
    public async Task<ProbeResult> ProbeAsync(int clashApiPort, CancellationToken ct) { ... }
}

public sealed class AutoFailoverEngine
{
    private readonly VpnEngine _vpn;
    private readonly ConfigSanityCheck _sanity;
    private readonly ISettingsLoader _settings;
    private int _attemptedServerCount = 0;
    private const int MaxAttempts = 3;

    public async Task<FailoverOutcome> HandleDeadConfigAsync(DeadConfigReason reason, CancellationToken ct) { ... }
}
```

Hook в `VpnEngine.StartAsync()`:
```csharp
var preCheck = _sanityCheck.CheckBeforeStart(generatedConfig);
if (preCheck.IsDead)
{
    _logger.Warning("[F-E] Pre-start sanity check: dead config detected ({Reason}). Attempting auto-failover.", preCheck.Reason);
    return await _failoverEngine.HandleDeadConfigAsync(preCheck.Reason, ct);
}
// ... start sing-box ...
_ = Task.Run(async () =>
{
    await Task.Delay(TimeSpan.FromSeconds(15), ct);  // give sing-box time to settle
    var probe = await _sanityCheck.ProbeAsync(_clashApiPort, ct);
    if (probe.IsDead)
    {
        _logger.Warning("[F-E] Post-start probe failed ({Reason}). Auto-failover.", probe.Reason);
        await _failoverEngine.HandleDeadConfigAsync(probe.Reason, ct);
    }
});
```

### UI feedback

`MainWindowViewModel`:
- `AutoFailoverActive` (bool observable).
- `AutoFailoverProgressText` ("Проверяем сервер 2 из 3...").
- `AutoFailoverFailed` event → toast "Все серверы подписки недоступны.
  [Открыть Servers]".

`SimplePage` / `ServersPage`:
- Если active server поменялся через failover — показать badge "Auto
  переключение → de-01 443 Khunrath" с возможностью revert.

### Acceptance

- [ ] stas-evidence-config.yaml → start → Phase 1 ловит placeholder
      pubkey/sid/server → автоматически переключается на
      `subscriptions[0].servers[0]` = `de-01 443 Khunrath`.
- [ ] Mock probe возвращает 5s timeout → Phase 2 ловит → переключение.
- [ ] 3 attempt'a fail → UI alert + stop (no infinite loop).
- [ ] Custom config mode: probe fails → alert, NO auto-switch.
- [ ] Unit tests:
  - `ConfigSanityCheck_DetectsPlaceholderPubkey`
  - `ConfigSanityCheck_DetectsPlaceholderServer`
  - `ConfigSanityCheck_PassesValidConfig`
  - `AutoFailoverEngine_PicksNextSubscriptionServer`
  - `AutoFailoverEngine_StopsAfter3Attempts`
  - `AutoFailoverEngine_NoSwitchInCustomMode`

### Severity

P0 / safety-net. F-A/B/D предотвращают; F-E ловит уже-сломанное
состояние у существующих пользователей.

### Action items

- [ ] `ConfigSanityCheck.cs` — pre-start + probe.
- [ ] `AutoFailoverEngine.cs` — orchestration.
- [ ] `VpnEngine.StartAsync` integration.
- [ ] UI bindings: `MainWindowViewModel.AutoFailoverActive` + simple page badge.
- [ ] Tests (см. acceptance).
- [ ] Unify placeholder lists с Android `PlaceholderVlessUri` constants
      (Core single-source-of-truth).

## 2 · Applications: Include vs Exclude — 2-mode

### Текущая поведенческая модель (как есть)

В `ApplicationsPage` сейчас single-mode: user выбирает чекбоксами какие
приложения **routit'ятся через VPN** (= **Include mode**, по сути
"split tunnel: только выбранные через proxy"). Все остальные приложения
идут direct.

В sing-box config (`current.json` stas'а):
```json
{ "process_name": ["chrome.exe", "msedge.exe", ...], "action": "route", "outbound": "proxy" }
```
= "выбранные → proxy" + final route = direct. Это Include mode.

### Что хочет user: 2-mode toggle

User'у нужен альтернативный режим **Exclude mode**: все приложения
направляются через VPN ПО УМОЛЧАНИЮ, а в чекбоксах user отмечает те
что должны идти **direct** (исключения). Это inverted-split-tunnel —
полезно когда большая часть приложений хочет VPN, а 2-3 (RU банк,
RU госуслуги, Steam) — direct.

### UI design

В `ApplicationsPage` — добавить новый segmented toggle сверху:
- **Include через VPN** (default, текущее поведение)
- **Exclude из VPN**

Перевод колонок:
- Include mode: чекбокс = "идёт через VPN" (default off → direct).
- Exclude mode: чекбокс = "идёт мимо VPN, direct" (default off →
  через VPN).

В обоих режимах:
- Save bar (или auto-save per Bug-r9-I) — сохраняет actual selection.
- Состояние "selected" приложений хранится в `AppSettings.App.RoutingApps`
  (один список) или в **отдельных полях**: `RoutingAppsInclude` +
  `RoutingAppsExclude` — TBD ниже.

### Storage / config schema decision

**Опция A — single list + mode flag**:
```yaml
app:
  routing_apps_mode: include   # | exclude
  routing_apps:
    - chrome.exe
    - firefox.exe
```
Pros: minimal schema change.
Cons: switch mode = очистка / migration данных.

**Опция B — два списка, активен один**:
```yaml
app:
  routing_apps_mode: include
  routing_apps_include:
    - chrome.exe
    - firefox.exe
  routing_apps_exclude:
    - steam.exe
    - bank-client.exe
```
Pros: user может переключаться без потери выборки в другом mode.
Cons: больше state, риск рассинхрона если user changes apps в обоих.

**Recommend опция B**: user может приходить и переключаться между
modes для разных тестов / use-cases — не теряя предыдущие выборы.
Зависимости с code:
- `AppSettings.App.RoutingAppsMode` ∈ {"include", "exclude"} — новое поле.
- `RoutingAppsInclude` / `RoutingAppsExclude` (List<string> processNames).
- `SettingsMigrator`: legacy `routing_apps` → `routing_apps_include`.
- `ConfigGenerator`: switch на mode →
  - include: `{ "process_name": [list], "action": "route", "outbound": "proxy" }`
    + `route.final = "direct"`.
  - exclude: `{ "process_name": [list], "action": "route", "outbound": "direct" }`
    + `route.final = "proxy"`.

### Action items

- [ ] Add `AppSettings.App.RoutingAppsMode` + `RoutingAppsInclude` /
      `RoutingAppsExclude` fields.
- [ ] `SettingsMigrator`: legacy `routing_apps` → `routing_apps_include`.
- [ ] `ConfigGenerator`: branch on mode for process_name route rules
      + `route.final`.
- [ ] `ApplicationsPage.axaml` (desktop): segmented toggle + bilingual
      copy. Use `Localization/Strings.cs`:
  - `AppsModeInclude` (RU "Только выбранные через VPN" / EN
    "Only selected via VPN").
  - `AppsModeExclude` (RU "Все, кроме выбранных, через VPN" / EN
    "All except selected via VPN").
  - `AppsModeHint` — короткое пояснение под toggle.
- [ ] `MainWindowViewModel` (App): `RoutingAppsMode` observable,
      `SwitchRoutingAppsModeCommand`, `IsAppRouted` computed property
      that returns IsChecked based on mode + presence in lists.
- [ ] Android (`AndroidApp.axaml.cs` Applications partial): аналогичный
      mode picker. Mobile design Mobile.html уже показывает segmented
      "Selected only / Exclude selected" — design parity.
- [ ] Tests:
  - `ConfigGeneratorIncludeModeTests` — выбраны 3 process → 3 → proxy
    + final direct.
  - `ConfigGeneratorExcludeModeTests` — выбраны 3 process → 3 → direct
    + final proxy.
  - `SettingsMigratorLegacyAppsTests` — legacy → include + Mode=include.
  - `MainWindowViewModelAppsModeTests` — switch mode → UI list refreshes,
    selected entries persist.
  - `LeakProtectionExcludeModeTests` — exclude mode не должен ронять
    проверки (final=proxy всё ещё валиден).

### Severity / effort

Severity: feature request (not a bug). Effort: ~6-8ч (Core + App +
Android + tests). Можно разделить на 2 chip'а:
1. **chip-AppsMode-Core**: AppSettings + SettingsMigrator +
   ConfigGenerator + tests.
2. **chip-AppsMode-UI**: ApplicationsPage (desktop) + Android
   Applications partial + Localization + UI tests.

## 3 · Сводка задач (для очереди chip'ов)

### Сейчас актуально (после stas confirm)

| # | Chip | Severity | Effort |
|---|---|---|---|
| F-A | `VlessServersResolver` scope guard (Bug-r9-F Fix-A) | **P0** | 2-3 ч |
| F-B | `SettingsMigrator` legacy `vless.servers` cleanup | **P0** | 2-3 ч |
| F-C | ServersPage legacy-entry warning marker | P2 UX | 1-2 ч |
| F-D | LeakProtection scope-aware refinement | **P0** | 1-2 ч |
| F-E | Dead config detect + auto-failover (runtime safety net) | **P0** | 4-5 ч |
| AM-1 | Apps 2-mode Core (settings + migrator + generator) | feature | 3-4 ч |
| AM-2 | Apps 2-mode UI (desktop + Android + tests) | feature | 3-4 ч |

### Зависимости

- F-A, F-D, F-E can run в параллель (touch different files).
- F-B touches `SettingsMigrator.cs` — AM-1 тоже его модифицирует
  → объединяем в один chip (AM-1+F-B combo) для избежания merge conflict.
- F-C depends on F-A/B (UI marker должен read from corrected resolver
  output).
- AM-1+F-B first → AM-2.
- AM не блокирует F-*.

### Execution wave plan (2026-05-11)

**Wave 1 (parallel chips, spawn together):**
- F-A: VlessServersResolver scope guard.
- F-D: LeakProtection scope-aware.
- F-E: ConfigSanityCheck + AutoFailoverEngine + VpnEngine integration.
- AM-1+F-B combined: SettingsMigrator (legacy vless.servers cleanup +
  new RoutingApps* fields default) + AppSettings + ConfigGenerator
  include/exclude branching.

**Wave 2 (inline, after wave 1 merges back):**
- F-C: ServersPage UI marker for orphan vless.servers entries.
- AM-2: ApplicationsPage segmented toggle + ViewModel binding +
  localization + Android Applications partial.
- Integration tests across all fixes (stas-evidence fixture).

### Что было сделано в r9 (committed)
- ✅ Bug-r9-I (Apps tab persist + auto-save) — `d72420f`.
- ✅ Bug-r9-F-DEFENSIVE 3-in-1 — `30a5f22` (но Fix-2 теперь требует
  доработки → F-D в этом плане).
- ✅ Bug-r9-H stale TUN cleanup — `3fd653a`.
- ✅ Bug-r9-E+G VPN conflict detection + Zapret UX — `e1cf0de`.
- ✅ wgturn Phase 1 build — `696486f`.
- ✅ wgturn Phase 2 Core skeleton — `655fb6b`.

### Что НЕ блокировано user'ом
- F-A, F-B, F-C, F-D, AM-1, AM-2 — все можно делать без дополнительной
  информации (stas's config.yaml уже у нас).
