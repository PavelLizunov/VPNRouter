# Phase 4 — Avalonia 12 modernization (compiled bindings + x:DataType)

**Owner**: Wave 14 single agent
**Roadmap ref**: Avalonia 12 release notes — `https://avaloniaui.net/blog/avalonia-12/` + `https://docs.avaloniaui.net/docs/data-binding/compiled-bindings`
**Effort**: 1 day
**Risk**: MEDIUM (turning runtime binding warnings into compile-time errors surfaces hidden binding bugs — must fix before commit)

## Why

We bumped to Avalonia 12.0.3 in Wave 12 (commit `034baba`), but we left compiled bindings to **implicit default** rather than **explicit opt-in**. Per the Avalonia 12 docs:

- Compiled bindings are now the default (1,867% FPS gain on complex layouts)
- BUT only fully effective when `x:CompileBindings="True"` + `x:DataType` are explicit
- Missing `x:DataType` silently falls back to reflection bindings
- `AvaloniaUseCompiledBindingsByDefault=true` MSBuild property enforces at project level

Audit of our 14 AXAML files:
- **0 files** have `x:CompileBindings="True"` explicit
- **13 of 14 files** have at least 1 `x:DataType` somewhere
- **App.axaml + Tokens.axaml** are style/resource-only (no DataContext bindings — exempt)
- **AboutWindow.axaml** has only 1 `x:DataType` despite multiple bindings — possible silent reflection fallback

The win: turn EVERY binding into a compile-time-verified path. Surfaces hidden binding typos. Locks in the Avalonia 12 perf gain at the project level.

## What

3 sub-tasks:

### 4-AV-1: Project-level enforcement
- Add `<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>` to `VPNRouter.App.csproj` `<PropertyGroup>`.

### 4-AV-2: Per-file `x:CompileBindings="True"` + `x:DataType`
- 12 bindable AXAML files (skip App.axaml + Tokens.axaml as style-only):
  - `VPNRouter.App/Views/AboutWindow.axaml` (currently 1 x:DataType — needs root + audit)
  - `VPNRouter.App/Views/MainWindow.axaml` (1 x:DataType — needs root + audit)
  - 10 `VPNRouter.App/Views/Pages/*.axaml` files (each has at least 1 x:DataType)
- Add to root element: `x:CompileBindings="True" x:DataType="vm:<ViewModelType>"`.
- For pages, the root x:DataType is usually `MainWindowViewModel` (the shared DataContext).
- For DataTemplates inside ItemsControls (we have 1 ListBox), add `x:DataType` to each `<DataTemplate>` element.

### 4-AV-3: Fix any compile-time binding errors revealed
- Build immediately after each XAML edit.
- If `Cannot resolve "Foo" on type "VPNRouter.App.ViewModels.MainWindowViewModel"` errors appear:
  - Verify the property exists on the ViewModel (it may be a typo).
  - Verify the property is at the correct partial-class location.
  - If genuinely missing, fix the binding OR add the missing property to the VM.
- DO NOT use `x:CompileBindings="False"` as a workaround — that defeats the purpose. Fix the binding.

## How

**Step 1**: Add the MSBuild property to `VPNRouter.App.csproj`.

**Step 2**: For each of the 12 bindable AXAML files:
1. Read the root element.
2. Find the existing `x:DataType` (most have one already deeper in the file). Identify the ViewModel type.
3. Add to root: `x:CompileBindings="True" x:DataType="vm:<Type>"` where `vm` is `xmlns:vm="clr-namespace:VPNRouter.App.ViewModels"`.
4. Build. Fix errors. Repeat until clean.

**Step 3**: For DataTemplates inside ItemsControls / ListBox:
1. Read MainWindow.axaml's ListBox section (only one we have, lines 735-755).
2. The ListBoxItems use direct `{Binding LblTabXxx}` — already cleanly typed since the parent ListBox inherits root x:DataType.
3. No DataTemplate inside the ListBox to worry about.

**Step 4**: AboutWindow.axaml deep audit:
1. Find all Binding paths.
2. Verify each resolves on AboutWindowViewModel (or whatever the type is).
3. If AboutWindow uses MainWindowViewModel.About sub-state, x:DataType should be the actual context type.

**Step 5**: Build the solution `dotnet build VPNRouter.sln -c Release`. Expected output: 0 errors, 0 binding warnings.

**Step 6**: Run tests:
- Scoped: `dotnet test ... --filter "FullyQualifiedName!~Headless&FullyQualifiedName!~PageScreenshot&FullyQualifiedName!~VisualDiff"`
- HeadlessGuiTests, PageScreenshotTests, VisualDiffTests — separately per VPNRouter.Tests/CLAUDE.md
- Characterization hashes should NOT drift (we're not changing public MVM surface)

**Step 7**: MCP verify — launch the binary, click through all 6 tabs, verify no rendering regression. Compiled bindings should render identically; only the perf characteristic improves (microbench could measure).

## Verification gate
- [ ] MSBuild property added
- [ ] 12 AXAML files have root `x:CompileBindings="True"` + correct `x:DataType`
- [ ] Build 0 errors, 0 binding warnings (binding warnings becoming errors is the WHOLE POINT — verify they're all genuine + fixed)
- [ ] Scoped suite green (1088 → 1088, no test count change since this is XAML-only)
- [ ] Headless + PageScreenshot + VisualDiff suites green
- [ ] Characterization hashes (MVM + AndroidApp) unchanged
- [ ] MCP verify: 6 tabs render identical to v2.34.0-r1

## Outcome
*(filled by agent)*

## Follow-up

- **GroupBox** (new Avalonia 12 control) — cosmetic, can replace `Border` wrapped sections in Settings pages. Defer to Phase 4B.
- **Focus Traversal API** — accessibility improvement. Audit keyboard nav after this lands; defer to Phase 4C if there are issues.
- **CompositionAnimation API** — Avalonia 12 has new compositor; potential to migrate the 2-3 animations we have. Profile first to see if it's worth.
- **Android Avalonia 12** — ph4-android-net10 (separate task).
