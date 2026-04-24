# v2.28.x — UX bug-fix batch

**Триггер**: три user-reported проблемы от одного пользователя (2026-04-24):

1. Добавил подписку → в UI конфиги не появились; показались только после Reset settings
2. Zapret качался ненадёжно — пришлось несколько раз жать Download пока не сработало
3. Страница Free Configs «условно неюзабельная» — много разделов, долго ждать первый тест 30k, а если остановить Deep Verify и запустить заново — идёт по всем конфигам, а не по уже помеченным рабочим

Все три — P0/P1 перед следующим stable cut. Рукопожатия user → developer нарушены
по обоим самым важным flow'ам (добавление подписки + DPI bypass + поиск рабочих серверов).

---

## Bug 1 · Subscription add → UI не обновляется (P0)

### Симптом
User открывает Subscribe tab, добавляет URL подписки, жмёт Add. На глаз ничего не
происходит. Серверы видны только после Reset settings (который перезапускает app).

### Root cause
`VPNRouter.App/ViewModels/MainWindowViewModel.cs:1917-1936` — `AddSubscriptionAsync`.

Текущий flow:
```
1. Создать SubscriptionEntry + добавить в _settings.App.Subscriptions
2. Создать SubscriptionViewModel + добавить в Subscriptions (UI)
3. RefreshSubscriptionAsync(svm) ← async fetch
   └─ fetch HTTP → parse → entry.Servers = [...]
   └─ RebuildSubscriptionPool() строит SubscriptionServers (UI binding)
   └─ SaveSettings()
```

**Две проблемы**:

1. **`RebuildSubscriptionPool()` вызывается ТОЛЬКО если fetch успешен** — сидит внутри
   try-блока в `RefreshSubscriptionAsync`. Если HTTP fail / timeout / bad JSON → exception → `finally` не ребилдит → UI остаётся пустым.

2. **Нет авто-переключения на Subscribe tab**. Если user был на Manual (VLESS) tab
   когда жал Add, всё работает но происходит «за кадром», user не видит результат.

### Почему Reset работает
`SettingsLoader.ResetToDefaults()` → рестарт → `LoadSettingsIntoUI()` на старте:
- Читает subscriptions из yaml
- Принудительно вызывает `RebuildSubscriptionPool()`
- Ставит `IsSubscribeMode / SelectedTabIndex` по `ConfigMode`

Вот этот «load path» корректный. «Add path» — сломан.

### Fix strategy

**Step 1 (minimal fix)**: `AddSubscriptionAsync` после Add должен:
```csharp
// Auto-switch to Subscribe tab so user sees the new server list
if (SelectedTabIndex != /* SubscribeTab */ 1) {
    IsSubscribeMode = true;
    IsVlessMode = false;
    SelectedTabIndex = 1;  // (или константа)
}
await RefreshSubscriptionAsync(svm);
// Fail-safe rebuild (ok если RefreshSubscriptionAsync уже ребилднул)
RebuildSubscriptionPool();
```

**Step 2 (защита inside RefreshSubscriptionAsync)**:
```csharp
try {
    // fetch + entry.Servers = [...]
}
finally {
    RebuildSubscriptionPool();  // всегда, даже на exception
    SaveSettings();
}
```

**Step 3 (regression test)**:
- `VPNRouter.Tests/ViewModelTests.cs`: симуляция add subscription в headless Avalonia, проверить что `Subscriptions.Count == 1`, `IsSubscribeMode == true`, `SelectedTabIndex == 1` сразу после Add (без await на fetch).
- Проверить что Add на broken URL (fetch throws) — всё равно `Subscriptions.Count == 1` и UI не падает.

### Acceptance
- [ ] User добавляет подписку → tab переключается на Subscribe → после fetch (≤5с) появляются серверы
- [ ] Если fetch fail (bad URL / no network) → subscription всё равно видна в списке (без серверов), error banner виден
- [ ] Regression test ловит: add subscription → `SubscriptionServers.Count > 0` (или banner с error)
- [ ] Retest для user'а: не требуется reset settings

### Оценка
Маленький fix, 1-2 часа с regression test'ом. Risk: низкий — добавляем fail-safe + auto-switch-tab; не меняем логики персистенса.

---

## Bug 2 · Zapret download ненадёжный (P1)

### Симптом
User несколько раз жмёт Download Zapret — сначала падает, потом в какой-то момент скачивается. Неясно что происходит, ошибки непонятны.

### Root cause
5 cascading issues в `VPNRouter.Core/Services/ZapretUpdater.cs` + `VPNRouter.App/ViewModels/MainWindowViewModel.cs`.

**Issue 2.1 — нет retry на transient ошибках**
Line 112-115: `GetStreamAsync()` на `api.github.com/...` + release asset. Если 403 (rate limit — unauth 60req/hr), 503, или network timeout на середине download → exception, никакого retry. User видит "Download error" и жмёт снова вручную.

**Issue 2.2 — partial download остаётся в %TEMP%**
`finally` на line 175-177 пытается `File.Delete(tempZip)`, но если антивирус держит handle → `IOException` silently swallowed → partial ZIP остаётся. `Path.GetTempFileName()` создаёт новый имя каждый раз, так что сам по себе этот partial не путает следующий attempt — но мы НЕ чистим stale temps старше 1ч в `%TEMP%`, и это growing garbage.

**Issue 2.3 — нет service-level lock от двойного клика**
`IsZapretDownloading` flag — только UI-level guard. Между user click и установкой flag есть race window. Если UI thread подлагивает (какой-то binding update или GC pause) — два `DownloadAndExtractAsync()` запускаются параллельно → ZIP extract fail ("file in use").

**Issue 2.4 — partial ZIP extract silently fails**
Line 127-146: если download был частичный (network drop) — ZIP parse throws, но юзер видит "Invalid release" — непонятно что это значит, think it's a permanent bug.

**Issue 2.5 — криптические error messages**
`"Download error: {ex.Message}"` — message варьируется от "The remote server returned an error (403)" до "I/O error" до "Invalid release". User не понимает: transient? permanent? скачать снова? идти спать?

### Fix strategy

**Step 1 — Retry with exponential backoff**
```csharp
private static async Task<T> RetryAsync<T>(Func<Task<T>> op, int attempts = 3, int baseMs = 2000)
{
    for (int i = 0; i < attempts; i++) {
        try { return await op(); }
        catch (Exception ex) when (i < attempts - 1 && IsTransient(ex)) {
            await Task.Delay(baseMs * (1 << i)); // 2s, 4s, 8s
        }
    }
    throw new InvalidOperationException("Retry exhausted");
}

// IsTransient: HttpRequestException with 5xx, TaskCanceledException on timeout, IOException mid-copy
```

Применить к:
- GitHub API GET `/repos/.../releases/latest` (line 77ish)
- HTTP stream copy to temp file (line 112-115)

**Step 2 — Pre-flight cleanup**
Перед download удалить stale `*.zip` в `%TEMP%` старше 1ч с pattern `tmp*.zip` — clean slate guarantee.

**Step 3 — Service-level semaphore**
```csharp
private static readonly SemaphoreSlim _downloadLock = new(1, 1);

public async Task<...> DownloadAndExtractAsync(...) {
    if (!await _downloadLock.WaitAsync(TimeSpan.Zero)) {
        throw new InvalidOperationException("Download already in progress");
    }
    try { ... } finally { _downloadLock.Release(); }
}
```

UI видит `InvalidOperationException("Download already in progress")` → показывает toast "Already downloading — wait for current to finish". Вместо двух parallel downloads.

**Step 4 — Validate ZIP size**
После download + до extract: check `Content-Length` header против actual file size. Mismatch → retry (probably truncated).

**Step 5 — Actionable error messages**
Categorize exceptions:
- `HttpRequestException` 403: "GitHub rate limit reached. Try again in ~{minutes} min."
- `HttpRequestException` 5xx: "GitHub server error (transient). Retrying automatically..."
- `IOException` during copy: "Network interrupted. Click Download to retry."
- `FileNotFoundException` in extract: "Downloaded file corrupted. Click Download to retry."
- Other: "Download failed: {ex.Message}"

И при retry показать "Retry 2 of 3 in 4s..." вместо тишины.

### Acceptance
- [ ] Transient GitHub 503 → auto-retry → success на attempt 2 без user clicks
- [ ] Двойной клик Download — второй игнорируется с понятным toast
- [ ] Network drop mid-download → auto-retry → success на attempt 2
- [ ] Permanent fail (e.g. нет сети вообще) → clear message "No internet — check your connection"
- [ ] stale partial .zip не аффектит следующий download

### Оценка
2-3 часа. Retry logic — маленькая, uniform pattern. Error categorization — tedious но straightforward. Risk: низкий — добавляем resiliency, не меняем happy path.

---

## Bug 3 · Free Configs page неюзабельная (P1 UX)

### Симптом
> «Много разделов, приходится очень долго ждать первого теста всех 30к конфигов. Также если в какой-то момент остановить — глубокая верификация начинает идти по всем, а не только по тем что помечены рабочими.»

Перевожу на business-speak: **user просто хочет 3 рабочих сервера под Discord за 10 секунд**, а получает 6 разделов, 4 toggle'а, 30k прогресс-бар и regression при Stop.

### Root cause

**Issue 3.1 — 6 разделов одновременно + 6-chip dashboard**
`VPNRouter.App/Views/Pages/FreeConfigsPage.axaml` — 627 строк, 6 секций в left-nav (Overview / Scan / Deep Verify / Filters / My Sources / Cleanup). Каждая с несколькими toggle'ами. Суммарно ~20+ контролов всегда видны.

Non-technical user смотрит на это и видит airplane cockpit, а хотел «показать лучшие».

**Issue 3.2 — Refresh тестирует все ~30k TCP connect'ами**
`FreeConfigAggregator.RefreshAsync` тестирует подряд всё что есть в pool.json. Fast Scan режим skips TLS handshake (~90s vs ~3 минуты) но всё равно тестирует всех. Нет early-stop типа «нашли 100 рабочих — ОК, хватит».

**Issue 3.3 — Deep Verify «Stop → Start заново» регрессит на full list (не только previously-verified)**
`FreeConfigsPageViewModel.cs:605-619` — `foreach (var cfg in candidates)` где `candidates` = полный pool отсортированный по priority. При Stop loop прерывается, но **состояние «какие уже провалидированы» не сохраняется между Stop и Start**. Следующий клик Deep Verify → re-iterates `candidates` с начала.

Фильтр `Status != Timeout && Status != Unreachable` применяется (pre-filter lines 520-526), но вот этот DEEP-VERIFY-RESULT не сохраняется persistent. Т.е. после Stop мы теряем track кого уже deep-verified.

**Issue 3.4 — My Sources + Cleanup занимают space но редко used**
80% users никогда не open My Sources (добавлять свой агрегатор) и не clean cache manually. Эти секции мешают.

### Fix strategy — two-tier UI

**Phase 3A — Simplified default view (v2.28.1)**

Главная страница показывает:
```
┌────────────────────────────────────────────────┐
│  VPN Servers — Free Configs                    │
│                                                │
│  [Refresh]  Found 1247 working (100 best       │
│             shown, latency ≤ 200ms)            │
│                                                │
│  [ Deep verify next 3 ]  [X Only working]      │
│                                                │
│  ┌──────────────────────────────────────────┐  │
│  │ 🇩🇪 mixer-de-01.example.com  120ms  tcp │  │
│  │ 🇳🇱 amsterdam-07.example.com  140ms  tcp│  │
│  │ 🇸🇪 stockholm-02.example.com  180ms  tcp│  │
│  │ ...                                      │  │
│  └──────────────────────────────────────────┘  │
│                                                │
│  [ Apply selected ]        [ Advanced... ]     │
└────────────────────────────────────────────────┘
```

**Что в default скрыто (→ «Advanced»)**:
- Dashboard с 6 chip метриками
- Preset picker (Gaming/Streaming/Chat/BestEffort/Custom) → default «any working», hidden
- Fast Scan + Smart Refresh toggles → default optimal, hidden
- My Sources (редко used)
- Cleanup (destructive, prone to click-and-regret)

**Advanced toggle** (collapse / expand) раскрывает всё выше.

**Phase 3B — Early-stop on Refresh (v2.28.1)**

Добавить в `FreeConfigAggregator.RefreshAsync` параметр `int? stopAfterWorking = null`. Когда достигнуто — cancellation token внутри.

Default в UI: `stopAfterWorking = 100` (достаточно для выбора 3-5 хороших).
Advanced toggle: «Test all 30k» = `stopAfterWorking = null`.

User впервые open'ит page → жмёт Refresh → за 20-30 сек получает 100 рабочих, сортированных по latency. Выбирает top-3 и Apply. Done.

**Phase 3C — Persistent Deep-Verify checkpoint (v2.28.2)**

В cache (`%ProgramData%\VPNRouter\cache\free-configs.json`) добавить field на каждый config:
```
LastDeepVerifyAt: DateTime?
DeepVerifyResult: enum { Unknown, Verified, Failed, Skipped }
```

Deep Verify loop:
```csharp
foreach (var cfg in candidates) {
    if (cfg.LastDeepVerifyAt > DateTime.UtcNow.AddHours(-6)
        && cfg.DeepVerifyResult == Verified) {
        // skip — already verified this session
        continue;
    }
    // ... test, update cfg fields, save incremental
}
```

Это fix'ит: Stop → Start продолжает с `Unknown`/`Failed`, не повторяет успешные.

### Acceptance
Phase 3A:
- [ ] Default view показывает ≤ 6 контролов + список серверов
- [ ] Advanced toggle раскрывает всё остальное
- [ ] Target user: «найти 3 сервера за 30сек» — flow уложился без scroll'а / поиска по разделам

Phase 3B:
- [ ] Refresh останавливается после 100 working (default) → UI responsive, user может Apply сразу
- [ ] Advanced `Test all` — можно задать 30k полный scan если очень надо

Phase 3C:
- [ ] Stop Deep Verify → Start снова → не re-verifyет уже помеченных Verified за последние 6ч
- [ ] Cache survives app restart (session state persistent)
- [ ] Regression: session state не ломает normal Refresh flow

### Оценка
Phase 3A: 4-6 часов (XAML reorganization + Advanced toggle + binding wiring)
Phase 3B: 2-3 часа (параметр + cancellation + UI spinner)
Phase 3C: 3-4 часа (cache schema bump + loop logic + migration от старого кеша)

**Total**: 9-13 часов. Неделя с VP evaluation. Слишком много на один релиз, разбить:

---

## Предлагаемый roadmap

### v2.28.1-r1 — критические bugfix'ы (1 день)
- Bug 1: Subscription add → auto-switch tab + fail-safe rebuild
- Bug 2: Zapret retry + cleanup + concurrent guard + error clarity

### v2.28.2-r1 — Free Configs Phase A+B (2-3 дня)
- Phase 3A: two-tier UI (Simple / Advanced)
- Phase 3B: early-stop after N working

### v2.28.3-r1 — Free Configs Phase C (1-2 дня)
- Phase 3C: persistent deep-verify checkpoint
- Regression tests для всех 3 phase

### v2.28.0 stable cut
После подтверждения user'ом на 2.28.3-r1 что все три user-reported issues fix'нуты.

---

## Priority matrix

| Bug | User impact | Fix effort | Risk | Priority |
|-----|-------------|------------|------|----------|
| 1. Subscription invisible | **P0** — data-loss feel, блокирует onboarding | S (2-3ч) | Low | **v2.28.1** |
| 2. Zapret unreliable | P1 — frustration, eventually works | M (2-3ч) | Low | **v2.28.1** |
| 3A. Free UI two-tier | P1 UX — first impression bad | L (4-6ч) | Med (XAML refactor) | **v2.28.2** |
| 3B. Early-stop refresh | P1 UX — 30s wins | M (2-3ч) | Low | **v2.28.2** |
| 3C. Deep-verify checkpoint | P2 UX — regression annoyance | M (3-4ч) | Med (cache schema) | **v2.28.3** |

---

## Regression testing focus

Эти bug'и — сигнал что у нас **тонко с UI-level integration testing**. Unit-тесты (ConfigGenerator / LeakProtection / Subscription parse) крепкие, но ViewModel state transitions (tab switch / observable rebuild / cancellation) покрыты мало.

Action: после v2.28.x batch — написать headless Avalonia regression suite для:
- AddSubscription happy path
- AddSubscription failure path (bad URL)
- Zapret download concurrent clicks
- FreeConfigs Refresh + Stop + Refresh resume

Это отдельный plan item для v2.29.

---

## Связь с core audit plan

Этот документ **не заменяет** `plans/vpnrouter-core-stability-audit.md` (core VPN layer). Complementary: core stability = network/TUN/sing-box layer; этот = UI/UX + orchestration layer.

Cross-refs:
- Core audit §F2 (subscription refresh не triggers ConfigReload) — близко к Bug 1 но не то. §F2 про live-VPN-running refresh. Bug 1 про add-subscription-before-start. Оба стоит fix'ать.
- Core audit §D5 (HealthMonitor restart storm UX) — ни при чём, другой flow.
