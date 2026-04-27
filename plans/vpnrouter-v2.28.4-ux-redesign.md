# v2.28.4-r1 — UX redesign batch

Триггер: user feedback от 2026-04-27 после серии итераций -r1..-r6 на v2.28.3.

## Сводка проблем

| # | Файл | Симптом | Статус -r6 |
|---|------|---------|------------|
| 1 | `NetworkPage.axaml` Leak Protection | Текст вылазиет за экран | НЕ ПОЧИНЕН (TextWrapping="Wrap" обёртка не помогла) |
| 2 | `DpiBypassPage.axaml` | Overflow нет, но стиль не соответствует дизайн-системе | Не починен |
| 3 | `ApplicationsPage.axaml` | Overflow нет, всё умещается, но стиль может расходиться с дизайном | Не починен |
| 4 | `FreeConfigsPage.axaml` | Текущий 6-section layout (вернули в -r6) — не то что хотел user | Сделать Simple-страницу с green Deep-verify card стилем |

---

## Item 1 — NetworkPage Leak Protection overflow (P0)

### Что я уже сделал в -r1 (не помогло)

```xaml
<!-- BEFORE -->
<CheckBox IsChecked="{Binding StrictMode}"
          Content="{Binding StrictModeLabel}"
          ToolTip.Tip="{Binding L_TipLeakStrictMode}"
          MinHeight="0" Padding="4,0"/>

<!-- AFTER -->
<CheckBox IsChecked="{Binding StrictMode}"
          ToolTip.Tip="{Binding L_TipLeakStrictMode}"
          MinHeight="0" Padding="4,0">
    <TextBlock Text="{Binding StrictModeLabel}" TextWrapping="Wrap"/>
</CheckBox>
```

User repro: «не помогло ошибка осталаось». Значит TextWrapping не справился с layout'ом — вероятно проблема выше по дереву (родительский Border / Grid не отдаёт ширину дочернему TextBlock).

### Гипотезы корневой причины

**A.** `<Border Padding="8,6" ...>` без явного `MaxWidth` — растёт по контенту, не сжимается под ScrollViewer.

**B.** `<StackPanel Spacing="4">` внутри Border — горизонтально не ограничен, тянет за собой содержимое.

**C.** CheckBox internal template имеет фиксированный `MinWidth` для content presenter — TextBlock внутри не получает родительскую ширину как constraint.

**D.** ScrollViewer parent имеет `HorizontalScrollBarVisibility="Disabled"` но фактически контент шире — возможно нужен `MaxWidth` на StackPanel inside.

### План фикса

1. **Inspect actual rendered tree** через Avalonia DevTools (или статически по XAML) — найти кто именно растягивает.
2. Прокинуть `HorizontalAlignment="Stretch"` + `MaxWidth` на StackPanel внутри Leak Protection Border.
3. Как fallback — заменить CheckBox.Content разметку на Grid с явной ColumnDefinitions, как в `radio-card` pattern: `ColumnDefinitions="24,*"` где 24 это сам checkbox + остальное на TextBlock с TextWrapping.

### Acceptance

User открывает Network → Leak Protection на узком окне (~500px) → 4 чекбокса (Strict mode, IPv4-only, Flush DNS, Strict DNS) wrap'ят текст без horizontal overflow.

---

## Item 2 — DpiBypassPage стилевое соответствие дизайну (P1)

User: «там особо проблем небыло, но там стиль не соответвукет дизайну».

### План

1. Открыть `DpiBypassPage.axaml`, сравнить с дизайн-карточками из `AdvancedMode.html` (Tools tab).
2. Привести в соответствие токены: `SurfaceSunkenBrush` для grouped panels, `RadiusSm` для cards, `BorderDefaultBrush` 1px, FontSize ladder из tokens (11/12/13).
3. Кнопки → `AccentSolidBrush` для primary, `SurfaceBaseBrush + BorderDefaultBrush` для secondary.

---

## Item 3 — ApplicationsPage style audit (P2)

User: «Проблемы нет, все работает и умещается идеально, ПРОВЕРИТЬ СТИЛЬ».

### План

1. Сравнить app group cards с `AdvancedMode.html` Apps tab.
2. Verify: `RadiusSm`/`RadiusMd` consistency, padding 14/8/4, FontSize 11/12, color tokens.

---

## Item 4 — Free Configs Simple-страница со стилем дизайна (P0 — главное)

### Что хочет user (Variant A из переписки)

**Simple-страница** (как в `-r5`: одна card с N + ping + skip-RU + Refresh, **без** 6 секций слева), **со стилем как в green Deep verify card** из `AdvancedMode.html` / screenshot'а (Tokens-токены, скруглённый зелёный card, правильные шрифты).

### Чего НЕ хочет

- Полного 6-секционного master-detail layout (это что сейчас в `-r6`)
- Голой Simple-страницы без дизайн-стиля (это что было в `-r5`)

### Architecture spec

```
┌─ Free Configs (Simple, design-tokens styled) ──────────┐
│                                                          │
│  ┌─ Search settings card ──────────────────────────┐   │
│  │  ✓✓ Поиск рабочих VLESS-конфигов                 │   │
│  │  Глубокая проверка через временный sing-box.     │   │
│  │                                                    │   │
│  │  Цель: [100▼]  с пингом до [300] ms              │   │
│  │  ☑ Пропускать RU    ☑ Только рабочие             │   │
│  │                                                    │   │
│  │  ┌──────────────────────────────────────────┐   │   │
│  │  │  ✓✓ Глубокая проверка (Refresh+Verify)    │   │   │
│  │  └──────────────────────────────────────────┘   │   │
│  └────────────────────────────────────────────────────┘   │
│                                                            │
│  Server list (latency-sorted, IP-deduped)                 │
│  ...                                                       │
│                                                            │
│  [        Apply selected         ]                        │
└───────────────────────────────────────────────────────────┘
```

### Style tokens для зелёной card (из AdvancedMode.html)

- `Background="{DynamicResource SuccessBgBrush}"` (light: `#DCFCE7`, dark: rgba green)
- `BorderBrush="{DynamicResource SuccessSolidBrush}"` (`#16A34A`)
- `BorderThickness="1"`
- `CornerRadius="{StaticResource RadiusSm}"` (6px)
- `Padding="12,10"`
- Inner: SuccessFgBrush для текста, AccentSolidBrush для primary button (или `SuccessSolidBrush` чтоб matched green theme)

### Bindings (всё уже работает, ViewModel не трогаем)

- `LatencyGoalTarget` (int?, default 100)
- `LatencyGoalMaxPingMs` (int?, default 300, null = no limit)
- `ExcludeRu` (bool, default true)
- `OnlyWorking` (bool, display filter)
- `RefreshCommand` → auto-chains в DeepVerify
- `DisplayedConfigs` → IP-deduped + ping-filtered list
- `ApplySelectedCommand`

### Acceptance

1. Open Free Configs → одна зелёная card с goal-inputs + filter checkboxes + big primary button. **Никакого left-nav из 6 секций.**
2. Click Refresh → status banner появляется, прогресс виден, auto-chains в Deep Verify.
3. List показывает Verified/Ok с unique IPs, отсортирован по latency.
4. Apply → connect к выбранному (не подписке).
5. Re-Refresh → previously-Verified сохраняются.

### Backlog item

Если позже user захочет Advanced features (My sources / Cleanup / preset picker) — добавить **отдельной** страницей или модальным окном «Advanced...» — НЕ в Simple page.

---

## Roadmap

- **v2.28.4-r1**: Items 1+4 (P0/P0)
- **v2.28.4-r2** (если нужно): Items 2+3 (P1/P2)
- **v2.28.4 stable**: после user-confirm

## Что НЕ делать

- НЕ возвращать 6-секционный layout (что сейчас в -r6)
- НЕ убирать ViewModel-fixы из v2.28.3-r2..r5 (auto-chain, dedup, persist, IsSubscribeMode)
- НЕ трогать другие страницы (Subscribe, Servers, Tools, Telegram) — user не жаловался
