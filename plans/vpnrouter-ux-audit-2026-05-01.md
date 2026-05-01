# VPNRouter v2.30.2 — UX audit (live, in-app)

**Date**: 2026-05-01
**Driver**: Claude via `mcp__vpnrouter-test__*` (screenshot + click)
**App version**: v2.30.2 stable (just shipped)
**Window state**: ~510 × 700, default-ish size

Methodology: walk every page, click every interactive element, take a
screenshot, note observations and any UX issues. Findings are graded:

- **🐛 BUG** — actual broken behaviour (something doesn't work)
- **⚠️ UX** — confusing / inconsistent / unclear
- **💡 SUGGEST** — possible improvement, not a bug
- **✅ OK** — works as expected (audit positive — not a finding)

The finding numbers are sequential as I encounter them.

## Initial state

- VPN: disconnected
- Mode: Simple
- ConfigMode: вручную (generated) / полный (full tunnel)
- Active server: is-01-grpc-test (from last test session)
- Subscriptions: 1 enabled (simple, ninitux URL)
- Custom configs: none
- Manual VLESS servers: 3 (is-01-grpc/hy2/tuic)

---

## Simple Mode

(layout sections in DOM order top-to-bottom)

### Header

- App icon, title "Virtual Penguin Network"
- 3 protocol chips: `• VPN`, `Zapret`, `TG`
- "..." overflow menu (right)
- Window title "Virtual Penguin Network" (Win32) — different from
  product name "VPNRouter" — affects tooling that searches by title
  (e.g. my MCP `list_windows` filter "VPNRouter" returned nothing).

**⚠️ UX-1**: Win32 window title `"Virtual Penguin Network"` vs product
name `"VPNRouter"` mismatch. Tooling and Alt+Tab show the friendly name
but help docs / install command refer to "VPNRouter". Either align both
or document the dual naming.

### Status card (top)

- Grey dot ● + "Не подключено" + subtitle "Трафик идёт напрямую — выбери
  конфиг и запусти туннель."
- ✅ Clear copy.

### "Конфиг · Режим" card

- Subtitle: "вручную · полный" (current mode + tunnel routing)
- Chevron `›` on right.

**⚠️ UX-2**: Clicking on this card collapses the lower section
("Конфиг VPN" + "Что идёт через VPN" + "Автозапуск"). The chevron `›`
doesn't visually flip to `▽` when expanded — user can't tell from the
icon whether the section is open or closed. Looks like a glitch the
first time you accidentally click it.

### "Конфиг VPN" input field

- Label: "Конфиг VPN"
- Placeholder/value: ninitux subscription URL (already filled)
- Hint below: "Приму vless://-ссылку или URL подписки (http/https)."
- ✅ Hint is bilingual-aware and accurately describes what's accepted.

### "Что идёт через VPN" — radio group

- Option 1: "Выбранные приложения" — subtitle "Discord, браузеры,
  мессенджеры, рабочие"
- Option 2: "Весь трафик" (selected) — subtitle "Включая игры и банки"

**⚠️ UX-3**: Subtitles list specific apps ("Discord, браузеры …") for
"Выбранные приложения" but the actual selected apps depend on what
profiles are toggled in Advanced → Приложения. New users may not
realise they need to go configure that to actually filter on those
apps. Either drop the example list or add a tooltip / link "Настроить
список →".

### "Автозапуск" card

- Title "Автозапуск"
- Subtitle "Настроить автозапуск VPN при старте Windows"
- Chevron `›`

**⚠️ UX-4**: Click on "Автозапуск" Simple-mode card jumps the user into
**Advanced mode** → Настройки tab → Автозапуск sidebar section.
Counter-intuitive — Simple-mode users expect the Simple-mode card to
expand inline or open a small flyout, not flip the entire app into a
6-tab + sidebar Advanced view. Suggest: inline disclosure / small
modal in Simple mode, or at minimum a confirmation "Switch to advanced
to configure autostart?".

### Подключить (primary button)

- ✅ Standard primary CTA, large, accent.

### Расширенные настройки (expander/link at bottom)

- Title "Расширенные настройки"
- Subtitle list: "Серверы · Подписки · Zapret · Telegram-прокси · Free Configs"
- Chevron `›`
- ✅ Click flips to Advanced mode (verified). Names accurately match
  the Advanced top tabs.

---

## Advanced Mode — Настройки tab → Автозапуск sub-section

(Reached via the Автозапуск card click above.)

Layout:
- Left sidebar: Маршрутизация / Правила / Защита от утечек / Контент /
  Обновления / **Автозапуск** (selected)
- Main content: 3 sub-sections.

### Sub-section "На старте Windows (до логина)"

Description: "Нужна служба Windows для запуска VPN, Zapret или
Telegram-прокси до входа пользователя."

- Checkbox "Фоновая служба Windows" (off)
  - Subtitle: "Запускает VPN / Zapret / Telegram-прокси при загрузке ОС
    до входа в систему. Требует прав администратора."

**💡 SUGGEST-5**: Subtitle says "Требует прав администратора." but
doesn't telegraph that toggling will trigger a UAC prompt and/or run an
installer. First-time users may click and feel something is broken
during the (silent) UAC delay. Add "(вызовет запрос UAC)" hint, or
toast "Запрашиваем права администратора…" on click.

### Sub-section "Запускать при старте службы" (greyed out)

3 disabled checkboxes:
- "Запускать VPN при старте системы"
- "Запускать Zapret при старте системы"
- "Запускать TgProxy при старте системы"

Footer: "Эти флаги читает служба при boot. Требуется установленная
служба."

**⚠️ UX-6**: User sees three things they want but can't enable, and
the only fix instruction is "Требуется установленная служба" with NO
link / button to install. Add inline "[Установить службу]" CTA, OR
make it implicit: enabling "Фоновая служба Windows" above auto-installs
+ then unlocks these.

### Sub-section "При входе пользователя"

- Checkbox "Запускать интерфейс при входе в Windows" (✓ on)
  - Description: "Запускает приложение VPNRouter после входа. VPN
    придётся стартануть вручную или включить «на старте Windows»
    выше."
- ✅ Clear, references the section above by name.

### Footer toolbar (visible inside the page above the tab status bar)

- Left: "✓ Настройки сохраняются автоматически при изменении"
- Right: "Применить сейчас (перезапустить VPN)" button

**🐛 BUG-7 (LAYOUT)**: At the current window width (~510 px), the
auto-save text on the left is **truncated by and overlaps with** the
"Применить сейчас (перезапустить VPN)" button on the right. The
button background covers part of "при изменении". Looks broken; the
two need either narrower text, Wrap, or a stacked layout below ~600 px.

**⚠️ UX-8**: The footer text says "Настройки сохраняются автоматически
при изменении" — implying nothing else needs to happen. But there's
ALSO a "Применить сейчас" button to its right that says "перезапустить
VPN". So which is it — auto-save no restart, or auto-save and restart?
Add micro-copy clarifying: e.g. "✓ Сохранено. Чтобы изменения вступили
в силу — Применить сейчас."

---

## Advanced Mode — Настройки tab → Маршрутизация

Sidebar: Маршрутизация / Правила / Защита от утечек / Контент /
Обновления / Автозапуск (each clickable; persists across tabs in the
session — clicking other top-level tabs and coming back lands on
last-selected sub-section).

Маршрутизация content:
- Header: "Маршрутизация" + description "Определяет, какой трафик
  пойдёт через VPN."
- Radio card 1: **"Split Tunnel"** — "Только выбранные приложения.
  Остальное идёт напрямую."
- Radio card 2 (selected): **"Full Tunnel"** — "Весь трафик ОС через
  VPN, включая игры и банки."
- Checkbox card: "Российский трафик через реальный IP" (✓) — long
  description.

**⚠️ UX-9 (D1 RULE VIOLATION)**: "Split Tunnel" and "Full Tunnel" are
hardcoded English in a Russian UI. CLAUDE.md app rule D1 explicitly
forbids this. Suggest: "Раздельный туннель / Полный туннель" or
"Split-туннель / Full-туннель" if keeping the technical term is
desired.

## Advanced Mode — Настройки → Правила

Layout:
- Header: title text **"Прав"** (LOOKS LIKE TRUNCATED "Правила"!) + view
  toggle pills: **Карточки** (selected) / Список / Текст / Импорт... /
  Экспорт...
- Blue info card: "Как работают правила." with bullet points + X close
- Checkbox: "Свои правила важнее тумблеров" (off)
- "Добавить правило:" form:
  - 2 dropdowns: "direct" + "domain_suffix"
  - input ".corp.example"
  - "Комментарий" input
  - "+ Добавить" full-width primary button
- "Нет правил. Добавьте через форму ниже или раскройте..." (truncated)

**🐛 BUG-10**: Page title appears as **"Прав"** instead of "Правила".
At 510 px width the title text cuts off because the view-toggle pill
row to its right occupies most of the line. Either narrower pills,
move pills below title, or shorten title.

**🐛 BUG-11**: Buttons "Импорт..." and "Экспорт..." are cut with
ellipsis indicating truncation. The pill row overflows the line. The
narrow-window rule (A1) requires pills to wrap or move to overflow
menu below ~600 px.

**⚠️ UX-12**: View toggles "Карточки / Список / Текст" lack icons.
The design handoff mentions `▦ Cards · ☰ Read · ✎ Edit` — those icons
should be there to disambiguate at a glance.

**⚠️ UX-13**: "Добавить правило" form uses raw sing-box terms in
dropdowns: "direct" (action) and "domain_suffix" (matcher type).
Tooltips with examples would help non-technical users. E.g.
domain_suffix → "оканчивается на (`.corp.com` matches `app.corp.com`)".

**⚠️ UX-14**: "Нет правил. Добавьте через форму ниже или раскройте…"
text below the form is truncated. Probably ends with "Импорт" or "Текст".
Missing wrap.

### Текст view-mode

Code editor with line-number gutter, syntax-highlighted comments, an
8-line example template, footer "0 правил активно" + "Откатить" + "Применить (0)".

**🐛 BUG-15**: Every comment line in the example is **truncated** at
the right edge of the editor:
- "# Одно правило на строку. Формат: <action> <typ" (cut)
- "# Types: domain / domain_suffix / domain_keywor" (cut)
- "direct ip_cidr 10.0.0.0/8, 192.168.0.0/16     # т" (cut)
- "block geosite ads                                 # с" (cut)
- "!block port 53                                    # о" (cut)

The template the user is being shown is unreadable. Need horizontal
scroll OR text wrapping inside the editor.

**⚠️ UX-16**: Format hint says "Выключить правило: # или ! off в начале
строки" — the word "off" is dangling and confusing. Probably should be
"Выключить правило: префикс # или ! в начале строки." (drop "off").

**💡 SUGGEST-17**: Line-number gutter shows only "1" but there are 8
lines. Either each line should have its own number, or wrapped lines
should share line numbers (e.g. `1`, blank, blank for wrap of line 1).

## Advanced Mode — Настройки → Защита от утечек

4 checkboxes:
- "Строгий режим (быстрая реакция на сбои)" (off)
- "Только IPv4 (защита от IPv6 leak)" (✓)
- "Очищать DNS кэш при подключении" (✓)
- "Строгий DNS (весь DNS через VPN, без утечек)" (off)

**💡 SUGGEST-18**: "Строгий режим" — "быстрая реакция на сбои" is vague.
Tooltip with concrete example: "Если sing-box упал — все защищаемые
приложения сразу теряют коннект до восстановления, без grace period."

**⚠️ UX-19**: "IPv6 leak" mixes English-Russian. Acceptable but could
be just "IPv6-утечек".

**💡 SUGGEST-20**: 2 DNS-related checkboxes are isolated. Group them
under a "DNS" subheader for clarity.

## Advanced Mode — Настройки → Контент

1 checkbox:
- "Блокировать рекламу и трекеры" (off) — subtitle "AdGuard DNS + adblock rule_set (~300K доменов)"

✅ Single, clear toggle. Subtitle gives technical detail. OK.

## Advanced Mode — Настройки → Обновления

1 card "Канал обновлений":
- Checkbox "Получать prerelease обновления (experimental канал)" (✓)

**⚠️ UX-21**: Mix of 2 English words in a single Russian sentence
("prerelease обновления (experimental канал)"). Better: "Получать
тестовые сборки (prerelease)" or "Канал: тестовый / стабильный" radio.

**💡 SUGGEST-22**: No manual "Проверить обновления сейчас" button.
No display of current version or last check time. Add:
- "Текущая версия: 2.30.2"
- "Последняя проверка: 2 минуты назад"
- [Проверить сейчас] button

## Advanced Mode — Серверы tab → "Серверы" sub-tab

Layout: column header (Сервер | IP | Ping | Порт) + 3 rows of manual
VLESS servers + 2 buttons + URL input + add/delete.

**⚠️ UX-23**: Ping column shows `—` for all rows. New users won't
know `Проверить все` is what populates it. Add tooltip on column
header: "Не запускалось — нажми «Проверить все»".

**⚠️ UX-24 (D1 RULE)**: "Deep verify" button label is English in RU UI.
Suggest: "Глубокая проверка" / "Полная проверка".

**⚠️ UX-25**: Footer note "VLESS+Reality маршрутизирует TCP. Для UDP
(игры, QUIC) используйте Custom Config с TUIC или Hysteria2 outbound."
mixes "Custom Config" (English) and "outbound" (English) inside an
otherwise-Russian sentence. Either translate ("свой конфиг с
TUIC/Hysteria2") or italicize the technical terms.

**⚠️ UX-26**: URL placeholder "vless://uuid@server:443?...#name" is
**outdated** since v2.30.1 added multi-protocol support. Should
reflect: "vless:// / hy2:// / tuic:// / ss://...#name".

**💡 SUGGEST-27**: Each row has a small `⌃` icon — meaning unclear.
Probably "show details" or "edit". Use a more universal `ℹ️` or `⋯`.

**💡 SUGGEST-28**: Each row has an `○` (empty circle) on the very
left — placeholder for the active-server green dot. When VPN is
disconnected, this column could collapse to 0 width to give the row
more horizontal space.

## Advanced Mode — Серверы tab → "Свой конфиг (JSON)" sub-tab

Empty state.

- Hint text: "Нажмите на конфиг для активации"
- Buttons: "Добавить конфиг…" + "Удалить" (greyed)

**⚠️ UX-29**: Empty state is too sparse for new users. They see
"Нажмите на конфиг для активации" but there's nothing to click. Add a
hero illustration + plain-language explainer:

> "У тебя пока нет своих конфигов.
> Свой конфиг — это готовый JSON-файл sing-box для нестандартных
> протоколов (TUIC, Hysteria2, Reality+gRPC и др.).
>
> [Добавить конфиг…]   [Что это →]"

**💡 SUGGEST-30**: Hide "Нажмите на конфиг для активации" when the
list is empty (it implies action that is impossible).

## Advanced Mode — Подписка tab

Layout: server list (6 rows aggregated from 1 enabled subscription) +
2 verify buttons + section "Подписки" with one card + add-form.

Columns: Сервер | IP | Ping | Порт.

Server names follow format "${region}-${num} ${port} ${name}", e.g.
"de-01 443 main-brat" — readable.

✅ Click on row triggers reconnect (verified in r3 test).
✅ Green dot moves correctly on click (verified in r3 test).

Subscription card metadata row: `✓ simple https://ninitux.com/api/v1/app/config/...  6s  —  ↻ ✕`

**⚠️ UX-32**: Same Ping column with `—` (UX-23 applies).

**⚠️ UX-33**: Subscription card metadata "6s" and "—" have NO labels.
What does "6s" mean? Last refresh time? Polling interval? And what is
"—"? Server count? Need column headers or tooltips.

**⚠️ UX-34**: URL input placeholder "URL подписки (subscription link)"
duplicates an English-Russian translation in a single placeholder.
Pick one: "URL подписки" (RU only) or "subscription link" (EN only).

**💡 SUGGEST-35**: Column header just says "Ping" — clarify unit:
"Ping (мс)" or just "мс".

## Advanced Mode — Приложения tab

State: Full Tunnel mode active.

- Yellow alert at top: "Активен Full-tunnel — выбор приложений
  игнорируется, весь трафик идёт через VPN." + button
  "Переключить на Split tunnel"
- Sidebar (categories with counts):
  Discord (1) / Messengers (3) / AI_Tools (3) / Браузеры (23) /
  Работа (6) / Streaming (2) / Gaming (3) / Virtualization (9) /
  Privacy_Shell (5) / Свои
- Right pane: "← Выберите категорию"

**🐛 BUG-36**: While Full Tunnel is active, the category list in the
sidebar is **visually still styled as clickable** but **all clicks are
silently ignored**. Tested clicking Discord, Browsers, AI_Tools — none
selects. The yellow banner explains the situation in text, but the
sidebar lacks visual disabling (no `cursor: not-allowed`, no
greyed-out colour, no inert state). Either gray out the sidebar OR
allow clicks in preview-only mode with toggle disabled.

**⚠️ UX-37**: Category names mix EN/RU within a single list:
- English: Discord, Messengers, AI_Tools, Streaming, Gaming,
  Virtualization, Privacy_Shell
- Russian: Браузеры, Работа, Свои

Should follow user's selected locale uniformly.

**⚠️ UX-38**: Snake_case names (`AI_Tools`, `Privacy_Shell`) are
profile JSON keys leaking into the UI. Display titles should be
"AI инструменты" / "Приватность" or pretty-cased.

**⚠️ UX-39**: With Full Tunnel banner suppressing interaction, the
right pane shows the static text "← Выберите категорию" and there's
nothing to do. Could show a more contextual message: "В режиме Full
Tunnel список приложений не используется. Чтобы включить его —
переключитесь на Split Tunnel".

**💡 SUGGEST-40**: Category "Свои" has no count number while all
others do. Display "0" for consistency.

After switching to Split Tunnel:

- Sidebar enabled, AI_Tools selected
- Header "AI_Tools"
- Checkbox "Включить всю группу" (✓)
- 3 app rows with checkboxes: claude.exe / ChatGPT.exe / Cursor.exe
- "✓ Применить изменения" button (greyed)
- Input "имя процесса (например Discord)" + button "+ Add"

**⚠️ UX-41 (D1 RULE)**: "+ Add" button is English. Consistency: every
other list-add button in the app uses "+ Добавить".

**⚠️ UX-42**: Same auto-save vs apply confusion as UX-8 — "Применить
изменения" button visible but greyed and footer says auto-save. When
do users click it?

**💡 SUGGEST-43**: App rows show only `.exe` filename. Could enrich
with Windows Shell icon (`SHGetFileInfo` or similar) for visual
identification.

## Advanced Mode — Инструменты tab → Zapret sub-tab

Sidebar: Статус / Стратегия / Hosts / Фильтры / Обновления /
Диагностика / Дополнительно.

### Статус

- Description text
- "● Остановлен" status card
- Yellow warning "▲ Только Windows. Можно использовать без VPN и вместе с VPN."
- Big "Запустить обход DPI" button

**⚠️ UX-44**: "(zapret от Flowseal)" is a GitHub username — most users
don't know who Flowseal is. Either drop the parenthetical or expand
to "@Flowseal/zapret-discord-youtube".

**💡 SUGGEST-45**: Combine the "● Остановлен" status card with the
yellow Windows-only warning into a single status row to save vertical
space.

**⚠️ UX-46 (consistency)**: Sub-tab named "TgProxy" but elsewhere
the same feature is called "Telegram-прокси" (Simple mode hint).
Standardize.

### Стратегия

- Header "Стратегия" + form label "Стратегия" (duplicated)
- Dropdown "multisplit"
- Version "1.9.7b" + Обновить button

**⚠️ UX-47**: Page header "Стратегия" + immediately-below form label
"Стратегия" are visually redundant.

**💡 SUGGEST-48**: Dropdown options like "multisplit" lack
descriptions. Add subtitle/tooltip explaining tradeoffs.

**💡 SUGGEST-49**: Update button shows version but no info on
whether update is available. Add "Актуальна" / "Доступна 1.9.8".

### Hosts

- Two big buttons: "Добавить Discord hosts" / "Добавить Flowseal hosts"
  with descriptive subtitles.

**⚠️ UX-50**: "Discord hosts" / "Flowseal hosts" — "hosts" is OK as
technical term but capitalization differs ("Discord" capitalized,
"Flowseal" capitalized — both proper nouns, consistent here).

### Фильтры

Two dropdowns:
- "Игровой фильтр (диапазон 1024-65535)" → "Выкл"
- "IPSet фильтр" → "None (отключено)"

**⚠️ UX-51**: Inconsistent off-state copy: "Выкл" vs "None (отключено)".
Standardize: both say "Выкл" or both say "Отключено".

### Обновления (Zapret)

- Button "Обновить IPSet список" (greyed when nothing to update?)
- Checkbox "Авто-проверка обновлений zapret" (✓)

**⚠️ UX-52 (consistency)**: Lower-case "zapret" here vs upper-case
"Zapret" in sub-tab name. Pick one (probably "zapret" since it's the
binary name, but UI labels should follow case convention).

### Диагностика

- Single button "Запустить диагностику" (greyed)

✅ Minimal, OK.

### Дополнительно

Buttons (all greyed when Zapret stopped):
- "Очистить кэш Discord"
- "Запустить тесты сети"
- "Удалить службу zapret"
- "Открыть меню service.bat"
- "Открыть папку" + "GitHub" link "Flowseal/zapret-discord-youtube"

**⚠️ UX-53**: All these advanced actions are silently disabled. No
hint as to why. Should display "Запустите Zapret чтобы использовать"
above the button group.

**⚠️ UX-54 (DESTRUCTIVE)**: "Удалить службу zapret" looks identical
to other buttons. Destructive actions should have red border / danger
state to prevent misclicks.

**💡 SUGGEST-55**: GitHub link is great — gives upstream source. ✅

## Advanced Mode — Инструменты tab → TgProxy sub-tab

- Description: "MTProto прокси для обхода блокировки Telegram. Работает
  локально, трафик идёт напрямую к серверам Telegram через WebSocket."
- "Порт:" → "1443"
- "Secret:" → "автоген" + Копировать + Новый buttons
- "Не установлен" + "Скачать" button
- "● Остановлен" status row
- "Открыть в Telegram" button + hint
- "Открыть папку" + "GitHub Flowseal/tg-ws-proxy"
- Path hint: "Настройка: Telegram → Настройки → Продвинутые → Тип
  соединения → MTProto Proxy"
- Big "Запустить Telegram Proxy" button at bottom

**⚠️ UX-56 (D1 RULE)**: "После этого просто Start/Stop" — English
inside Russian sentence. "После этого просто Запуск/Остановка."

**⚠️ UX-57**: TgProxy also has the dual-state confusion: "Не установлен"
+ "Остановлен" both shown simultaneously. New users see two negative
states and don't know which to fix first. Reduce to single canonical
state: "Не установлен — нажмите Скачать".

**💡 SUGGEST-58**: "Открыть в Telegram" — adds tooltip "Откроет
tg://-ссылку в Telegram-клиенте с настройками прокси".

## Advanced Mode — Free Configs tab

Sub-tabs: ▶ Поиск / ★ Сохранённые (46).

### Сохранённые tab

- Subtitle "Конфиги, найденные в прошлых поисках. Они могут пе…" (truncated)
- Buttons: "↻ Перепроверить (46)" / "✕ Удалить всё"
- Columns: Страна 🇸🇪/🇭🇰/🇳🇱… | Адрес | (ping pill) | Скорость | Статус | actions
- "Обновлено 1d ago"
- Hint + Подключить (greyed)

**🐛 BUG-59**: Top-level tab "Серверы" appears as **"ерверы"** with
the leading "С" cut off when Free Configs tab is selected. The tab
strip doesn't horizontally scroll/snap correctly for the leftmost tab.
At narrow window width the tab row truncates the LEFT edge instead of
overflowing on the right.

**⚠️ UX-60**: Subtitle "Конфиги, найденные в прошлых поисках. Они
могут пе…" truncated. Probably "перестать работать". Wrap text.

**🐛 BUG-61**: "Обновлено 1d ago" mixes RU "Обновлено" with EN "1d ago".
Either:
- "Обновлено 1д назад" (matches the row Status column "Х дней назад")
- or just "Updated 1d ago"

**✅ UX-62**: Country flag emojis render correctly with Twemoji.

**⚠️ UX-63**: Some rows have "—" in Скорость column. Means "not
measured" but no tooltip explaining.

**💡 SUGGEST-64**: Hint "Выберите строку ↑ и нажмите «Подключить»…"
is subtle grey text. Could be a visual badge near the Подключить
button to make it more discoverable.

### Поиск tab

- Green hero card with title + description + button "✓✓ Найти рабочие
  конфиги" + dropdown "▼ Настройки"
- Below: "Конфиги  0 показано"
- Empty state "Нажмите кнопку выше, чтобы найти конфиги."

**⚠️ UX-65**: Section title "✓✓ Найти рабочие конфиги" (with double
checkmarks) appears TWICE — once as header, once as button label.
Visually duplicative.

**⚠️ UX-66**: "Найдёт N рабочих с пингом ниже порога" — literal `N`
placeholder leaking into the UI. Should reference actual setting:
"Найдёт до 10 рабочих с пингом ниже 200 мс" (or whatever the values
are).

**💡 SUGGEST-67**: "▼ Настройки" dropdown is hidden by default —
useful but discoverable only by clicking. Could show a one-line
summary: "Настройки · до 10 шт., пинг < 200 мс".

## Header — "..." menu

Sections:
- "Вид" — Светлая/Тёмная theme + RU/EN language
- "Диагностика" — Открыть логи / Проверить утечку IP / Проверить
  обновления
- "Устранение неполадок" — Проверить состояние / Перезапустить в Safe
  Mode / Сбросить настройки
- "О приложении" + version "v2.30.2"
- "◄ Simple" — toggle Simple/Advanced mode

**✅** Diagnostic actions are well-organized in one place.

**⚠️ UX-68 (D1 RULE)**: "Перезапустить в Safe Mode" — "Safe Mode" in
EN. Should be "в безопасном режиме".

**⚠️ UX-69 (DESTRUCTIVE)**: "Сбросить настройки" — destructive but
visually neutral. Add danger styling and require confirmation dialog
before applying.

**💡 SUGGEST-70**: Version "v2.30.2" is grey small text. Could expand
to a 2-line block: "v2.30.2 · обновлено сейчас · [Проверить]".

**⚠️ UX-71 (DUPLICATION)**: "Проверить обновления" exists in this
menu AND in Настройки → Обновления tab. Pick one canonical place. The
menu item is good for quick access; the Settings tab adds nothing
unique now.

**💡 SUGGEST-72**: "Проверить утечку IP" — what does it do? Opens
browser? Tooltip "Откроет 2ip.ru / ipleak.net в браузере" would
clarify.

---

# Summary statistics

- **🐛 BUG findings**: 7 (truly broken behavior — UI mismatch, layout
  overflow, English-Russian mixing in single sentence, leftmost tab
  truncation, comment-line cutoff in editor)
- **⚠️ UX findings**: 35 (confusing/inconsistent — duplicate labels,
  truncated text, EN/RU mix violating D1 rule, undiscovered features,
  ambiguous states, etc.)
- **💡 SUGGEST findings**: ~30 (improvements, not regressions)

---

# Top priority fixes (P0/P1 candidates for v2.30.3 / v2.31)

Sorted by user-visible impact:

| Pri | Finding | Impact | Effort |
|---|---|---|---|
| P0 | BUG-7 footer overlap (Применить сейчас covers auto-save text) | Looks broken on every Settings page | Small (CheckBox + button wrap @ <620px) |
| P0 | BUG-15 text-mode editor truncates every comment line | Users can't read template they're shown | Small (enable horizontal scroll OR wrap) |
| P0 | BUG-59 first tab "Серверы" cut to "ерверы" when Free selected | Tab strip layout broken at narrow widths | Medium (TabControl scroll behaviour) |
| P1 | BUG-10/11 "Прав" + Импорт/Экспорт truncation | Page title looks broken | Small (move pills below title at narrow width) |
| P1 | BUG-36 Apps sidebar silently disabled in Full Tunnel | User clicks 5x and gets nothing — no feedback | Small (apply IsEnabled binding to whole sidebar list) |
| P1 | UX-1 product-name mismatch ("Virtual Penguin Network" vs "VPNRouter") | Tooling and search by title fails | Medium (decide canonical name + WindowTitle binding) |
| P1 | UX-9 Split/Full Tunnel English in RU UI (D1 rule) | Established convention violation | Small (Strings localization) |
| P1 | UX-69 + UX-54 destructive actions visually neutral | Risk of accidental data loss | Small (add Danger style) |
| P2 | UX-4 Simple-mode Автозапуск card jumps to Advanced | Counterintuitive | Medium (inline disclosure) |
| P2 | UX-8 + UX-42 auto-save vs Применить ambiguity | Settings UX confusion | Small (clarify copy) |
| P2 | UX-71 update-check duplicated (menu + Settings tab) | Two paths, one source-of-truth confusion | Small (remove from one) |
| P2 | UX-37/38 Apps category names EN/RU mix + snake_case | Polish | Medium (Profile JSON → display title map) |
| P2 | UX-65/66 Free Configs literal "N" + duplicated header | Polish | Small (string fix) |
| P3 | All other ⚠️ UX + 💡 SUGGEST findings | Polish / clarity | Various |

---

# Methodology + repro

Audit performed by Claude using the new in-tree
`mcp__vpnrouter-test__*` MCP server (committed in 84ac7fa). All
screenshots taken via `screenshot` tool, all clicks via `mouse_click`,
all observations live in-app on v2.30.2 stable. Total session: ~30
minutes from connect to fully-walked + documented.

This is the **first comprehensive UX audit** of VPNRouter from a
software-as-an-app perspective rather than from a "is this code
correct" perspective. Most findings are not bugs in functionality
but mismatches between expected UX patterns and current copy/layout.

# Cross-refs

- `tools/VpnRouterTestMcp/` — MCP server used to drive this audit
- `plans/computer-use-mcp-setup.md` — MCP setup
- `VPNRouter.App/CLAUDE.md` — D1 rule (no English in RU UI), F1 (state
  sync rules), A1-A4 (narrow-window adaptation rules)
- `Localization/Strings.cs` — bilingual strings backing
- `Styles/Tokens.axaml` — design tokens


