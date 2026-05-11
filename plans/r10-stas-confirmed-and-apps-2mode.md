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
      url: https://ninitux.com/api/v1/app/config/4e5a007b2ab25cb800d9a96d2f36bf37
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
3. Stas позже добавил subscription `simple` (`ninitux.com/.../4e5a...`).
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
| AM-1 | Apps 2-mode Core (settings + migrator + generator) | feature | 3-4 ч |
| AM-2 | Apps 2-mode UI (desktop + Android + tests) | feature | 3-4 ч |

### Зависимости

- F-A, F-B, F-D can run в параллель (touch different files).
- F-C depends on F-A/B (UI marker должен read from corrected resolver
  output).
- AM-1 first → AM-2.
- AM не блокирует F-*.

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
