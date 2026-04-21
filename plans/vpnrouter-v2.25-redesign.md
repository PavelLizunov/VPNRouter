# VPNRouter v2.25 — Redesign по Claude Design handoff

**Source design**: `design/` (скопирован из https://api.anthropic.com/v1/design/h/fvX4KbCBREFp9d-PXhApGA).
**Base version**: `v2.24.4` (полный self-healing + apt-repo + polkit passwordless).
**Target**: `v2.25.0` → `v2.25.N` (phased rollout).

**Platform requirement**: работать одинаково на Windows / macOS / Linux. Никаких platform-specific хаков в новых страницах — только если существующая имплементация уже имеет `#if PLATFORM_WINDOWS` / `OperatingSystem.IsLinux()` branch.

---

## Как после compact продолжить

1. Прочитать этот файл полностью.
2. Прочитать `design/project/AdvancedMode.html` (чистовик, на что ориентируемся).
3. Прочитать `design/project/SimpleMode.html` + `design/project/UIKit.html` (для сравнения паттернов).
4. Прочитать `design/project/tokens.css` (палитра + типографика + радиусы). Сверить с
   текущим `VPNRouter.App/Styles/Tokens.axaml`.
5. Прочитать `design/chats/chat1.md` (transcript — показывает что пользователь хотел
   и на чём остановился).
6. Текущая версия в `VPNRouter.Core/AppVersion.cs` — отсюда bump на `2.25.0`.

Никакого URL-fetch заново — всё уже в репо.

---

## Что меняется (общий обзор)

Дизайнер перевёрстывал **7 Advanced-страниц + меню ⋯** по единой дизайн-системе. Плюс унифицировал header и footer между Simple и Advanced. Работа делалась от кода **v2.21.7** — значит он **не видел**:

- Self-healing пункты меню (Safe Mode, Reset config, Run Health Check) — они наши v2.23-v2.24.1
- apt-repo workflow и polkit policy (v2.22.2, v2.22.4) — серверная инфраструктура, UI не трогается
- sing-box 1.13.3 в subheader — уже на дизайне показан `sing-box 1.13.3`

Значит при имплементации дизайн-пункты совмещаем с тем что уже есть в UI сверх `v2.21.7`.

### Глобальные изменения (применяются к каждой странице)

1. **Header**: `[28px лого] [имя + мини-badges] [◂ Simple] [⋯]` — одна строка. Версия / by NiniTux / sing-box version уходят в `О приложении`.
2. **Mini-badges VPN / Zapret / TG**: кнопки с `data-route`. Визуально — статусные пилюли (`on`=arctic/green, `off`=grey, `warn`=amber с пульсирующей точкой). Клик → навигация в соответствующий таб.
3. **Footer**: `● Подключено [mode/split] → server · ip       [Stop]` — одна строка. Убрать двойной `Автозапуск с Windows` + гигантская `Остановить VPN` на всю ширину.
4. **Scrollable tabbar**: горизонтальный overflow вместо обрезки `Free`.
5. **Selection color везде `--accent-bg-subtle`** (arctic tint), не teal и не green.
6. **Primary CTA везде Arctic** (`--accent-solid`). Никаких фиолетовых / ярко-зелёных кнопок — эти цвета остаются для семантических состояний (success / warning), не для UI chrome.

### Per-page изменения

| # | Страница | Изменение |
|---|---|---|
| 1 | **Меню ⋯** | Popover: сегменты Light/Dark + RU/EN, divider, "Диагностика" section, Advanced CTA внизу. **Добавить** наши Safe Mode / Reset / Run Health Check в секцию Troubleshooting. |
| 2 | **Servers** | Header-row для колонок ("Сервер · IP · Ping · Port"). Selection arctic. Tooltips вместо inline-подсказок. |
| 3 | **Subscribe** | Такой же header-row. "—" = iconное состояние недоступности. Карточка подписки как список-строка. |
| 4 | **Settings → Routing** | Side-nav слева (Маршрутизация · Защита от утечек · Контент · Обновления · Автозапуск). Radio-группа вертикально. Checkboxed карточка для `.ru` bypass. Apply — компактная кнопка. |
| 5 | **Applications** | Split (120px категории + 1fr content). Счётчики моно-числа справа, не pill-badges. |
| 6 | **Tools → TgProxy** | Sub-tabs (Zapret / TgProxy). `Остановить Telegram Proxy` — secondary (`.btn`), не primary. Banner для статуса. |
| 7 | **Tools → Zapret** | Side-nav (Статус · Стратегия · Hosts · Фильтры · Обновления · Диагностика · Дополнительно). `Запустить обход DPI` — arctic primary (не violet). |
| 8 | **Free Configs** | Side-nav + stats grid 5 колонок + dashed empty state с CTA. Пустое состояние чистое, без overlap. |

---

## Дизайн-токены (из `design/project/tokens.css`)

Цвета верифицированы из реального XAML на моменте снятия дизайна:

- **Primary**: `#2563EB` (blue) — но дизайнер маппит на Arctic `--accent-solid`
- **Success**: `#22C55E`, `#059669`, `#16A34A`, `#065F46`, `#166534`
- **Warning**: `#F59E0B`, `#FEF3C7`, `#92400E`
- **Danger**: `#EF4444`, `#FEE2E2`, `#991B1B`
- **Accent alt (Purple)**: `#7C3AED`, `#A78BFA` (намеренно используется в 3 местах: Deep Verify buttons, Zapret buttons)
- **Neutrals**: `#94A3B8` + набор серых
- **Emerald tints**: `#A7F3D0`, `#DCFCE7`, `#ECFDF5`
- **Orange tints**: `#FFEDD5`, `#9A3412`, `#78350F`

**Type scale**: 8 / 9 / 10 / 11 / 12 / 13 / 14 px.
**Radii**: 3 / 4 / 5 / 6 / 8 / 10 (pill).
**Spacing**: 2 / 4 / 6 / 8 / 10 / 12.

У нас уже есть **`VPNRouter.App/Styles/Tokens.axaml`** (v2.16.0 Arctic theme). Подавляющее большинство совпадает — **сверить, не переписывать**. Добавить только то что дизайн вводит нового (pill radius 10, специфичные emerald/orange tints для статусов).

---

## План имплементации по релизам

### v2.25.0 — глобальный header/footer/tabbar (3-4 часа)

Трогаем ВСЕ страницы одним махом. Это 30% визуального wow-эффекта за минимум работы.

**Файлы**:
- `VPNRouter.App/Views/MainWindow.axaml` — header + footer + tabbar
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs` — bindings для badges
- `VPNRouter.App/Styles/Tokens.axaml` — пилюли, мини-badge styling

**Steps**:
1. Header сжать до одной строки: logo 28×28, brand-block с name + badges, `◂ Simple` ссылка, `⋯` меню.
2. Убрать версию / by / sing-box version. Перенести в About dialog (создать если нет — или в меню ⋯ → "О приложении" → popup).
3. Mini-badges: `VpnBadgeCommand`, `ZapretBadgeCommand`, `TgProxyBadgeCommand` — переключают `SelectedTabIndex` на соответствующий таб.
4. Badge visual state: `IsVpnRunning` / `IsZapretRunning` / `IsTgProxyRunning` + опционально `HasWarning` для pulse-animation.
5. Footer: компактный statusbar + маленькая Stop-кнопка справа. Удалить текущий `Автозапуск с Windows` чекбокс на этой строке (переехал в Settings → Автозапуск, если его ещё нет в Settings — создать).
6. Tabbar: добавить `ScrollViewer HorizontalScrollBarVisibility="Hidden" HorizontalAlignment="Left"` вокруг ListBox.
7. Selection color списков: все `SelectedItem` highlight — через `AccentBgSubtleBrush` (уже в Tokens.axaml).

**Acceptance**:
- Advanced mode header — одна строка, в ней badges, видно статус VPN/Zapret/TG.
- Клик по badge → соответствующий таб открывается.
- Stop-кнопка в footer компактная, не на всю ширину.
- `Free Configs` виден в tabbar даже на узком окне (520px).
- Cross-platform: собрать + запустить на Windows и Linux apt. macOS CI пройдёт.

### v2.25.1 — Tokens.axaml сверка (1-2 часа)

Диф `design/project/tokens.css` ↔ `VPNRouter.App/Styles/Tokens.axaml`. Добавить недостающее.

**Steps**:
1. Распарсить tokens.css (`:root { --surface-base: ...; ... }`).
2. Сверить каждую переменную с Tokens.axaml (`<Color x:Key="SurfaceBase">...</Color>`).
3. Добавить отсутствующие (skip platform-неприменимые типа `--font-family-mac`).
4. Прогнать Grep на hardcoded hex'ы в `.axaml` файлах — перевести на `DynamicResource`.

### v2.25.2 — Popover ⋯ menu (2-3 часа)

Переверстать MenuFlyout под `design/project/AdvancedMode.html` секция **1 · Меню ⋯**.

**Steps**:
1. Сегменты Light/Dark + RU/EN (segmented control вместо отдельных MenuItem'ов).
2. Divider + `section-label` "Диагностика".
3. Punte `Open logs / Check leaks / Check updates`.
4. Divider + `О приложении` (c версией справа) + `Перейти в Advanced` как primary CTA.
5. **Добавить** наши v2.23-v2.24.1 пункты под отдельным divider: "Troubleshooting" section с Run Health Check / Restart in Safe Mode / Reset config to defaults.

### v2.25.3 — ServersPage + SubscribePage (2-3 часа)

**ServersPage.axaml**:
1. `srv-head` (grid with column labels).
2. Server rows: grid `14px 1fr 100px 40px 40px 24px`, mono font для `.name` / `.host` / `.p`.
3. Active server: arctic tint background + bold name + filled radio indicator (зелёная точка внутри).
4. Buttons row: `⚡ Проверить все` (success) + `🔍 Deep verify` (primary arctic).
5. Input для VLESS URI с mono font.
6. Row из `Удалить` (ghost) + `+ Добавить сервер(ы)` (primary).

**SubscribePage.axaml**:
- Same shape + подписка-карточка в стиле serv-list row (внизу).
- "—" как grey dash iconically рендерится для недоступных ping/port.

### v2.25.4 — Settings (NetworkPage) с side-nav (2 часа)

`NetworkPage.axaml` переделывается в split 140 + 1fr:

1. Left side-nav: Маршрутизация, Защита от утечек, Контент, Обновления, Автозапуск.
2. Right pane: контент выбранной секции.
3. Маршрутизация: radio-cards (Split Tunnel / Full Tunnel) + checkbox card (`.ru` bypass).
4. Apply компактная кнопка (не full-width).
5. Автосохранение indicator слева от Apply.

### v2.25.5 — ApplicationsPage (2 часа)

Split 120 + 1fr:

1. Категории слева, моно-числа справа от имени (не pill badges).
2. Active категория — arctic tint.
3. `+ Новая категория` inline под списком.
4. Right pane: title группы, "Включить всю группу" checkbox, список приложений, inline add.

### v2.25.6 — ToolsPage (Zapret + TgProxy) (2 часа)

Sub-tabs (Zapret / TgProxy) + side-nav для Zapret:

**Zapret**: side-nav (Статус · Стратегия · Hosts · Фильтры · Обновления · Диагностика · Дополнительно), `Запустить обход DPI` arctic primary.

**TgProxy**: описание + Port input + Secret input с copy/new buttons + info banner + row of buttons (Open in Telegram / Open folder / GitHub) + note + secondary Stop button внизу.

### v2.25.7 — FreeConfigsPage (2 часа)

Side-nav (Обзор · Скан · Deep verify · Фильтры · Мои источники · Очистка) + contents:

1. Banner "Как начать" с close (x).
2. Stats grid 5 колонок (Всего · Провер. · Работ. · Подозр. · Недост.) — один stat `.hl` (arctic highlight).
3. Input для фильтра.
4. Dashed empty state с `☁` icon + CTA `↓ Обновить список`.
5. Footer line `Обновлено 1d ago` моно серый мелкий.

### v2.25.8 — Polish (1-2 часа)

1. Pulse-анимация для `warn` badges (Storyboard/Animate через Avalonia Transitions).
2. Hover effects на всех кнопках / badges / rows — transitions 220ms ease-out.
3. Финальная сверка `design/project/UIKit.html` — если пропустили детали.

---

## Cross-platform считаны

**Что имеет platform-specific в UI**:

- **Windows vs Linux icon**: Window.Icon уже branching'ится (penguin_mascot vs penguin_mascot_tile). Не трогать.
- **Tools tab**: на Linux скрыт (Zapret Windows-only). Существующий `IsZapretAvailable` binding сохранить.
- **Fonts**: `SF Mono` / `Cascadia Code` / `Ubuntu Mono` — Avalonia auto-resolves по OS если в FontFamily указать fallback stack.
- **Шрифты иконок**: использовать встроенные `▶` `⋯` `↻` символы (они кроссплатформенные в Avalonia 11), избегать emoji за пределами ASCII.

**CI проверки после каждой фазы**:
- Windows build (`dotnet build VPNRouter.sln` локально + `build.ps1`)
- Linux CI (`build-linux.yml` через tag push)
- Mac CI (`build-mac.yml` через tag push)

Если macOS build упал с hdiutil — retry уже встроен в build-mac.sh (v2.22.1).

---

## Риски и trade-offs

1. **Header: удаление версии + by NiniTux**. Пользователь может заметить пропажу. Решение: первый запуск после v2.25.0 → показать MessageBox / notification "Redesign shipped — детали в меню → О приложении". Или просто note в release.
2. **Удаление автозапуска из footer**. Если в Settings → Автозапуск не было checkbox'а — добавить. Иначе пользователь потеряет доступ. **Проверить перед имплементацией**.
3. **Scrollable tabbar**: на широких мониторах окно VPNRouter остаётся 520×640 (фиксированный). На узких мониторах раньше обрезалось — сейчас вложено в ScrollViewer.
4. **tokens.css может предлагать значения, которых нет в Tokens.axaml** (например `shadow-lg` с более глубокой тенью). Если добавление нарушает baseline theme — откатить конкретный token, не весь PR.

---

## Status tracker

### v2.25.0 — глобальный header/footer/tabbar
- [x] Header compact (logo 28px + brand + Simple + ⋯) — 2026-04-21
- [x] Mini-badges VPN/Zapret/TG (status + click-nav) — уже были в v2.24.x, перенесены как есть
- [x] Footer unified (status + compact Stop) — 2026-04-21
- [x] Scrollable tabbar (no Free clipping) — 2026-04-21 (ScrollViewer wrap)
- [x] About dialog с версией / by / sing-box version — 2026-04-21 (AboutWindow.axaml + OpenAboutCommand)
- [x] Автозапуск перенесён в Settings (если ещё не) — уже в NetworkPage → Autostart section
- [ ] Service Restart/Reinstall кнопки перенести в Settings → Autostart (отложено в v2.25.4 — см. NetworkPage redesign)

### v2.25.1 — tokens reconciliation
- [ ] Diff tokens.css vs Tokens.axaml
- [ ] Добавить недостающие
- [ ] Convert hardcoded hex in axaml files

### v2.25.2 — Menu ⋯ popover
- [x] Light/Dark + RU/EN segments — 2026-04-21 (SetTheme{Light,Dark} + SetLanguage{Russian,English} commands, `Classes.active="{Binding !IsDarkTheme}"`-driven highlight)
- [x] Divider + Diagnostics section — 2026-04-21 (section-label TextBlock + menu-divider Border)
- [x] Troubleshooting section (Health Check / Safe Mode / Reset) — 2026-04-21 (new section between Diagnostics and About)
- [x] About menu item — 2026-04-21 (with AppVersionShortText pill on the right)
- [x] Advanced CTA bottom — 2026-04-21 (`Classes="primary-cta"` arctic-subtle bg at the end of the popover)

### v2.25.3 — Servers + Subscribe
- [x] srv-head column labels — 2026-04-21 (Сервер | IP | Ping | Port каmargin outside list)
- [x] Mono font для host/name — 2026-04-21 (`Consolas, 'SF Mono', 'Cascadia Code', 'Ubuntu Mono'` stack)
- [x] Active arctic tint — 2026-04-21 (`Border.srv-row.active → AccentBgSubtle`, name → bold arctic fg)
- [x] Deep verify / Проверить все buttons — 2026-04-21 (Deep verify: #7C3AED → AccentSolid arctic, Test all stays Success green)
- [x] Subscribe page как список + карточка подписки — 2026-04-21 (same srv-row template for both server pool AND subscriptions card list)
- [x] ServerViewModel.HostSubtitle — NEW, builds `tcp + reality` string for the design's subtitle line

### v2.25.4 — Settings side-nav
- [x] Split layout — 2026-04-21 (160 → 140 px left nav, matches design)
- [x] Radio cards для routing — 2026-04-21 (Split / Full Tunnel как full-width radio-cards с title + subtitle, active → arctic border + arctic-bg-subtle fill)
- [x] `.ru` bypass checkbox card — 2026-04-21 (`Border.checkbox-card.active` тот же arctic highlight; Block ads card тоже получил новый стиль)
- [x] Apply compact button — 2026-04-21 (right-aligned 10,5 padding, left = ✓ Autosave hint)
- [x] Service Restart/Reinstall кнопки перенесены в Settings → Autostart — 2026-04-21 (закрывает перенос из v2.25.0 footer, с status-pill Running/Stopped)

### v2.25.5 — Applications
- [x] Split 120 + 1fr — 2026-04-21 (160 → 120 px left nav, tighter per design)
- [x] Mono counters — 2026-04-21 (pill-badge solid-arctic убрана, теперь plain mono muted число справа)
- [x] Arctic active — 2026-04-21 (`ListBox.cat-list ListBoxItem:selected` → Surface-base bg + AccentFg name + bold)
- [x] `+ Новая категория` inline — 2026-04-21 (было уже в нижней части левого nav — оставлен паттерн, только выправлены отступы)
- [x] App rows chip-style — 2026-04-21 (`Border.app-row` = SurfaceSunken + RadiusSm padding вместо плоской ListBox-строки)

### v2.25.6 — Tools (Zapret + TgProxy)
- [x] Sub-tabs — 2026-04-21 (ToolsPage уже был готов — ListBox-стрип Zapret/TgProxy)
- [x] Zapret side-nav + arctic primary — 2026-04-21 (side-nav уже был, `Запустить обход DPI` button violet → arctic, progress bar violet → arctic, GitHub link violet → AccentFg, warning banner вместо italic muted text, active side-nav item → Surface-base bg + arctic fg)
- [x] TgProxy compact layout + secondary Stop — 2026-04-21 (primary arctic → secondary SurfaceBase+border+TextPrimary, right-aligned, compact padding)

### v2.25.7 — Free Configs
- [ ] Side-nav
- [ ] Stats grid 5
- [ ] Dashed empty state

### v2.25.8 — polish
- [ ] Pulse animation for warn badges
- [ ] Hover transitions
- [ ] UIKit.html cross-check

---

## Files to read after compact

- `plans/vpnrouter-v2.25-redesign.md` — этот файл (план)
- `design/project/AdvancedMode.html` — главный reference для Advanced (675 lines)
- `design/project/SimpleMode.html` — reference для Simple
- `design/project/UIKit.html` — 1:1 MainWindow recreation with tweakable states
- `design/project/tokens.css` — дизайн-токены
- `design/chats/chat1.md` — transcript разговора с дизайнером (интент)
- `VPNRouter.App/Styles/Tokens.axaml` — наши текущие tokens
- `VPNRouter.App/Views/MainWindow.axaml` — текущий layout
- `VPNRouter.App/Views/Pages/*.axaml` — текущие страницы
- `VPNRouter.Core/AppVersion.cs` — bump отсюда на 2.25.0
