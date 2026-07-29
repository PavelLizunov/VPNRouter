# Phase 2 — R04 — NetworkPage read-mode rule row narrow layout

**Owner**: Qwen Code session (code-only)
**Branch**: `codex/qwen-audit-r04-ui-layout-2026-07-29`
**Base**: `origin/main`. INSPECTED overlap with P06: the P06 branch (`codex/qwen-audit-p06-smart-connect-persistence-2026-07-29`, FLOW-1) touched ONLY `VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs` (+6) and `VPNRouter.Tests/SmartConnectPersistenceTests.cs`. It did NOT touch `NetworkPage.axaml`. UI-2 lives entirely in `VPNRouter.App/Views/Pages/NetworkPage.axaml`. **No overlap → base is `origin/main`, not the P06 branch.**
**Roadmap ref**: `plans/qwen-remaining-remediation-index-2026-07-29.md` (R04); prompt pool P06
**IDs**: UI-2
**Effort**: ~1.5 h
**Risk**: LOW-MEDIUM (UI layout; risk is regressing the wide layout or hiding the delete action further)
**Blast radius**: `VPNRouter.App/Views/Pages/NetworkPage.axaml` (+ a narrow-layout test) · ~+60 LOC · runtime: read-mode rule row rendering at narrow widths
**Rollback**: `git revert <commit>` / delete branch

---

## 1. Final P00 verdict / severity / confidence / corrected scope

| ID | Orig | Verdict | Final | Conf |
|---|---|---|---|---|
| UI-2 | P2 | CONFIRMED | P2 | High |

Corrected scope (from P00): **worse than the original "value clips" framing** —
at `MinWidth=360` the fixed columns alone exceed the detail pane, the Value
column collapses (ellipsizes), and the `✕` delete button is pushed off the right
edge → the delete action itself is unreachable, not merely cosmetic.

## 2. Verified current root cause (commit `b39a28c3`)

`VPNRouter.App/Views/Pages/NetworkPage.axaml`:

- Read-mode rule row `DataTemplate` (verified `:1478-1521`):
  ```xml
  <Grid ColumnDefinitions="20,70,140,*,Auto" ColumnSpacing="10" Margin="14,1" ...>
    <TextBlock Grid.Column="0" Text="●" .../>
    <TextBlock Grid.Column="1" Text="{Binding Action}" .../>
    <TextBlock Grid.Column="2" Text="{Binding Type}" .../>
    <TextBlock Grid.Column="3" Text="{Binding Value}" TextTrimming="CharacterEllipsis" .../>
    <Button Grid.Column="4" Content="✕" Command="{Binding RemoveCommand}" .../>
  </Grid>
  ```
- Fixed columns sum to 20+70+140 = 230 px, + 4×10 spacing + 28 margin = ~298 px
  before the Value(`*`)/delete(`Auto`) columns.
- Detail pane: `:191` `ColumnDefinitions="140,*"`; `ScrollViewer` `:230-232`
  `HorizontalScrollBarVisibility="Disabled"`, `AllowAutoHide="False"`.
- Window floor: `MainWindow.axaml:14-15` `Width="520"`, `MinWidth="360"`.
- At `MinWidth=360` the detail pane is ≈ 360 − 140 (nav) − ~14 (reserved
  scrollbar) ≈ 206 px usable; horizontal scroll is DISABLED, so the fixed portion
  alone exceeds the pane and the `✕` button is clipped off-edge.
- The same read-mode row shape is reused across the direct/proxy/block rule
  groups (e.g. direct group `:1477`, proxy group `:1525`).
- Existing responsive infrastructure: `IsRulesNarrow` VM property
  (`MainWindowViewModel.cs:1753`, `[ObservableProperty] private bool
  _isRulesNarrow`), set in `NetworkPage.axaml.cs:48` (`vm.IsRulesNarrow = width <
  NarrowBreakpoint`), with established wide/narrow template pairs at
  `:533/:672`, `:772/:980`, `:1197/:1333`.

## 3. Why

At the supported minimum window width the read-mode rule row loses its delete
action entirely (clipped off the right edge with horizontal scroll disabled). A
user managing custom rules on a narrow window cannot remove a rule. The page
already has an `IsRulesNarrow` responsive pattern; this row simply lacks a narrow
template.

## 4. What

Make the read-mode rule row responsive so the Value and the `✕` delete action
remain visible/reachable at `MinWidth=360`. Reuse the existing `IsRulesNarrow`
pattern: provide a narrow sibling `DataTemplate` that shrinks/stacks the fixed
columns (e.g. drop/condense the `●`/Type columns or stack Action+Value) while
keeping the delete button in-layout. Apply to every read-mode rule group that
shares this row shape (direct/proxy/block).

```diff
- <Grid ColumnDefinitions="20,70,140,*,Auto" ColumnSpacing="10" Margin="14,1" ...>
+ <!-- wide row: unchanged -->
+ <Grid IsVisible="{Binding !IsRulesNarrow}" ColumnDefinitions="20,70,140,*,Auto" ...> ... </Grid>
+ <!-- narrow row: condensed, delete stays reachable -->
+ <Grid IsVisible="{Binding IsRulesNarrow}" ColumnDefinitions="*,Auto" ...>
+   <StackPanel Grid.Column="0" ...> Action / Type / Value stacked ... </StackPanel>
+   <Button Grid.Column="1" Content="✕" Command="{Binding RemoveCommand}" .../>
+ </Grid>
```

(Exact condensed layout chosen during implementation; the invariant is that the
delete button is always in-layout and keyboard-focusable.)

## 5. How (ordered minimal steps)

1. Run the `audit-overflow-fix` skill for the NetworkPage scope to confirm the
   overflow and catch any sibling bare-string/overflow issues in the same row.
2. Read the full read-mode rules section (direct/proxy/block groups) and the
   existing `IsRulesNarrow` wide/narrow pairs to copy the established idiom.
3. Add a narrow `DataTemplate`/row variant bound to `IsRulesNarrow`; keep the
   wide variant bound to `!IsRulesNarrow` (byte-identical to today).
4. Ensure the delete `Button` keeps `Command="{Binding RemoveCommand}"` and stays
   keyboard-focusable in the narrow variant.
5. Do NOT enable horizontal scrolling as a clipping mask.
6. Add a narrow-layout contract test.

### Tests written

- `NetworkPageLayoutTests.ReadModeRuleRow_NarrowTemplate_DeleteReachable` — asserts
  a narrow row template exists and the delete control is present/visible when
  `IsRulesNarrow=true` (headless Avalonia or VM-level contract).
- `NetworkPageLayoutTests.ReadModeRuleRow_WideTemplate_Unchanged` — asserts the
  wide row still uses the 5-column shape when `IsRulesNarrow=false`.
- (Optional) an overflow audit assertion that no sibling read-mode row clips the
  delete action.

### Verification approach

`audit-overflow-fix` static pass + headless/VM layout contract. No local Avalonia
launch; build and any headless tests run in remote GitHub CI.

## 6. Affected callers / consumers + invariants

- Consumers: every read-mode rule group (direct/proxy/block) using the row
  template; `RemoveCommand` on `CustomRuleViewModel`. Invariant: wide-layout
  rendering is byte-identical; `RemoveCommand` binding preserved; delete stays
  keyboard-accessible.
- `IsRulesNarrow` setter (`NetworkPage.axaml.cs:48`) and `NarrowBreakpoint`
  unchanged.

## 7. Exact expected file list

- `VPNRouter.App/Views/Pages/NetworkPage.axaml` (narrow read-mode row variant for each affected group)
- `VPNRouter.Tests/NetworkPageLayoutTests.cs` (or the existing App layout/VM test file — add tests)

## 8. Non-goals

- Do NOT change the wide layout.
- Do NOT enable horizontal scrolling to mask clipping.
- Do NOT touch edit-mode rule rows unless the audit-overflow-fix pass proves they
  share the exact defect (keep scope to the reported read-mode row).
- Do NOT change `NarrowBreakpoint` or the `IsRulesNarrow` mechanism.
- Do NOT launch the Avalonia app or run MCP (code-only).

## 9. Security / concurrency / data-loss / platform review

- **Security**: none.
- **Concurrency**: none (UI template).
- **Data-loss**: the defect blocks a destructive action (delete) from being
  reachable; the fix restores access. `RemoveCommand` semantics are unchanged —
  no new deletion path is introduced.
- **Platform**: Avalonia layout, cross-platform; verify the narrow variant renders
  on the shared token set (Styles/Tokens.axaml) per the audit-overflow-fix skill.

## 10. Dependencies / overlaps

- **P06 (FLOW-1) inspected — NO overlap** (different file) → base `origin/main`.
- No other R-package touches `NetworkPage.axaml`. R09 (UI-1) touches
  `MainWindow.axaml`, a different file — independent.

## 11. Remote-only verification gates

- [ ] Gate 1 — Build clean (remote CI): 0 errors.
- [ ] Gate 2 — Tests green (remote CI): new layout contract test passes; existing App tests stay green.
- [ ] Gate 3 — Docs: brief Outcome filled; zone CLAUDE.md unchanged.
- [ ] Gate 4 — Self-review: `audit-overflow-fix` pass clean.
- [ ] Gate 5 — MCP verify: deferred to orchestrator's remote/live stage; Qwen does NOT run MCP. Note in Outcome whether a narrow-window screenshot is requested.
- [ ] Gate 6 — Characterization diff: N/A (wide layout asserted byte-identical by test).

## 12. Outcome (PENDING — filled after merge)

**Status**: PENDING
**Commits**: PENDING
**Pushed**: PENDING
**Test deltas**: PENDING
**Files changed**: PENDING

**Gate results:**
- [ ] Gate 1: PENDING
- [ ] Gate 2: PENDING
- [ ] Gate 3: PENDING
- [ ] Gate 4: PENDING
- [ ] Gate 5: PENDING (narrow-window screenshot — orchestrator/live stage)
- [-] Gate 6: N/A

**Surprises encountered**: PENDING
**Follow-ups spawned**: PENDING

## 13. Rollback

`git revert <commit>` on the R04 branch, or delete
`codex/qwen-audit-r04-ui-layout-2026-07-29`. The read-mode row reverts to the
single 5-column template (prior behavior); no state is written.

## 14. Self-contained copyable Qwen prompt

```text
Выполни brief plans/phase2-audit-r04-ui-layout-2026-07-29.md через Qwen Code.
ID: UI-2 (P2). Base branch: origin/main (P06/FLOW-1 не трогал NetworkPage.axaml
— overlap отсутствует). Сначала прочитай brief целиком, AGENTS.md,
plans/CLAUDE.md и VPNRouter.App/CLAUDE.md. Сделай read-mode rule row в
Views/Pages/NetworkPage.axaml responsive так, чтобы value и delete (✕) оставались
видимы/достижимы при MinWidth=360; переиспользуй существующий IsRulesNarrow
narrow-template паттерн; не добавляй horizontal scrolling как маскировку
clipping; сохрани keyboard-accessible delete. Запусти skill audit-overflow-fix
для UI scope. Напиши минимальный narrow-layout contract test. НЕ запускай
локальные build/Avalonia app/binary, не делай live/MCP мутаций. Только
чтение/поиск/редактирование и запись тестов. Commit/push/CI делает orchestrator.
Без release/merge/tag/deploy. Без emoji. Заполни Outcome шаблоном PENDING.
```
