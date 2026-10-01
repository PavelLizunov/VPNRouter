# D-2: desktop design pass (button roles, disclosure toggles, empty states)

## Why

The owner asked to carry the sensible Android design changes to the PC version. D-1 (#442) moved the desktop icons to
the Lucide set; this pass carries the design rules. Findings come from the D-1 design captures
(`PageScreenshotDesignTests`, every page in light and dark at 360 and 520 px) of `main` before the change.

## Found (captures of `main`, before)

1. Default Fluent buttons (light grey) look disabled: "Remove", "Refresh all", "Download", "Update IPSet list",
   "Copy", "New", "Check for updates", "Other versions", "Pick from IPv4 ping", "Import...", "Export...". The same
   problem was fixed on Android with the tonal role (U4, A-5).
2. Coloured buttons (28: Start VPN, Add, Find working configs, Test all, ...) turn light grey with black text on mouse
   hover, because the Fluent hover colour replaces their own.
3. The "Config / Mode" row of the simple page has a bare grey chevron; Android has a labelled "Change" / "Hide" pill.
4. The Public tab "Settings" is a Fluent Expander inside the green card: a boxed header about 130 px wide with two
   chevrons (one from the "▾ Settings" text, one from the Expander).
5. The Servers list has no empty state: a large blank box. The Subscribe empty state uses 0.4 opacity text.
6. The DPI strategy legend is 8 px text with 8 px icons.
7. Rules: "No rules yet. Add via the form below or expand «Advanced mode»": the form is above and there is no
   "Advanced mode" (the text editor is the «Edit» tab).
8. Checkboxes and radio buttons use the Fluent default blue (or the Windows accent colour), not the app accent.

## Changes

1. `Styles/ButtonRoles.axaml` (desktop only): the Fluent button resources give a button without its own colour the
   tonal role (AccentBgMuted background, AccentFg text, no border; hover and pressed one step stronger); disabled is
   SurfaceSunken with TextMuted text (light; the dark values follow the dark tokens).
2. Class `solid` on the 28 buttons with an Accent/Success/Warning/Danger solid background: hover and pressed keep
   their colour and dim slightly instead of turning grey.
3. Disclosure pattern (App.axaml styles `disclosure-pill`, `Pictogram.disclosure` with a 180 ms turn): the simple-page
   config row shows "Change ⌄" / "Hide ⌃"; the Public settings row is a full-width row "Settings ... Show ⌄" /
   "Hide ⌃" (shared keys `SectionShow`, `SectionHide` from A-6) instead of the Expander.
4. Servers empty state ("No servers yet. Paste a server link into the field below and add it.", shared key
   `SrvManualEmptyHint`); Subscribe empty state uses the text tokens instead of opacity.
5. Legend: 9 px text, 10 px icons, wider spacing.
6. `CustomRulesEmpty` says "form above" and names the «Edit» tab.
7. Fluent accent palette pinned to the token accent (#0EA5E9 light, #38BDF8 dark), as on Android (A-6).
8. Dead `BoolToChevronConverter` removed.
9. Found in the first captures of this branch and fixed: "Update IPSet list" and the two hosts toggles are stretched
   buttons with left-aligned labels (centred now); the servers empty state said "No servers" twice (one line now).
10. No dead controls: "Test all" and "Deep verify" (Servers, Subscribe) are disabled while their list is empty,
    "Refresh all" while there is no subscription (same as A-6 on Android).
11. Telegram: the port field is 88 px instead of 120 px so the secret field keeps its room at 360 px; four muted labels
    (Telegram port and secret, two hints) lose an extra 40 to 50 % opacity on top of the muted token.
12. `VisualDiffTests` baselines for DPI bypass and Tools re-pinned: the change of the default button role, the accent
    checkbox and the legend size is intended, and the old baselines still showed the removed Emergency Channel tab.

Built on top of A-6 (#443) for the shared keys; rebased onto `main` after A-6 merges.

## Invariants

- No hex colours in page XAML. The ButtonRoles file holds Fluent resource values equal to the token colours.
- Pictogram ids and `PictogramText` unchanged. The mascot is not touched.

## Verification

- Windows worker, exact SHA: `PageScreenshotDesignTests`, `VisualDiffTests`, `PictogramTextTests`,
  `DesktopPictogramScreenshotTests`, the ViewModel tests; captures read before and after.
- PR CI.

## Outcome

Windows worker, exact SHAs (preflight: CPU 1 to 3 %, 12.6 GB free RAM, 13.5 GB free disk, three idle dotnet/java
processes left by the APK build): `fd87f73d` 67 of 69 (the two visual baselines, see 12), `14a792be` captures read,
`8eaaad94` 112 of 112 (`VisualDiffTests`, `HeadlessGuiTests`, every `MainWindowViewModel*` test). Captures read before
(`main`) and after: window simple and advanced, every tab, Settings sub-tabs, light and dark, 360 and 520 px, Russian.
Not seen: mouse hover and pressed states (headless captures have no pointer), the real Windows accent colour, a live
WINBRAT run.
