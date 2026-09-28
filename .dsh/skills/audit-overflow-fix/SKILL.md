---
name: audit-overflow-fix
description: Find and fix horizontal UI overflow on narrow VPNRouter Avalonia windows by wrapping bare-string CheckBox and Button content in wrapping TextBlocks and using design tokens from Styles/Tokens.axaml.
whenToUse: User reports text running off screen ("вылазиет за экран", "не помещается"), shares a screenshot with horizontal overflow, or a new settings card is being added.
---

# Audit and fix UI overflow on narrow windows

VPNRouter opens at 520px width with `MinWidth` 360px (`VPNRouter.App/Views/MainWindow.axaml`).
Layouts must fit at 360px. A bare-string `Content` on `CheckBox` or `Button` uses a
non-wrapping template, so the widest localized label sets the minimum width and
pushes past the `ScrollViewer` edge.

## Fix pattern

```xaml
<!-- Wrong: bare string, no wrapping -->
<CheckBox IsChecked="{Binding X}" Content="{Binding XLabel}"/>

<!-- Right -->
<CheckBox IsChecked="{Binding X}" MinHeight="0" Padding="4,0">
  <TextBlock Text="{Binding XLabel}" TextWrapping="Wrap"/>
</CheckBox>
```

Apply the same to `Button` content. If wrapping alone does not help, check the parent:

- `StackPanel` grows with its content: give it `MaxWidth` or use
  `<Grid ColumnDefinitions="24,*" ColumnSpacing="8">` with the control in column 0
  and a wrapping `TextBlock` in column 1.
- `Border` with `HorizontalAlignment="Stretch"` does not constrain width by itself.
- A `ScrollViewer` with `HorizontalScrollBarVisibility="Disabled"` clips; the child
  must shrink on its own.

## Design tokens

Use `{DynamicResource ...}` / `{StaticResource ...}` from `VPNRouter.App/Styles/Tokens.axaml`;
never hardcode hex colors. Common ones: `RadiusSm`, `RadiusMd`, `SurfaceSunkenBrush`
(grouped sub-settings), `SurfaceBaseBrush` (radio and checkbox cards),
`BorderDefaultBrush`, `AccentSolidBrush` + `AccentOnSolidBrush` (primary buttons),
`SuccessBgBrush`, `WarningBgBrush`, `DangerSolidBrush`, `InfoBgBrush` + `InfoFgBrush`.

Settings card: `Border` with `Padding="12,10"`, `CornerRadius="{StaticResource RadiusSm}"`,
`SurfaceSunkenBrush` background and `BorderDefaultBrush` 1px border, containing a
`StackPanel Spacing="6"` of wrapped controls. Routing radio cards use the
`Border.radio-card` / `Border.radio-card.active` styles in `NetworkPage.axaml`.

## Checklist for a new settings page

1. Every `CheckBox`/`Button` with localized content wraps a `TextBlock TextWrapping="Wrap"`.
2. Colors and radii come from tokens.
3. A `ScrollViewer` with horizontal scrolling disabled hosts the page.
4. At 360px width nothing scrolls horizontally or clips.
5. Spacing uses the 4px grid; body font sizes stay within 9-13.

## Verification

- Search for bare-string `Content="{Binding` on `CheckBox`/`Button` and for hex colors.
- Run focused headless `PageScreenshotTests` at normal and narrow widths; use
  `VisualDiffTests` where a baseline exists.
- Live UI checks run only through the fixed-WINBRAT verifier in `docs/agent-contract.md`.

## Do not

- Hide overflow with `MinWidth` on the control; let the text wrap.
- Leave bare-string content on controls with localized text.
