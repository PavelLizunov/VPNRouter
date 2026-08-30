---
name: merge-design-handoff
description: Process a design handoff bundle URL. Fetches gzipped tar, extracts to /tmp, reads README + chats + AdvancedMode.html / SimpleMode.html before implementing. Maps design tokens to existing Avalonia Tokens.axaml.
whenToUse: User shares URL like "https://api.anthropic.com/v1/design/h/<hash>" or asks to "implement design" / "fetch design file" / "ориентируйся на дизайн".
---

# Process design handoff bundle

User exports HTML/CSS/JS prototype from design tool as a `.tar.gz`
served from `api.anthropic.com/v1/design/h/<hash>`. Bundle contains:
- `README.md` with instructions for coding agent
- chat transcripts (intent / iteration history — **read thoroughly**)
- HTML prototypes (`AdvancedMode.html` / `SimpleMode.html` / `UIKit.html`)
- `tokens.css` — design system tokens
- `assets/` — icons, graphics, screenshots

## Step 1 — fetch + extract

```bash
set -Eeuo pipefail
DESIGN_DIR="$(mktemp -d /tmp/vpnrouter-design.XXXXXX)"
cleanup() { rm -rf -- "$DESIGN_DIR"; }
trap cleanup EXIT

curl --fail --silent --show-error --location --proto '=https' \
  "https://api.anthropic.com/v1/design/h/<hash>" \
  --output "$DESIGN_DIR/payload.tar.gz"

tar -tzf "$DESIGN_DIR/payload.tar.gz" > "$DESIGN_DIR/entries.txt"
if grep -Eq '(^/|(^|/)\.\.(/|$))' "$DESIGN_DIR/entries.txt" ||
   tar -tvzf "$DESIGN_DIR/payload.tar.gz" | grep -Eq '^[lh]'; then
  echo "Unsafe archive path or link" >&2
  exit 1
fi

tar -xzf "$DESIGN_DIR/payload.tar.gz" \
  --directory "$DESIGN_DIR" --no-same-owner --no-same-permissions

trap - EXIT
printf 'DESIGN_DIR=%s\n' "$DESIGN_DIR"
```

Record the printed `DESIGN_DIR` value for later DSH tool calls; do not rely on a shell variable surviving across calls and do not reuse a stale extraction. Reject unexpected archive layouts or symlink escapes before reading project files.

## Step 2 — read in this order

1. **`README.md`** — root description of bundle contents.
2. **`chats/chat1.md`** (or additional chat files) — **full transcript of design iterations**. Explains design choices and rationale.
3. **`project/README.md`** — design system overview, principles, tokens.
4. **`project/<open-file>.html`** — primary design prototype.
5. **`project/UIKit.html`** — common component kit.
6. **`project/tokens.css`** — semantic tokens. **Map each CSS token → existing Avalonia DynamicResource** in `VPNRouter.App/Styles/Tokens.axaml`.

## Step 3 — extract design intent

In chat transcripts, identify:
- Rejected patterns or requested adjustments
- Confirmed/approved visual elements
- Revert requests or specific size/color/spacing requirements

In HTML/CSS sources:
- Card patterns (`Background`, `Border`, `CornerRadius`, `Padding`)
- Button styles (primary / secondary / destructive)
- Color usage per state (success / warning / danger)
- FontSize ladder and Spacing scale (`gap`, `margin`)

## Step 4 — map to existing tokens

VPNRouter uses a dense token system in `VPNRouter.App/Styles/Tokens.axaml`.
Do not create redundant tokens — **map design CSS variables to existing brushes**:

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

If a new token is genuinely required, add it to `VPNRouter.App/Styles/Tokens.axaml` rather than hardcoding hex values.

## Step 5 — implement

Match visual output, not HTML structure 1:1. Use Avalonia controls and patterns:
- `Border` for cards
- `StackPanel` / `Grid` for layout
- `<Style Selector="Border.foo">` for repeated styles
- `Classes` + `Classes.active="{Binding ...}"` for conditional state

## Step 6 — do NOT open in browser

Read HTML + CSS source directly. Inspect structure and tokens in code rather than rendering in browser.

## Step 7 — clean task-owned extraction

After the handoff has been integrated or rejected, remove the exact `DESIGN_DIR` created by this run. Do this on both success and failure paths; never use a broad `/tmp/vpnrouter-design*` cleanup glob.

## Common gotchas

### User sent design URL for style reference only
Confirm whether the design represents a full specification or a visual reference.

### Bundle archives
Use `curl` and `tar` to download and unpack `.tar.gz` payloads safely to `/tmp`.

## NOT to do

- Do not copy HTML structure 1:1 into XAML. Match visual output using native Avalonia controls.
- Do not invent duplicate design tokens when existing tokens match.
- Do not skip chat transcripts — they contain essential context behind design choices.
