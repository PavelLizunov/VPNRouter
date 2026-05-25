# TgProxy feature checklist

Use when release notes mention TgProxy / Telegram proxy / MTProto / tg://.

## Setup

1. Window already launched. Focus + screenshot.
2. If in Simple mode, click "Расширенные настройки".
3. From Advanced mode tab strip, click "Инструменты" → sub-tab "Telegram-прокси".

## Verify hero card (stopped state)

4. Screenshot.
5. Confirm:
   - Plane icon (paper-plane glyph, accent color).
   - Title: "Включить Telegram" / "Activate Telegram".
   - Lede: "Поднимем локальный MTProto, откроем ссылку и Telegram сам подцепит секрет..."
   - Big magic button: "Запустить и открыть Telegram".
   - 3 step chips visible.

## Trigger Start

6. Click the magic button. State transitions:
   - Status text near footer: "Загрузка..." / "Скачивание зависимостей..." (if first run, downloads Python+wheels+source).
   - On success: title flips to "Telegram через MTProto" / "Telegram via MTProto".
   - Air-pill appears in hero center: "В эфире · :1443".

## Verify TgProxyStats display (r15/r16)

7. **r15 change**: wait ~10-15 seconds after start for first stats refresh.
8. Screenshot.
9. Air-pill should now show stats appended: "В эфире · :1443 Активных: N | Всего: N | ↑N ↓N" (or English equivalent in EN locale).
10. **r16 change**: confirm "Активных:" / "Всего:" labels are Russian when language=RU. Pre-r16 they were English ("Active:" / "Total:") regardless of locale.

## Open in Telegram (optional)

11. Telegram Desktop must be installed for the auto-open flow. If
    installed, clicking the magic button should:
    - Open `tg://proxy?server=127.0.0.1&port=1443&secret=XXX` URL.
    - Telegram Desktop receives the deep link, prompts to use proxy.
12. If Telegram Desktop is NOT installed (common on dev VMs), a banner
    appears: "Telegram Desktop не найден. Прокси работает, но открыть
    его автоматически нельзя — скопируйте ссылку..."

## Stop

13. Click the magic button again (now labeled "Остановить" or similar).
14. State transitions back to stopped, air-pill disappears.

## Per-feature log checks

| Looking for | Pattern |
|---|---|
| Proxy spawned | `[TgProxy] python.*proxy.py` |
| Port bound | `:1443` |
| Stats refresh | `[TgProxy] StatsUpdated` |
| Scheme registered | `[TgProxy] tg-scheme: registered=` |
| Stop confirmed | `[TgProxy] proxy stopped` |
| Crash | `[TgProxy] proxy exited unexpectedly` |

## Expected log noise

- `[TgProxyManager] Already running on port 1443` — expected if user
  clicks Start twice rapidly.
- `[TgProxyUpdater] Step 1/3: Downloading Python 3.12` — expected on
  first launch (one-time download).

## Pass criteria summary

- Hero card renders both stopped + running states.
- Air-pill shows port number.
- Air-pill shows stats text after first refresh (r15+).
- Stats labels localized when locale=RU (r16+).
- Stop button transitions cleanly.
- Log shows expected lifecycle events.

## Screenshots to attach

- `tmp-rN-tgproxy-stopped.png` — hero in stopped state.
- `tmp-rN-tgproxy-running.png` — air-pill with stats text.
- `tmp-rN-tgproxy-banner.png` — scheme-missing warning (if applicable).
