---
name: merge-design-handoff
description: Process a Claude Design (claude.ai/design) handoff bundle URL. Fetches gzipped tar, extracts to /tmp, reads README + chats + AdvancedMode.html / SimpleMode.html before implementing. Maps design tokens to existing Avalonia Tokens.axaml.
when: User shares URL like "https://api.anthropic.com/v1/design/h/<hash>" or asks to "implement design" / "fetch design file" / "ориентируйся на дизайн".
---

# Process Claude Design handoff bundle

User exports HTML/CSS/JS prototype from claude.ai/design as a `.tar.gz`
served from `api.anthropic.com/v1/design/h/<hash>`. Bundle contains:
- README с инструкциями для coding agent
- chat transcripts (intent / iteration history — **обязательно прочесть**)
- HTML prototypes (`AdvancedMode.html` / `SimpleMode.html` / `UIKit.html`)
- `tokens.css` — design system tokens
- `assets/` — penguin SVG, lockup, screenshots

## Step 1 — fetch + extract

```bash
# WebFetch на URL вернёт binary preview + сохранит файл в tool-results/
WebFetch https://api.anthropic.com/v1/design/h/<hash>?open_file=AdvancedMode.html
# ↑ говорит: "Binary content (application/gzip, NNNkB) saved to .../webfetch-<id>.bin"

# Берём путь из stderr и распаковываем:
DESIGN_DIR="C:/tmp/vpnrouter-design"
mkdir -p "$DESIGN_DIR"
gunzip -c "<saved-bin-path>" > "$DESIGN_DIR/payload.tar"
cd "$DESIGN_DIR" && tar -xf payload.tar
# Получаем vpnrouter-design-system/ внутри $DESIGN_DIR
```

## Step 2 — read in this order

1. **`README.md`** — корневой. Скажет что в bundle и какой `open_file`.
2. **`chats/chat1.md`** (или больше) — **полная transcript user↔design**. Где они landed. **Не пропускать** — design HTML это "что", chat это "почему".
3. **`project/README.md`** — design system overview, principles, tokens.
4. **`project/<open-file>.html`** — primary design (тот что user открыл).
5. **`project/UIKit.html`** — общий kit (если open_file отдельный).
6. **`project/tokens.css`** — semantic tokens. **Map каждый CSS token → существующий Avalonia DynamicResource** в `VPNRouter.App/Styles/Tokens.axaml`.

## Step 3 — extract design intent

В chat transcripts типичные сигналы:
- "не нравится" / "переделай" — что отвергнуто
- "идеально" / "то что нужно" — что зафиксировано
- "вернуть как было" — revert intent
- specific size/color/spacing requests

В HTML — искать `<style>` блоки + inline `style=`, выписать:
- Card pattern (`Background`, `Border`, `CornerRadius`, `Padding`)
- Button styles (primary / secondary / destructive)
- Color usage per state (success / warning / danger)
- FontSize ladder
- Spacing scale (`gap`, `margin`)

## Step 4 — map to existing tokens

VPNRouter уже имеет dense token system в `VPNRouter.App/Styles/Tokens.axaml`.
Не создавать новые tokens — **map design CSS vars to existing brushes**:

| Design CSS var | Avalonia token |
|---|---|
| `--surface-app` | `SurfaceAppBrush` |
| `--surface-sunken` | `SurfaceSunkenBrush` |
| `--surface-base` | `SurfaceBaseBrush` |
| `--surface-raised` | `SurfaceRaisedBrush` |
| `--text-primary` | `TextPrimaryBrush` |
| `--text-secondary` | `TextSecondaryBrush` |
| `--text-muted` | `TextMutedBrush` |
| `--text-accent` | `TextAccentBrush` |
| `--border-default` | `BorderDefaultBrush` |
| `--accent-solid` | `AccentSolidBrush` |
| `--success-bg` / `-border` / `-fg` / `-solid` | `SuccessBgBrush` / `SuccessBorderBrush` / `SuccessFgBrush` / `SuccessSolidBrush` |
| `--radius-sm` (6px) | `RadiusSm` |
| `--radius-md` (8px) | `RadiusMd` |
| `--radius-lg` (10px) | `RadiusLg` |
| `--fs-xs` (10px) | `FontSize="10"` (literal) |
| `--fs-sm` (11px) | `FontSize="11"` |
| `--fs-md` (12px) | `FontSize="12"` |
| `--fs-lg` (13px) | `FontSize="13"` |

Если в design новый token которого нет в Avalonia tokens — добавить в Tokens.axaml,
не hardcode'ить hex.

## Step 5 — implement

Match VISUAL output, не структуру HTML 1:1. HTML это prototype, Avalonia это
production. Использовать существующие patterns:
- `Border` для cards
- `StackPanel`/`Grid` для layout
- `<Style Selector="Border.foo">` для повторяющихся стилей
- `Classes` + `Classes.active="{Binding ...}"` для conditional state

## Step 6 — НЕ запускать в браузере

README design bundle прямо говорит: "Don't render these files in a browser
or take screenshots unless the user asks you to. Everything you need —
dimensions, colors, layout rules — is spelled out in the source."

Читать **HTML + CSS source** напрямую. Если непонятно — спросить user'а,
не догадывать.

## Common gotchas

### User sent design URL but wants something specific
Иногда user шлёт design URL как **референс стиля**, не как полную spec
implement'нуть. Прочесть transcripts чтобы понять что именно нужно. Спросить
если ambiguous.

### Design file gzip + tar binary
WebFetch не сможет распарсить — сохраняет в `tool-results/*.bin`. Пут берётся
из text response ("Binary content ... saved to <path>"). НЕ пытаться парсить
без `gunzip` + `tar`.

### Design has 2 modes (Simple + Advanced)
VPNRouter UIKit defines обе. Если user не указал какой — спросить. Default
обычно "Advanced" (более полный).

## NOT to do

- Не копировать HTML структуру 1:1 в XAML — это разные технологии. Match output, не source.
- Не создавать новые design tokens с нуля если уже есть существующие — map.
- Не открывать design в браузере (README прямо запрещает).
- Не пропускать chat transcripts — там lives the actual intent.
