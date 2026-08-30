---
name: audit-overflow-fix
description: Find and fix UI overflow on narrow VPNRouter Avalonia windows. Wraps bare-string CheckBox.Content / Button.Content in TextBlock with TextWrapping="Wrap". Verifies design tokens from Styles/Tokens.axaml.
whenToUse: User reports "вылазиет за экран", "не помещается", or shares screenshot showing horizontal overflow in NetworkPage / DpiBypassPage / ApplicationsPage / etc. Also triggers when implementing new settings cards.
---

# Audit & fix UI overflow on narrow windows

VPNRouter targets 520px window width. Bare-string CheckBox/Button content
binds set parent min-width to widest localised label → push past
ScrollViewer right edge.

## The anti-pattern

```xaml
<!-- WRONG: bare-string sets no-wrap TextBlock template -->
<CheckBox IsChecked="{Binding X}" Content="{Binding XLabel}"/>
```

## The fix pattern (v2.27.0-r2 + v2.28.3-r1)

```xaml
<CheckBox IsChecked="{Binding X}"
          MinHeight="0" Padding="4,0"
          ToolTip.Tip="{Binding L_TipForX}">
  <TextBlock Text="{Binding XLabel}" TextWrapping="Wrap"/>
</CheckBox>
```

Same для Button.Content:
```xaml
<Button Command="{Binding YCommand}" Padding="10,4" ...>
  <TextBlock Text="{Binding YLabel}" TextWrapping="Wrap"/>
</Button>
```

## When TextWrapping isn't enough

Если фикс не помог — проверить родительский контейнер:

1. **`<StackPanel>`** — без `MaxWidth` тянется по contentу. Решение: либо `MaxWidth`, либо переделать на `Grid` с `ColumnDefinitions="24,*"`:
```xaml
<Grid ColumnDefinitions="24,*" ColumnSpacing="8">
  <CheckBox Grid.Column="0" IsChecked="{Binding X}" MinHeight="0" Padding="0"/>
  <TextBlock Grid.Column="1" Text="{Binding XLabel}" TextWrapping="Wrap"/>
</Grid>
```

2. **`<Border>`** — `HorizontalAlignment="Stretch"` не помогает если родительский panel не constraining width. `MaxWidth` на StackPanel внутри.

3. **ScrollViewer ancestor** — `HorizontalScrollBarVisibility="Disabled"` обрезает контент, но не сжимает; child должен сам сжиматься.

## Design tokens to use

Из `VPNRouter.App/Styles/Tokens.axaml`:

| Token | Use for |
|---|---|
| `RadiusSm` (6px) | Кнопки, inputs, settings cards |
| `RadiusMd` (8px) | Larger panels |
| `SurfaceSunkenBrush` | Сгруппированные subsettings (Leak Protection, Updates) |
| `SurfaceBaseBrush` | Радио-карты / checkbox-карты (default state) |
| `SurfaceRaisedBrush` | Empty state cards |
| `BorderDefaultBrush` | 1px borders для cards |
| `AccentSolidBrush` + `AccentOnSolidBrush` | Primary buttons |
| `SuccessBgBrush` + `SuccessSolidBrush` + `SuccessFgBrush` | Зелёный card (Deep verify) |
| `WarningBgBrush` + ... | Busy / cancel banners |
| `DangerSolidBrush` + White | Stop / destructive buttons |
| `InfoBgBrush` + `InfoFgBrush` | Quickstart / info banners |

**Никогда не hardcode hex** — всегда `{DynamicResource ...}`.

## Settings card pattern (matches design system)

```xaml
<Border Padding="12,10" CornerRadius="{StaticResource RadiusSm}"
        Background="{DynamicResource SurfaceSunkenBrush}"
        BorderBrush="{DynamicResource BorderDefaultBrush}"
        BorderThickness="1">
  <StackPanel Spacing="6">
    <TextBlock Text="{Binding HeaderLabel}" FontWeight="SemiBold"
               Opacity="0.7" TextWrapping="Wrap"/>
    <CheckBox IsChecked="{Binding X1}" MinHeight="0" Padding="4,0">
      <TextBlock Text="{Binding L_X1Label}" TextWrapping="Wrap"/>
    </CheckBox>
    ...
  </StackPanel>
</Border>
```

## Radio-card pattern (NetworkPage routing)

```xaml
<Style Selector="Border.radio-card">
  <Setter Property="BorderThickness" Value="1"/>
  <Setter Property="BorderBrush" Value="{DynamicResource BorderDefaultBrush}"/>
  <Setter Property="Background" Value="{DynamicResource SurfaceBaseBrush}"/>
  <Setter Property="CornerRadius" Value="{StaticResource RadiusSm}"/>
  <Setter Property="Padding" Value="10,10"/>
</Style>
<Style Selector="Border.radio-card.active">
  <Setter Property="BorderBrush" Value="{DynamicResource AccentSolidBrush}"/>
  <Setter Property="Background" Value="{DynamicResource AccentBgSubtleBrush}"/>
</Style>
```

Применение:
```xaml
<Border Classes="radio-card" Classes.active="{Binding IsSplitTunnel}">
  <Grid ColumnDefinitions="24,*" ColumnSpacing="8">
    <RadioButton Grid.Column="0" IsChecked="{Binding IsSplitTunnel}"
                 GroupName="RoutingMode" VerticalAlignment="Top"
                 MinWidth="0" MinHeight="0" Margin="0,1,0,0"/>
    <StackPanel Grid.Column="1" Spacing="2">
      <TextBlock Classes="card-title" Text="{Binding L_SplitTunnelTitle}"/>
      <TextBlock Classes="card-sub" Text="{Binding L_SplitTunnelSubtitle}"/>
    </StackPanel>
  </Grid>
</Border>
```

`card-title` style имеет `TextWrapping="Wrap"` уже встроенным.

## Audit checklist для новой settings page

1. Все `CheckBox`/`Button` `Content="{Binding ...}"` обёрнуты в `<TextBlock TextWrapping="Wrap"/>` ?
2. Все `<Border>`/`<StackPanel>` имеют semantic tokens (не hex) ?
3. ScrollViewer есть, `HorizontalScrollBarVisibility="Disabled"` ?
4. На window 500px width — нет horizontal scroll, нет clip'нутых элементов ?
5. Padding/Margin использует 4px grid (4/8/10/12/14) ?
6. FontSize из ladder: 9/10/11/12/13 (никаких 14+ для body) ?

## Verification tools

- Use DSH `grep` to find bare-string `Content` bindings and raw colors.
- Run focused headless `PageScreenshotTests` at normal and narrow viewports; use `VisualDiffTests` where a baseline exists.
- Any shipped live UI scenario runs only through the fixed-WINBRAT verifier under `docs/agent-contract.md`; do not use a local desktop fallback.

## NOT to do

- Не использовать hex в новом коде — всегда tokens.
- Не оставлять `Content="{Binding XLabel}"` на CheckBox/Button с локализуемым текстом.
- Не ставить `MinWidth` на CheckBox/Button чтобы скрыть overflow — пусть wrap'ится.
