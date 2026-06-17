# Zapret feature checklist

Use when release notes mention Zapret / DPI bypass / probe / cache / hosts.

## Setup

1. Window already launched by Phase 2. Bring it to foreground via
   `mcp__vpnrouter-test__focus_window` with `title="VPNRouter"`.
2. List windows to find current bounds:
   `mcp__vpnrouter-test__list_windows` with `title_filter="VPNRouter"`.

## Navigate to Zapret page

3. If the window opens in Simple mode (Virtual Penguin Network mascot at
   top), click the "Расширенные настройки" / "Advanced settings"
   navigation button near the bottom of the page. Use
   `mcp__vpnrouter-test__mouse_click` at the button's screen coordinates
   (typically near `(window.x + 260, window.y + 720)` — verify via
   screenshot first).
4. From Advanced mode tab strip (Серверы / Подписка / Настройки /
   Приложения / Инструменты / Публичные), click "Инструменты" / "Tools".
5. The Tools tab has sub-tabs: `Zapret`, `Telegram-прокси`, `Экстренный канал`.
   Click `Zapret`.

## Verify hero card

6. Screenshot the page.
7. Confirm hero card visible with:
   - Shield icon (light teal background).
   - Title — when stopped: "Обход блокировок" / "DPI bypass".
   - Lede text — when stopped: "Скачаем zapret, поставим Discord hosts, подберём рабочую стратегию автоматически."
   - Big magic button: "Включить обход блокировок" / "Enable DPI bypass".
   - 3-step chips: "1 скачаем zapret", "2 настроим Discord hosts", "3 подберём стратегию".

## Verify "Тонкая настройка" expander

8. Click the "Тонкая настройка" / "Advanced settings" expander to open.
9. Screenshot.
10. **For r10/r11 changes (cache UI)**: scroll to bottom of expander,
    confirm new section visible:
    - Status line: «Кэш: general (ALT3) (успехов: 3)» OR
      «Кэш пуст — следующая проверка будет полной».
    - Two buttons: "Найти стратегию заново" + "Очистить кэш стратегий".
11. **For r9 changes (Custom Rules localization)**: not applicable on
    Zapret page — see checklist-network-settings.md.

## Trigger probe (optional, slow)

12. Click the magic button to start the auto-probe. The hero state should
    transition through:
    - Title: "Подбираю стратегию..." / "Picking strategy...".
    - Lede: "Тестирую (N/M): general (ALT3) — проверяю Discord и YouTube..."
    - Progress bar visible under the disabled button.
13. **For r3/r4 changes (Flowseal delegate + Part A live score)**: wait
    20+ seconds, screenshot. Lede should update with running «N/M ok»
    score after status-line bursts: «Тестирую (5/20): general (ALT3) — 12/18 ok».
14. **For r4 ipset auto-restore**: log `vpnrouter*.log` should contain
    `[ZapretAutoStrategy] Restored orphaned ipset from prior probe
    interrupt` if a prior probe was interrupted.

## Verify cache hit (Phase 2 of r6+, needs PRIOR successful probe)

15. Stop any running zapret via the magic button (button changes to
    "Остановить обход").
16. Click magic button again. **Expected**: status should transition to
    "Работает [general (ALT3)] (PID NNNN, warm)" within ~7-10 seconds
    instead of running full 2-7 min sweep.
17. If status doesn't show " warm" suffix → cache hit didn't fire; debug
    via `LblZapretCacheStatus` value (Tools expander) or
    `%ProgramData%/VPNRouter/cache/zapret_probe.json`.

## Clear cache button test (r10/r11)

18. In Tools expander, click "Очистить кэш стратегий".
19. Status line above should update to "Кэш пуст — следующая проверка
    будет полной".
20. Toast at footer: "Кэш стратегий очищен".

## Force fresh probe button test (r10/r11)

21. Click "Найти стратегию заново" — should stop any running zapret +
    immediately start a fresh sweep (bypass cache).

## Per-feature log checks

Greps to run on `vpnrouter*.log` after testing (via
post-ship-collect-logs.ps1 + manual inspection):

| Looking for | Pattern |
|---|---|
| Probe cycle started | `[ZapretAutoStrategy] Spawning Flowseal script` |
| Cache hit warm-start | `[VM] ZapretOneTap cache hit:` |
| Cache miss → full sweep | `[VM] Cache miss path — running full sweep` |
| Score parser fires (r5+) | `[VM] ZapretOneTap Flowseal score:` |
| ipset auto-restore | `[ZapretAutoStrategy] Restored orphaned ipset` |
| Winner detected | `[ZapretAutoStrategy] Flowseal sweep winner:` |
| Immediate exit (Bug-r9-G) | `[ZapretAutoStrategy] .* immediate exit (Bug-r9-G)` |

## Expected log noise (not actual errors)

- `[Zapret] Immediate exit detected (code=1, runtime=NNms)` — Bug-r9-G,
  AV blocks winws.exe on dev VM. Expected; not a real failure.
- `[VM] Cache entry stale or unreliable` — expected when cache is older
  than 7 days or has 3+ consecutive failures.
- `winws crashed (exit code: -1)` followed by retry — expected during
  full sweep when individual strategies fail.

## Pass criteria summary

- Hero card renders.
- Title/lede match expected state.
- Tools expander opens.
- New cache UI section visible (r10/r11+).
- Buttons clickable.
- Log scan shows no unexpected [ERR]/Exception/FATAL.
- Cache buttons mutate state visibly (status text changes).

## Screenshots to attach

- `tmp-rN-zapret-stopped.png` — hero in stopped state.
- `tmp-rN-zapret-tools-expander.png` — Tools expander open showing cache UI.
- `tmp-rN-zapret-probing.png` — mid-sweep (optional, slow).
- `tmp-rN-zapret-warm-start.png` — second-trigger warm-start state.
