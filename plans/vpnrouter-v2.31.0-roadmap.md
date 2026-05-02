# v2.31.0 — Stability + A11y cycle

**Тема**: после v2.30.x UX polish (20 fixes за 4 итерации в v2.30.7) — закрываем
deferred items из 47-finding extended audit + 26-finding pre-release walk +
72-finding original audit. Фокус на коде и accessibility, без новых features.

Android v3.0 — отдельный трек.

## Pillar 1 — Core stability (8 items)

Дефекты из code review `VPNRouter.Core/Services/`. Каждый получает unit-тест в
`VPNRouter.Tests/UnitTest1.cs`.

| ID | Файл:строка | Что | Risk | Effort |
|---|---|---|---|---|
| **CO-4** | `ProfileManager.cs:236` | `JsonConvert.DeserializeObject<ProfileCollection>(json)` без `MaxDepth` (DoS на nested JSON arrays) | Low | 30min |
| **CO-8** | `SingBoxManager.cs:610-615` | Empty `catch{}` swallows ExitCode read failure → exitCode==0 и unknown branches report identical "Failed" state | Low | 30min |
| **CO-2** | `HealthMonitor.cs:188` | `_restartCts = new CancellationTokenSource()` reassigned without disposing prior — leaks one CTS per restart attempt | Low | 1h |
| **CO-3** | `SingBoxManager.cs:421` | Sync-over-async deadlock window `_http.PutAsync(...).GetAwaiter().GetResult()` на shared static HttpClient. На saturated threadpool — deadlock | Medium | 2h (рефактор на async path) |
| **CO-1** | `HealthMonitor.cs:124-128` | `_debounceTimer?.Dispose(); _debounceTimer = new Timer(...)` non-atomic. ETW callbacks fire на multiple threadpool threads → double-Dispose race + leaked timer | Medium | 3h (lock или Interlocked.Exchange) |
| **CO-6** | `EtwProcessMonitor.cs:57-66, 91-92` | `_session = session` race vs `Stop()` reading `_session`. Если Stop в окне между ctor и assignment, worker thread blocks на `Process()` forever | Medium | 2h |
| **CO-5** | `FirewallManager.cs:251-269` | `FindRulesByPrefix` parses netsh output на `:` delimiter, ignoring localized field names. На RU/DE/ES Windows может match user-defined Description начинающиеся с `VPNRouter_Block_` и silently delete их | High — потенциальная потеря пользовательских данных | 4h (switch на COM `HNetCfg.FwPolicy2`) |
| **AU-9** | (investigation) | Handle count growth ~170/cycle на VPN start/stop. Memory + threads stable. Возможно child Process objects retained или ETW listeners | Medium — investigation first | 4h |

**Тесты**:
- `JsonDosGuardTests` — feed deeply nested JSON, expect bounded parse
- `SingBoxManagerExitCodeTests` — assert different states for ExitCode==0 vs not-readable
- `HealthMonitorTimerRaceTests` — concurrent OnNewProcessDetected calls, assert no double-dispose
- `EtwSessionStopRaceTests` — Stop right after start, assert no deadlock
- `FirewallManagerLocalizedNetshTests` — mock netsh output на RU/DE locales

## Pillar 2 — A11y (1 systemic fix, ~30 callsites)

| ID | Что |
|---|---|
| **F-8 / F-10 / AU-7** | Empty UIA Name на CheckBox с TextBlock-wrapped Content. Затрагивает: Маршрутизация (1), Защита от утечек (4), Контент (1), Updates (1), Автозапуск (4), DpiBypass (~5), Apps rows (3+ per category × 9 categories), Tools, Free Configs (~5). Итого ~30+ XAML callsites |

**Подход**: написать helper attached property
`AccessibilityHelpers.WrappedContentName="..."` который автоматически выставляет
`AutomationProperties.Name` из дочернего `TextBlock.Text` ИЛИ напрямую перейти
на pattern с явной `AutomationProperties.Name` на каждом CheckBox.

**Альтернатива**: написать UIAutomation peer override для `CheckBox` который
читает `Content` Text recursively. Решение принимаем при старте Pillar 2 после
эксперимента с одним CheckBox.

**Тесты**: один UIA enumeration test после фикса — все CheckBox имеют
non-empty Name.

**Effort**: 4-6h.

## Pillar 3 — ViewModels + Multi-sub (4 items)

| ID | Файл:строка | Что | Effort |
|---|---|---|---|
| **VM-1** | `MainWindowViewModel.cs:3527` | `StartSubRefreshTimer` bails если `SubscriptionUrl` пустой, но multi-sub model использует `Subscriptions[]` — таймер никогда не стартует | 3h |
| **VM-8** | `MainWindowViewModel.cs:2542-2575` | App PropertyChanged subscriptions never unsubscribed на `LoadApps()` rebuilds — leak per RU↔EN toggle | 2h |
| **VM-10** | `MainWindowViewModel.cs:1246-1253` | `ShowRulesToast` Task chain not stored → leak per toast (cumulative if toasts flicker rapidly) | 30min |
| **VM-11** | `MainWindowViewModel.cs:1896-1898` | `ResetConfig` double-arm race (no cancellation token between two clicks) | 1h |

## Pillar 4 — UX polish closure (8 visible items)

| ID | Что | Effort |
|---|---|---|
| **F-3** (UX-2) | Chevron `›` ↔ `▽` flip на Simple-mode "Конфиг·Режим" + "Автозапуск" cards | 1h |
| **F-22** (UX-60) | Saved subtitle truncated — `TextWrapping="Wrap"` | 15min |
| **F-24** (UX-63) | Tooltip на `—` в Скорость column ("Не измерено — запусти Глубокую проверку") | 30min |
| **F-26** | Health Check inline toast после `Process.Start(notepad)` — "Отчёт сохранён + открыт в Блокноте" | 1h |
| **F-18** (UX-65) | "✓✓ Найти рабочие конфиги" дублируется header + button — убрать header или сократить | 15min |
| **F-15** | Tooltip на "Открыть меню service.bat" | 15min |
| **F-27** | Reset settings — visual armed state ("Нажмите ещё раз для сброса", danger glow) | 1h |
| **AU-10** | `domain_regex` consistency — добавить в Cards-mode ComboBox или убрать из validator | 30min |

## Pillar 5 — Defer if time (3 items)

| ID | Что |
|---|---|
| **F-25** | Implausible 1ms latency на all Saved configs — investigation. Reality fronts → localhost? TCP probe local cache? Diagnose only first |
| **F-4** (UX-6) | Greyed boot autostart checkboxes — добавить inline "Установить службу" CTA |
| **F-6** (UX-33) | Subscription card metadata "7s · –" — добавить tooltip "Интервал авто-обновления" |

## Suggested iteration shape

```
v2.31.0-r1: Pillar 1 (Core stability) + 5 unit tests       [3-4 дня]
v2.31.0-r2: Pillar 2 (A11y systemic)                       [1 день]
v2.31.0-r3: Pillar 3 (Multi-sub + VMs)                     [2 дня]
v2.31.0-r4: Pillar 4 (UX closure) + Pillar 5 if time       [1 день]
v2.31.0:    cut stable when verification gate green        [autonomous]
```

**Total scope**: ~20 items, ~7-9 рабочих дней. Каждая итерация верифицируется
через MCP+UIA после auto-update.

## Что НЕ в этом цикле

- **Android v3.0** — отдельный трек (Phase 1.C: libbox.aar, VpnRouterService.kt
  shim, real Avalonia.Android port)
- **Новые features** — нет
- **Design rework** — UI как есть; только bug fixes + a11y
- **Минор UX polish** — берём только high-value items, остальное — backlog

## Cross-refs

- `plans/vpnrouter-extended-audit-2026-05-02.md` — 47-finding extended audit
- `plans/vpnrouter-ux-audit-2026-05-01.md` — original 72-finding audit
- `plans/release-notes-v2.30.7.md` — last stable release notes
- `tools/VpnRouterTestMcp/` — MCP server для in-app verification
- `C:/tmp/uia-helpers.ps1` — PS UIA helper for UIPI-immune text input
