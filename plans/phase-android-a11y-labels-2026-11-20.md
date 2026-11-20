# Android accessibility labels: kebab menu and settings switches (A11Y-01, A11Y-03)

## Why

The Android UX audit (`03-android.md`, snapshots `1cf2d47f` Standard and `2fc5986d`
Business) found two TalkBack blockers in the Avalonia UI:

- A11Y-01 (P1): the whole kebab popup is hidden from the accessibility tree
  (`AccessibilityView.Raw`), and neither menu opener has an accessible name, so
  settings, language, theme, updates, diagnostics, About and the Advanced toggle
  are unreachable without sight.
- A11Y-03 (P2): the audited `CheckBox`/`RadioButton` controls have empty `Content`
  and no `LabeledBy`/`Name`, so TalkBack announces a nameless switch. The audit
  lists six Settings controls plus the Applications row and the subscription
  enable switch.

This task implements exactly those two findings. A11Y-02 (pointer-only selection),
A11Y-04 (modal isolation and focus transfer), icon-only button names from the
second half of A11Y-03, broader keyboard navigation and live device checks stay
with their own changes.

## What

1. Remove `HideSubtreeFromAccessibility(menuPanel)` and the helper itself. The
   kebab popup stays in the control/content views, so its items are enumerated by
   TalkBack again.
2. Give both kebab openers (`AndroidApp.axaml.cs` Home header,
   `AndroidApp.AdvancedShell.cs` Advanced header) `AutomationProperties.Name`
   from `Strings.MenuButtonName` and a tooltip from `Strings.TipMenuButton`, which
   Avalonia Android exposes as the node description. The header is patched, not
   rebuilt, on a language switch, so `ToggleLanguageAndRefresh` re-applies both
   labels (same pattern as every other menu label there).
3. Name the menu items themselves. An item with an icon keeps its label in a
   grid or `IconLabel`, so `ContentControlAutomationPeer.GetNameCore` cannot read
   the text and falls back to the content type name. `SetMenuItemText` now sets
   the name from the same label it writes (so the reset confirmation and the
   language switch follow), `UpdateMenuAdvancedToggle` names the mode action, and
   the About row is named from `Strings.SmpMenuAbout`.
4. Link the visible title to the switch in `MakeRadioCard` and `MakeCheckboxCard`
   (`AutomationProperties.LabeledBy`), which covers the six audited Settings
   controls: split/full routing, bypass Russian traffic, block ads,
   auto-reconnect, external control.
5. Link the app row checkbox to the app name label and the subscription enable
   checkbox to the subscription name label.
6. Add `MenuButtonName`/`TipMenuButton` to `Strings.Android.cs` and forward them
   through `VPNRouter.Android/Localization.cs`.
7. Add `AndroidAccessibilityLabelsTests` as a source-level regression guard for
   the wiring above. It does not prove runtime announcements.

## Invariants

- Click and pointer behavior is unchanged: no handler, hit area or event is
  moved or added; `LabeledBy` does not alter input.
- Appearance is unchanged: no size, padding, color, brush or layout edit.
- Data, storage keys and localization refresh semantics are unchanged; the new
  strings follow the existing RU/EN switch and the existing language refresh.
- No new dependency, no project or build change.
- The Advanced shell and overlays keep their current background behavior
  (A11Y-04 is out of scope).

## Verification

Not run in this isolated worker, per the task constraints: no local build, no
test run, no emulator/device, no push. Required before merge:

| check | owner |
|---|---|
| `AndroidAccessibilityLabelsTests` plus the existing Android slice on an authorized preflighted worker | implementer/CI |
| Android Release APK build on the authorized worker | implementer/CI |
| TalkBack acceptance checklist below on a real device or emulator with TalkBack | implementer |

The source guards are static: they pin the naming calls and the absence of the
hide workaround, not the spoken result.

## TalkBack acceptance checklist

Preconditions: Android device or emulator, TalkBack on, app installed from the
build under test, both RU and EN available in the menu.

1. Home header: focus the kebab with swipe navigation. TalkBack says the menu
   name (RU "Меню" / EN "Menu") and then the description, and announces a button.
2. Activate the kebab (double tap) and swipe through every section. The appearance
   header, Light/Dark, RU/EN, Open log, Export diagnostics, Check leaks, Check for
   updates, Health check, Safe mode, Reset settings, Add tile, About and the
   Advanced toggle are each focused and announced with their visible label. No
   item is skipped.
3. Theme: activate Light and then Dark. The menu closes, the page repaints, and
   the kebab still announces the menu name.
4. Language: switch RU -> EN and back. Reopen the menu: the kebab name and
   description follow the language, and every item keeps its localized label.
5. Advanced mode: switch with the Advanced toggle. In the Advanced header, focus
   the kebab: same name/description. Open it and confirm the items are announced.
6. Settings tab, Routing: focus the split and full radio buttons. Each announces
   its visible title (for example RU "Раздельный туннель", EN "Split Tunnel"),
   its checked state and radio role. Activate each by double tap and confirm the
   selection changes as before.
7. Settings tab, same section: focus the bypass control. It announces the visible
   title and checkbox state; double tap toggles it.
8. Settings tab, Content: the block-ads control announces its title and state.
   Settings tab, Reliability: auto-reconnect and external control do the same.
9. Applications: focus a row checkbox. It announces the app name, role and
   checked state. The row text is not announced as the checkbox name twice in a
   row that would confuse the two. Double tap toggles only that app, as before.
10. Subscribe: focus the enable checkbox of a subscription. It announces the
    subscription name, role and checked state. Double tap enables/disables it and
    the aggregated list refreshes as before.
11. Regression: the visible layout, fonts, spacing and colors of the menu and
    these cards look identical to the build before the change.

Record device, Android version, TalkBack version and the RU/EN results. A pass
closes the corresponding ledger entries.

## Unknowns

- Avalonia Android popup accessibility was not exercised; the audit's cause is
  from source only. If TalkBack still skips the popup, the next step is a live
  investigation of the popup host, not re-hiding the subtree.
- A `Name` change on an already focused node may not emit a property-change event
  in Avalonia 12.0.3. The label is refreshed for the next focus; step 4 checks it.
- The visible title `TextBlock` is still a separate node from the switch, so
  TalkBack can read the title again when swiping. Making the whole row one node
  belongs to A11Y-02/A11Y-03's row work, not here.
- The same composite-content naming weakness exists on buttons whose content is
  a layout outside the menu: the Home header button, the Advanced bottom tabs,
  the Advanced footer Connect, the Settings Apply button and the autostart card.
  They are not part of A11Y-01/03 as requested and were left for the follow-up;
  the menu is corrected here because it would otherwise stay unintelligible
  after the unhide.

## Rollback

Revert the commit. No data migration, storage change or release artifact is
involved.

## Outcome

Working diff on `t3/android-accessibility-labels`. No commit was made in this
isolated worker because the workspace has no Git identity configured; the primary
agent owns the commit, push and PR. The change touches eight Android/Core sources,
one new test file, this record and the two `plans/OPEN-DEFECTS.md` entries. No
build, test run or device run was executed here by task constraint; the focused
test and the TalkBack checklist above are the gates before the ledger entries can
be closed.
