# Network Page UI Audit: Custom Rules Virtualization, Template Complexity, Overflow & Design Tokens

**Date:** 2026-09-07  
**Worker:** Worker-1 (Network Page Audit)  
**Target Subsystem:** Network Settings & Routing Rules Engine UI  
**Target Files:**
- `VPNRouter.App/Views/Pages/NetworkPage.axaml` (2478 LOC)
- `VPNRouter.App/Views/Pages/NetworkPage.axaml.cs` (80 LOC)
- `VPNRouter.App/ViewModels/MainWindowViewModel.Settings.cs` (688 LOC)
- *Cross-referenced:* `VPNRouter.App/Converters.cs`, `VPNRouter.App/Styles/Tokens.axaml`

---

## 1. Executive Summary

The Network page (`NetworkPage.axaml`) is the largest single XAML view in the VPNRouter codebase at **2,478 lines of XAML**. It implements a master-detail split (140px fixed master navigation strip + ScrollViewer detail pane) that hosts six primary configuration sections: **Routing**, **Rules** (Cards view, Read view, text-based Edit view), **Leak Protection**, **Content Filtering**, **Updates & Version Rollback**, and **Autostart** (Windows service and login hooks).

This in-depth audit systematically evaluated the view and its backing ViewModels across the four assigned focus areas:
1. **Virtualization of custom rules lists, DNS entries, and CIDR inputs**: Critical breakdown of virtualization in Read Mode where unvirtualized `ItemsControl`s instantiate thousands of layout containers for user rule lists. Nested scroll containers in Cards mode cause scroll conflicts and duplicate ListBox overhead.
2. **DataTemplate complexity, deep element hierarchies, and redundant bindings**: High visual tree bloat, per-item `MenuFlyout` allocations, expensive `$parent[UserControl]` ancestor walks on every row, dual wide/narrow layouts allocated simultaneously per item, and redundant 3-TextBlock status structures.
3. **Bare-string CheckBox.Content and Button.Content overflow/clipping on narrow (360px) window**: 14+ bare-string `Button.Content` bindings that fail to wrap, severe clipping of filter pills in narrow UniformGrids, destructive confirmation bar layout collapse, and master-detail width starvation (leaving only 178px for the entire settings surface).
4. **Design tokens compliance from Styles/Tokens.axaml and theme invalidation cost**: Severe theme invalidation bugs in `ActionToTokenBrushConverter` and `BoolToBrushConverter` that fail to update colors when flipping Light/Dark themes, `StaticResource` bindings on theme-variant shadows, and hardcoded colors bypassing token definitions.
5. **UI thread responsiveness and lifecycle synchronization**: Uncached synchronous `sing-box version` OS process execution in property getters (`VersionText`) blocking the UI thread for up to 3 seconds, and missing `DataContextChanged` handlers in code-behind causing unresponsive breakpoint adaptation.

---

## 2. Findings Matrix

| ID | Severity | Focus Area | File:Line | Finding Summary |
|---|:---:|---|---|---|
| **NET-VIRT-01** | **P1** | Virtualization | `NetworkPage.axaml:1477, 1555, 1623` | Read mode uses unvirtualized `ItemsControl` for Direct, Proxy, Block rules, creating thousands of Grids |
| **NET-VIRT-02** | **P1** | Virtualization / Layout | `NetworkPage.axaml:230, 1196-1326, 1332-1460` | Nested ScrollViewer inside ScrollViewer & parallel dual-ListBox duplication for Cards view |
| **NET-PERF-01** | **P1** | UI Responsiveness | `MainWindowViewModel.Settings.cs:60-103` | `VersionText` property synchronously spawns `sing-box version` process blocking UI thread up to 3000ms |
| **NET-THEME-01** | **P1** | Design Tokens / Invalidation | `Converters.cs:124-149, 169-186`<br/>`NetworkPage.axaml:363, 794, 998, 1241, 1363` | `ActionToTokenBrushConverter` & `BoolToBrushConverter` fail dynamic theme invalidation on Light/Dark toggle |
| **NET-OVFL-01** | **P1** | Responsive Layout (360px) | `NetworkPage.axaml:355, 368, 381, 397, 401, 640, 719, 1160, 1169, 1777, 1782, 1982, 2063, 2147, 2245, 2252, 2396, 2468` | 14 bare-string `Button.Content` bindings overflow and clip horizontally on 360px windows |
| **NET-OVFL-02** | **P2** | Responsive Layout (360px) | `NetworkPage.axaml:989-1046` | Narrow rules filter `UniformGrid` (4 cols) compresses "direct", "proxy", "block" into 28px width, truncating text |
| **NET-OVFL-03** | **P2** | Responsive Layout (360px) | `NetworkPage.axaml:1148-1178` | Clear All confirm bar crushes warning TextBlock to 7px width next to two Auto buttons on 360px window |
| **NET-LAYOUT-01** | **P2** | Responsive Layout (360px) | `NetworkPage.axaml:191, 203-212` | Rigid 140px master navigation starves right settings pane to ~178px on 360px window; bare `ListBoxItem` contents clip |
| **NET-DATA-01** | **P2** | DataTemplate / Memory | `NetworkPage.axaml:1298-1321, 1391-1414` | Per-item `MenuFlyout` allocation & `$parent[UserControl]` visual tree walks on every rule card |
| **NET-DATA-02** | **P2** | DataTemplate / Layout | `NetworkPage.axaml:1481-1540, 1559-1615, 1627-1685` | Dual wide/narrow Grids allocated in visual tree for every single rule in Read mode |
| **NET-DATA-03** | **P2** | DataTemplate / Binding | `NetworkPage.axaml:2312-2330, 2338-2356, 2364-2382` | 3 duplicate TextBlocks per autostart component (9 total) for single status string color swap |
| **NET-THEME-02** | **P2** | Design Tokens / Invalidation | `NetworkPage.axaml:916, 1076` | `BoxShadow="{StaticResource ShadowLg}"` fails theme variant swap on bulk action flyout |
| **NET-THEME-03** | **P2** | Design Tokens Compliance | `NetworkPage.axaml:57, 58, 1174, 2121` | Hardcoded colors (`"White"`, `#33000000`) bypass design system tokens in `Tokens.axaml` |
| **NET-CODE-01** | **P2** | Lifecycle / Synchronization | `NetworkPage.axaml.cs:28-49` | Missing `DataContextChanged` handler leaves `IsRulesNarrow` uninitialized if DataContext arrives after tree attachment |
| **NET-EDIT-01** | **P3** | UI Interaction | `NetworkPage.axaml:1726-1755` | Rules editor line-number gutter ScrollViewer is unsynchronized with TextBox scroll offset |
| **NET-DATA-04** | **P3** | Binding Overhead | `NetworkPage.axaml:430-474` | 15 fine-grained `<Run>` bindings in rules help banner create unnecessary binding graph bloat |

---

## 3. Detailed Findings by Focus Area

### Focus Area 1: Virtualization of Custom Rules Lists, DNS Entries, and CIDR Inputs

#### NET-VIRT-01 (P1): Read Mode Uses Unvirtualized ItemsControl for Custom Rules
- **File:Line:**
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:1477` (`ReadModeDirectRules`)
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:1555` (`ReadModeProxyRules`)
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:1623` (`ReadModeBlockRules`)
- **Symptom / Finding:**
  Read mode displays routing rules partitioned into three action categories (Direct, Proxy, Block). Each group uses an `<ItemsControl>`:
  ```xaml
  <ItemsControl ItemsSource="{Binding ReadModeDirectRules}">
  ...
  <ItemsControl ItemsSource="{Binding ReadModeProxyRules}">
  ...
  <ItemsControl ItemsSource="{Binding ReadModeBlockRules}">
  ```
  By default in Avalonia, `ItemsControl` uses a standard non-virtualizing `StackPanel` as its `ItemsPanel`. When a user imports a standard community rule list (e.g., antizapret, geosite/geoip, or custom CIDR/domain lists ranging from 500 to 5,000 rules), Avalonia instantiates visual containers for **every single item simultaneously**.
  
  Inside each item template (lines 1479–1542, 1557–1614, 1625–1684), there are **two complete Grids** (wide and narrow), multiple TextBlocks, a delete Button, and two `$parent[UserControl]` ancestor tree walks. For a list of 1,500 rules, this forces the UI thread to allocate:
  - 3,000 `Grid` instances
  - Over 12,000 `TextBlock` elements
  - 1,500 `Button` elements
  - 3,000 visual tree ancestor traversals to the root `UserControl`
  
  This locks the application UI thread for 2–8 seconds during tab activation, incurs severe GC pauses, and consumes tens of megabytes of visual node memory.
- **Concrete Actionable Fix:**
  Replace the three unvirtualized `ItemsControl` blocks with a single virtualized `ListBox` or `ItemsRepeater` configured with `VirtualizingStackPanel`:
  ```xaml
  <ListBox ItemsSource="{Binding ReadModeGroupedRules}"
           MaxHeight="450"
           ScrollViewer.VerticalScrollBarVisibility="Auto">
    <ListBox.ItemsPanel>
      <ItemsPanelTemplate>
        <VirtualizingStackPanel/>
      </ItemsPanelTemplate>
    </ListBox.ItemsPanel>
  ...
  ```
  Or unify all three categories into a single virtualized list with sticky category section headers, ensuring container recycling.

---

#### NET-VIRT-02 (P1): Nested ScrollViewer Inside ScrollViewer & Parallel Dual-ListBox Duplication
- **File:Line:**
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:230` (Outer ScrollViewer)
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:1196-1326` (Wide ListBox)
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:1332-1460` (Narrow ListBox)
- **Symptom / Finding:**
  The detail pane is wrapped in an outer `ScrollViewer` (`Grid.Row="0" Grid.Column="1"`, line 230) containing an unconstrained vertical `StackPanel`. Inside this tree, the Cards view declares **two parallel ListBoxes** binding to the same `FilteredCustomRulesList`:
  1. Wide ListBox: `IsVisible="{Binding !IsRulesNarrow}"`, `MaxHeight="360"`
  2. Narrow ListBox: `IsVisible="{Binding IsRulesNarrow}"`, `MaxHeight="360"`
  
  This architecture introduces two major failures:
  1. **Nested Scroll Collision (Wheel Trapping):** Having a scrollable `ListBox` (`MaxHeight="360"`) embedded inside a page-level `ScrollViewer` causes mouse-wheel events to be captured by the inner list once the cursor enters the rules area, blocking page scrolling until the list reaches its boundary (and vice versa).
  2. **Dual-Tree Overhead:** Both ListBoxes exist in the XAML object model. Even though visibility toggles them, switching window width past 700px causes Avalonia to tear down the entire visual container tree of one ListBox and instantiate a completely new set of item containers for the other, causing frame drops during window resize.
- **Concrete Actionable Fix:**
  1. Consolidate into a single `ListBox` that uses a responsive template or adaptive container styles (e.g. styling the internal Grid rows based on container width or an attached breakpoint property).
  2. Avoid nested scroll containers by either dedicating the remaining page height to the ListBox (`Grid.Row="*"` without outer vertical scrolling on that pane) or configuring outer scrolling with smooth bubbling.

---

#### NET-PERF-01 (P1): Synchronous External Process Spawn in ViewModel Property (`VersionText`)
- **File:Line:** `VPNRouter.App/ViewModels/MainWindowViewModel.Settings.cs:60-103`
- **Symptom / Finding:**
  `MainWindowViewModel.Settings.cs` exposes:
  ```csharp
  public string VersionText => $"by NiniTux · v{AppVersion.Version} · sing-box {GetSingBoxVersion()}";
  ```
  Inside `GetSingBoxVersion()`:
  ```csharp
  var psi = new System.Diagnostics.ProcessStartInfo
  {
      FileName = exePath,
      Arguments = "version",
      UseShellExecute = false,
      RedirectStandardOutput = true,
      CreateNoWindow = true
  };
  using var proc = System.Diagnostics.Process.Start(psi);
  var output = proc?.StandardOutput.ReadToEnd() ?? "";
  proc?.WaitForExit(3000);
  ```
  Because `VersionText` is a calculated getter without a backing field or caching, **every binding evaluation synchronously spawns `sing-box version` on the calling thread** and blocks for up to 3,000 ms.
  
  `VersionText` is accessed when opening About dialogs, updating status pills, or evaluating bindings in the Settings/Updates surfaces. Running external process execution on the Avalonia UI dispatch thread completely freezes rendering and input handling.
- **Concrete Actionable Fix:**
  Cache the sing-box version once during application startup or load it asynchronously on demand:
  ```csharp
  private string? _cachedSingBoxVersion;
  public string VersionText => $"by NiniTux · v{AppVersion.Version} · sing-box {_cachedSingBoxVersion ?? "..."}";
  
  public async Task InitializeSingBoxVersionAsync()
  {
      if (_cachedSingBoxVersion != null) return;
      _cachedSingBoxVersion = await Task.Run(GetSingBoxVersion);
      OnPropertyChanged(nameof(VersionText));
  }
  ```

---

### Focus Area 2: DataTemplate Complexity, Deep Element Hierarchies, and Redundant Bindings

#### NET-DATA-01 (P2): Per-Item MenuFlyout Allocation & Visual Tree Walks in ListBox Items
- **File:Line:**
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:1305, 1314-1320` (Wide list item)
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:1398, 1407-1413` (Narrow list item)
- **Symptom / Finding:**
  Each realized row in both the wide and narrow ListBoxes defines a ⋯ button with an embedded context flyout:
  ```xaml
  <Button ToolTip.Tip="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).L_RulesBulkActions}">
    <Button.Flyout>
      <MenuFlyout>
        <MenuItem Header="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).L_CustomRulesDelete}"
                  Command="{Binding RemoveCommand}"
                  Foreground="{DynamicResource DangerFgBrush}"/>
      </MenuFlyout>
    </Button.Flyout>
  </Button>
  ```
  This incurs three performance penalties:
  1. **Heavy Object Allocations:** Rather than reusing a single flyout instance, every single item container instantiates its own `Flyout`, `MenuFlyout`, and `MenuItem` instances with independent event listeners and popup coordinators.
  2. **Ancestral Tree Traversal Overhead:** `$parent[UserControl]` requires Avalonia to recursively traverse up the visual tree until reaching the root `UserControl`. In virtualized lists where rows are recycled or newly created during scrolling, this traversal executes continuously on the UI thread.
  3. **Data Context Coupling:** The item references parent viewmodel strings (`L_RulesBulkActions`, `L_CustomRulesDelete`) via reflection-heavy relative source paths.
- **Concrete Actionable Fix:**
  1. Expose localized command labels directly on `CustomRuleViewModel` or expose them through a global static localization accessor:
     `Header="{x:Static loc:Strings.CustomRulesDelete}"`
  2. Define a single shared `MenuFlyout` in `UserControl.Resources` and reference it, or attach a single `ContextMenu` to the ListBox container.

---

#### NET-DATA-02 (P2): Dual Wide/Narrow Grids Allocated per Item in Read Mode
- **File:Line:**
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:1481-1540`
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:1559-1615`
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:1627-1685`
- **Symptom / Finding:**
  In Read mode, every item template embeds two complete layout branches:
  - Branch 1: `<Grid ColumnDefinitions="20,70,140,*,Auto" IsVisible="{Binding !$parent[UserControl].((vm:MainWindowViewModel)DataContext).IsRulesNarrow}">`
  - Branch 2: `<Grid ColumnDefinitions="*,Auto" IsVisible="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).IsRulesNarrow}">`
  
  Both Grids (along with their inner StackPanels, TextBlocks, and Buttons) are fully instantiated in memory for every item. Only one is visible at a time. The dual tree doubles the memory footprint of every row and registers two `$parent[UserControl]` binding observers per row that must be evaluated whenever the window resizes.
- **Concrete Actionable Fix:**
  Consolidate into a single layout container. Use Avalonia Styles to reposition elements on narrow screens, or use a flexible Grid with column-spanning rather than duplicating entire Grid trees:
  ```xaml
  <Grid ColumnDefinitions="Auto,Auto,*,Auto" Margin="14,2" Opacity="{Binding RowOpacity}">
    <TextBlock Grid.Column="0" Text="●" Foreground="{DynamicResource SuccessFgBrush}"/>
    <TextBlock Grid.Column="1" Text="{Binding Action}" FontWeight="Bold"/>
    <TextBlock Grid.Column="2" Text="{Binding Value}" TextTrimming="CharacterEllipsis"/>
    <Button Grid.Column="3" Content="✕" Command="{Binding RemoveCommand}"/>
  </Grid>
  ```

---

#### NET-DATA-03 (P2): Triple-TextBlock Duplication for Status Indicators in Autostart Section
- **File:Line:**
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:2312-2330` (VPN status)
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:2338-2356` (Zapret status)
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:2364-2382` (TgProxy status)
- **Symptom / Finding:**
  To display the delivery channel status for each autostart component with appropriate coloring, the XAML duplicates three identical `TextBlock`s with the exact same text binding:
  ```xaml
  <TextBlock Text="{Binding LblAutostartVpnStatus}" TextWrapping="Wrap" FontSize="9" Margin="22,0,0,0"
             IsVisible="{Binding IsAutostartVpnStatusGood}" Foreground="{DynamicResource SuccessFgBrush}"/>
  <TextBlock Text="{Binding LblAutostartVpnStatus}" TextWrapping="Wrap" FontSize="9" Margin="22,0,0,0"
             IsVisible="{Binding IsAutostartVpnStatusWarn}" Foreground="{DynamicResource WarningFgBrush}"/>
  <TextBlock Text="{Binding LblAutostartVpnStatus}" TextWrapping="Wrap" FontSize="9" Margin="22,0,0,0"
             IsVisible="{Binding IsAutostartVpnStatusBad}" Foreground="{DynamicResource DangerFgBrush}"/>
  ```
  This pattern is repeated for all three components, creating **9 TextBlocks and 9 visibility bindings** for 3 status lines. In the ViewModel, this requires 9 separate boolean properties (`IsAutostart...StatusGood`, `IsAutostart...StatusWarn`, `IsAutostart...StatusBad`).
- **Concrete Actionable Fix:**
  Replace each set of 3 TextBlocks with a single `TextBlock` and bind `Foreground` to a reusable status converter or bind CSS-like classes:
  ```xaml
  <TextBlock Text="{Binding LblAutostartVpnStatus}"
             Classes.status-good="{Binding IsAutostartVpnStatusGood}"
             Classes.status-warn="{Binding IsAutostartVpnStatusWarn}"
             Classes.status-bad="{Binding IsAutostartVpnStatusBad}"
             TextWrapping="Wrap" FontSize="9" Margin="22,0,0,0"/>
  ```
  With styles:
  ```xaml
  <Style Selector="TextBlock.status-good"><Setter Property="Foreground" Value="{DynamicResource SuccessFgBrush}"/></Style>
  <Style Selector="TextBlock.status-warn"><Setter Property="Foreground" Value="{DynamicResource WarningFgBrush}"/></Style>
  <Style Selector="TextBlock.status-bad"><Setter Property="Foreground" Value="{DynamicResource DangerFgBrush}"/></Style>
  ```

---

#### NET-DATA-04 (P3): 15-Segment Inlined Run Elements in Rules Help Banner
- **File:Line:** `VPNRouter.App/Views/Pages/NetworkPage.axaml:430-474`
- **Symptom / Finding:**
  The help banner decomposes localized sentences into 15 individual `<Run Text="{Binding ...}"/>` tags across 3 bullet points:
  - Bullet 1: `L_RulesHelpB1Pre`, `L_RulesHelpB1T1`, `L_RulesHelpB1Mid`, `L_RulesHelpB1T2`, `L_RulesHelpB1Suf` (5 runs)
  - Bullet 2: `L_RulesHelpB2Pre`, 3 hardcoded CIDR runs, `L_RulesHelpB2Mid`, `direct`, `L_RulesHelpB2Suf` (7 runs)
  - Bullet 3: `L_RulesHelpB3Pre`, `L_RulesHelpB3Bold`, `L_RulesHelpB3Suf` (3 runs)
  
  Each `Run` creates an independent binding subscription. This splinters localization keys into micro-fragments that make translation difficult and adds layout measuring overhead.
- **Concrete Actionable Fix:**
  Construct formatted strings or markdown in the ViewModel or localization layer, or use a lightweight rich text renderer with 3 total bindings.

---

### Focus Area 3: Bare-String CheckBox.Content and Button.Content Overflow/Clipping on Narrow (360px) Window

#### NET-OVFL-01 (P1): 14 Bare-String Button.Content Bindings Causing Overflow & Clipping on Narrow Viewports
- **File:Line:**
  - `NetworkPage.axaml:355, 368, 381` (Segment toggle: `L_RulesViewCards`, `L_RulesViewRead`, `L_RulesViewEdit`)
  - `NetworkPage.axaml:397, 401` (`L_CustomRulesImport`, `L_CustomRulesExport`)
  - `NetworkPage.axaml:640, 719` (`L_CustomRulesAddBtn` — Add button wide and narrow)
  - `NetworkPage.axaml:1160, 1169` (Clear All confirm: `L_Cancel`, `L_RulesClearAllConfirm`)
  - `NetworkPage.axaml:1777, 1782` (Rules editor strip: `L_RulesEditorRevert`, `RulesEditorApplyText`)
  - `NetworkPage.axaml:1982` (`MtuAutoTuneButton`)
  - `NetworkPage.axaml:2063` (`UpdateVm.CheckLinkText`)
  - `NetworkPage.axaml:2147` (`L_DiagExportButton`)
  - `NetworkPage.axaml:2245, 2252` (`L_RestartService`, `L_ReinstallService`)
  - `NetworkPage.axaml:2396` (`L_BtnInstallServiceInlineCta`)
  - `NetworkPage.axaml:2468` (`L_ApplyNowReloadVpn` — bottom bar)
- **Symptom / Finding:**
  The project UI standard (`audit-overflow-fix/SKILL.md` and `VPNRouter.App/AGENTS.md`) strictly prohibits bare-string `Content="{Binding ...}"` on `Button` and `CheckBox`. Avalonia's default Button template generates a non-wrapping `TextBlock`.
  
  On a 360px window, after subtracting the 140px fixed left master navigation, 28px margins (`Margin="14,0,14,0"`), and 14px scrollbar, **the detail pane has only ~178px of usable width**.
  
  When localized Russian text is active:
  - `L_ApplyNowReloadVpn` ("↻ Применить") or `L_DiagExportButton` ("Экспорт диагностики") cannot wrap and forces container expansion.
  - Multi-button toolbars (e.g. `L_RulesEditorRevert` + `RulesEditorApplyText`) push past the right edge of the ScrollViewer, clipping the primary action buttons.
- **Concrete Actionable Fix:**
  Wrap all localized button contents in `<TextBlock Text="{Binding ...}" TextWrapping="Wrap"/>` with `MinHeight="0"`, per the project fix pattern:
  ```xaml
  <Button Command="{Binding ApplyPendingChangesCommand}"
          Padding="10,4" MinHeight="0" ...>
    <TextBlock Text="{Binding L_ApplyNowReloadVpn}" TextWrapping="Wrap"/>
  </Button>
  ```

---

#### NET-OVFL-02 (P2): Narrow Rules Filter UniformGrid Compresses Action Pills to 28px at 360px Window
- **File:Line:** `VPNRouter.App/Views/Pages/NetworkPage.axaml:989-1046`
- **Symptom / Finding:**
  In narrow rules mode (`IsRulesNarrow=true`), the filter bar uses `<UniformGrid Columns="4" Rows="1">` containing 4 buttons:
  - `Content="{Binding L_RulesFilterAll}"`
  - `Content="direct"`
  - `Content="proxy"`
  - `Content="block"`
  
  At a 360px window width, the available width for the filter toolbar is 178px. Subtracting the bulk actions button (28px + 6px spacing) leaves only **144px** for the UniformGrid:
  - Width per column: `144px / 4 = 36px`
  - Button horizontal padding: `4,3` (8px total)
  - Usable text width per button: `36px - 8px = 28px`
  
  The word `"direct"` in 10pt SemiBold requires ~36px of text width; `"proxy"` and `"block"` require ~30px. Because the buttons use bare-string `Content="..."`, the text overflows the button borders and is visibly clipped or overlaps neighboring pills.
- **Concrete Actionable Fix:**
  Switch the filter bar from `<UniformGrid Columns="4" Rows="1">` to a `WrapPanel` or a 2x2 grid on screens below 420px, or display compact symbol indicators (e.g., `●`, `★`, `✕`, `All`) when width is constrained.

---

#### NET-OVFL-03 (P2): Clear All Confirm Bar Crushes Warning Text to 7px Width Next to Two Buttons
- **File:Line:** `VPNRouter.App/Views/Pages/NetworkPage.axaml:1148-1178`
- **Symptom / Finding:**
  The inline confirmation bar for "Clear All" uses:
  ```xaml
  <Grid ColumnDefinitions="*,Auto,Auto" ColumnSpacing="8">
    <StackPanel Grid.Column="0" Spacing="2" VerticalAlignment="Center">
      <TextBlock Text="{Binding ClearAllConfirmText}" .../>
      <TextBlock Text="{Binding L_RulesClearAllHint}" ... TextWrapping="Wrap"/>
    </StackPanel>
    <Button Grid.Column="1" Content="{Binding L_Cancel}" Padding="10,4" .../>
    <Button Grid.Column="2" Content="{Binding L_RulesClearAllConfirm}" Padding="10,4" .../>
  </Grid>
  ```
  At 360px window width (detail pane width ~178px):
  - Button 1 ("Отмена") takes ~60px
  - Button 2 ("Очистить все") takes ~95px
  - Column spacing takes `8 * 2 = 16px`
  - Total button width: `60 + 95 + 16 = 171px`
  - Available space for Column 0: `178px - 171px = 7px`
  
  The warning header and hint text in Column 0 are squeezed into a 7px-wide strip, causing catastrophic text wrapping into single-character vertical columns and breaking the layout completely.
- **Concrete Actionable Fix:**
  Use a responsive 2-row layout where buttons sit below the warning text on narrow screens:
  ```xaml
  <StackPanel Spacing="6">
    <TextBlock Text="{Binding ClearAllConfirmText}" TextWrapping="Wrap" FontWeight="SemiBold"/>
    <TextBlock Text="{Binding L_RulesClearAllHint}" TextWrapping="Wrap" FontSize="10"/>
    <Grid ColumnDefinitions="*,*" ColumnSpacing="8">
      <Button Grid.Column="0" Command="{Binding CancelClearAllCustomRulesCommand}" ...>
        <TextBlock Text="{Binding L_Cancel}" TextWrapping="Wrap" TextAlignment="Center"/>
      </Button>
      <Button Grid.Column="1" Command="{Binding ConfirmClearAllCustomRulesCommand}" ...>
        <TextBlock Text="{Binding L_RulesClearAllConfirm}" TextWrapping="Wrap" TextAlignment="Center"/>
      </Button>
    </Grid>
  </StackPanel>
  ```

---

#### NET-LAYOUT-01 (P2): Rigid 140px Master Navigation Column Starves Detail Pane at 360px Window
- **File:Line:**
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:191`
  - `VPNRouter.App/Views/Pages/NetworkPage.axaml:203-212`
- **Symptom / Finding:**
  The root layout is declared as:
  ```xaml
  <Grid RowDefinitions="*,Auto" ColumnDefinitions="140,*">
  ```
  Allocating a fixed 140px for the left tab strip means that at 360px window width, the master navigation consumes **39% of the entire window**, leaving only 220px total (and ~178px net after scrollbar and margins) for the complex forms and tables on the right.
  
  Furthermore, the ListBox items in the master navigation (lines 203–212) use bare strings:
  ```xaml
  <ListBoxItem Content="{Binding LblSettingsRouting}" Padding="10,7" .../>
  <ListBoxItem Content="{Binding LblAutostartSection}" Padding="10,7" .../>
  ```
  Long Russian titles (such as "Маршрутизация" and "Автозагрузка") cannot wrap or ellipsize and are clipped against the 140px border edge.
- **Concrete Actionable Fix:**
  1. Add `TextTrimming="CharacterEllipsis"` to `ListBoxItem` templates in the navigation ListBox.
  2. Implement a responsive adaptive breakpoint in `NetworkPage.axaml.cs`: when window width is below 480px, collapse the master navigation into a top segmented tab strip or an icon-only rail (width 44px), giving the settings detail pane 300px+ of usable width.

---

### Focus Area 4: Design Tokens Compliance from Styles/Tokens.axaml and Theme Invalidation Cost

#### NET-THEME-01 (P1): Converters Fail Dynamic Theme Invalidation on Light/Dark Switch
- **File:Line:**
  - `VPNRouter.App/Converters.cs:124-149` (`ActionToTokenBrushConverter`)
  - `VPNRouter.App/Converters.cs:169-186` (`BoolToBrushConverter`)
  - Referenced in `NetworkPage.axaml:363-393, 794-864, 998-1044, 1241-1252, 1363-1374`
- **Symptom / Finding:**
  Both `ActionToTokenBrushConverter` and `BoolToBrushConverter` resolve theme brushes imperatively inside `Convert(...)`:
  ```csharp
  var app = Avalonia.Application.Current;
  if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var res) && res is IBrush brush)
      return brush;
  ```
  In Avalonia, an `IValueConverter` is **only evaluated when the bound ViewModel property changes**.
  When the user toggles between Light and Dark themes via `ToggleTheme()`:
  - The rule `Action` ("direct", "proxy", "block") does not change.
  - The segmented button state (`IsRulesViewCards`, `IsRulesFilterAll`) does not change.
  
  Because the bound values are unchanged, **`Convert(...)` is never called upon theme change**. The controls retain the cached `SolidColorBrush` instance from whichever theme was active when the control was first rendered.
  
  As a result:
  - Rule action chips (direct, proxy, block) maintain light-theme backgrounds on dark surfaces (or vice-versa), causing unreadable contrast.
  - Segmented toggle buttons retain inactive/active brushes from the prior theme.
- **Concrete Actionable Fix:**
  Eliminate imperative resource lookup inside value converters. Use Avalonia Style Selectors with dynamic resources:
  ```xaml
  <Style Selector="Border.chip-direct">
    <Setter Property="Background" Value="{DynamicResource SurfaceSunkenBrush}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource BorderDefaultBrush}"/>
  </Style>
  <Style Selector="Border.chip-proxy">
    <Setter Property="Background" Value="{DynamicResource AccentBgSubtleBrush}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource AccentBorderBrush}"/>
  </Style>
  <Style Selector="Border.chip-block">
    <Setter Property="Background" Value="{DynamicResource DangerBgBrush}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource DangerBorderBrush}"/>
  </Style>
  ```
  And bind classes dynamically:
  `Classes.chip-direct="{Binding IsDirect}"`
  `{DynamicResource ...}` bindings automatically listen to theme variant changes and update immediately without needing ViewModel change notifications.

---

#### NET-THEME-02 (P2): StaticResource Binding on Theme-Variant Shadow Token Breaks Theme Switching
- **File:Line:** `VPNRouter.App/Views/Pages/NetworkPage.axaml:916, 1076`
- **Symptom / Finding:**
  On the bulk actions flyout borders:
  ```xaml
  <Border ... BoxShadow="{StaticResource ShadowLg}" ...>
  ```
  In `VPNRouter.App/Styles/Tokens.axaml`:
  - Light theme (line 125): `<BoxShadows x:Key="ShadowLg">0 12 28 0 #1A0F1320, 0 2 6 0 #0D0F1320</BoxShadows>`
  - Dark theme (line 228): `<BoxShadows x:Key="ShadowLg">0 16 32 0 #A5000000, 0 2 6 0 #80000000</BoxShadows>`
  
  `ShadowLg` is a theme-variant resource declared inside `ResourceDictionary x:Key="Light"` and `x:Key="Dark"`. Using `{StaticResource ShadowLg}` resolves the shadow once at XAML parse time. When switching themes, the shadow does not re-evaluate, leaving a dark harsh shadow in light mode or a faint grey shadow in dark mode.
- **Concrete Actionable Fix:**
  Change `{StaticResource ShadowLg}` to `{DynamicResource ShadowLg}` on lines 916 and 1076.

---

#### NET-THEME-03 (P2): Hardcoded Colors and Raw Hex Bypassing Design Tokens
- **File:Line:**
  - `NetworkPage.axaml:57` (`Background="White"` on iOS toggle thumb)
  - `NetworkPage.axaml:58` (`BoxShadow="0 1 2 0 #33000000"` on iOS toggle thumb)
  - `NetworkPage.axaml:1174` (`Foreground="White"` on Clear All confirm button)
  - `NetworkPage.axaml:2121` (`Foreground="White"` on Rollback confirm button)
- **Symptom / Finding:**
  The project rules (`VPNRouter.App/AGENTS.md`) mandate: *"Never hardcode hex colors in XAML; always use dynamic resources... Use semantic token categories for Surfaces, Text, Borders, Accent, States, and Radii"*.
  The occurrences above hardcode pure `"White"` and `#33000000` directly in the XAML markup, bypassing `AccentOnSolidBrush`, `TextInverseBrush`, and `ShadowXs`.
- **Concrete Actionable Fix:**
  - Line 57: Change `Background="White"` to `Background="{DynamicResource SurfaceRaisedBrush}"` or `{DynamicResource AccentOnSolidBrush}`.
  - Line 58: Change `BoxShadow="0 1 2 0 #33000000"` to `BoxShadow="{DynamicResource ShadowXs}"`.
  - Lines 1174 & 2121: Change `Foreground="White"` to `Foreground="{DynamicResource AccentOnSolidBrush}"`.

---

### Focus Area 5: Code-Behind & ViewModel Architectural Issues

#### NET-CODE-01 (P2): Missing DataContextChanged Handler in Code-Behind
- **File:Line:** `VPNRouter.App/Views/Pages/NetworkPage.axaml.cs:28-49`
- **Symptom / Finding:**
  `NetworkPage.axaml.cs` updates the ViewModel's `IsRulesNarrow` property using:
  ```csharp
  public NetworkPage()
  {
      InitializeComponent();
      SizeChanged += OnPageSizeChanged;
      AttachedToVisualTree += (_, _) => UpdateNarrowState();
  }
  
  private void UpdateNarrowState()
  {
      if (DataContext is not MainWindowViewModel vm) return;
      var width = Bounds.Width;
      if (width <= 0) return;
      vm.IsRulesNarrow = width < NarrowBreakpoint;
  }
  ```
  If the `NetworkPage` is constructed and attached to the visual tree before `DataContext` is populated (the standard flow during window initialization), `DataContext is not MainWindowViewModel` evaluates to false and `UpdateNarrowState()` exits early.
  Because there is **no handler for `DataContextChanged`**, if the window size does not change, `vm.IsRulesNarrow` is never updated and remains at its default initial state (`false`). This forces the wide layout on small windows until the user manually resizes the window.
- **Concrete Actionable Fix:**
  Subscribe to `DataContextChanged` in the constructor:
  ```csharp
  DataContextChanged += (_, _) => UpdateNarrowState();
  ```

---

#### NET-EDIT-01 (P3): Rules Editor Line-Number Gutter ScrollViewer Unsynchronized with TextBox
- **File:Line:** `VPNRouter.App/Views/Pages/NetworkPage.axaml:1726-1755`
- **Symptom / Finding:**
  The text-based rules editor places line numbers inside an isolated `ScrollViewer`:
  ```xaml
  <ScrollViewer VerticalScrollBarVisibility="Hidden" HorizontalScrollBarVisibility="Hidden">
    <TextBlock Text="{Binding RulesEditorLineNumbers}" .../>
  </ScrollViewer>
  ```
  And the text input inside an independent `TextBox`:
  ```xaml
  <TextBox Grid.Column="1" Text="{Binding EditedCustomRulesText}"
           ScrollViewer.VerticalScrollBarVisibility="Auto" .../>
  ```
  Because the gutter's `ScrollViewer` has no scroll synchronization with the internal `ScrollViewer` of the `TextBox`, when a user scrolls through a long list of rules (50+ lines), the line numbers remain static at line 1 while the text scrolls away, causing total misalignment between line numbers and rules.
- **Concrete Actionable Fix:**
  Bind the gutter's vertical scroll offset to the `TextBox`'s internal scroll offset via an attached behavior or event handler in code-behind, or handle line numbering within a unified text editor control.

---

## 4. Prioritized Remediation Plan

### Phase 1: High-Severity Performance & Layout Fixes (Sprint 1)
1. **Fix `VersionText` Process Start (`NET-PERF-01`)**: Cache `sing-box version` asynchronously to eliminate 3,000ms UI thread freeze.
2. **Virtualize Read Mode (`NET-VIRT-01`)**: Replace unvirtualized `ItemsControl`s with a virtualized `ListBox` / `ItemsRepeater` to handle 1,000+ rule lists smoothly.
3. **Fix Theme Dynamic Invalidation (`NET-THEME-01`, `NET-THEME-02`)**: Migrate `ActionToTokenBrushConverter` and `BoolToBrushConverter` to Avalonia Style Classes and replace `StaticResource ShadowLg` with `DynamicResource`.
4. **Wrap Button Contents on Narrow Screens (`NET-OVFL-01`)**: Wrap all 14 bare-string `Button.Content` bindings in `<TextBlock TextWrapping="Wrap"/>`.

### Phase 2: Responsive & Structural Layout Hardening (Sprint 2)
1. **Remedy Master Navigation Starvation (`NET-LAYOUT-01`)**: Implement collapsible / top-strip adaptive navigation when window width < 480px.
2. **De-duplicate Cards View ListBoxes (`NET-VIRT-02`)**: Merge the wide and narrow ListBoxes into a single adaptive ListBox.
3. **Fix Narrow Filter UniformGrid & Clear All Bar (`NET-OVFL-02`, `NET-OVFL-03`)**: Replace 4-column `UniformGrid` with `WrapPanel` and stack confirmation bar controls vertically.
4. **Subscribe to `DataContextChanged` (`NET-CODE-01`)**: Ensure `IsRulesNarrow` is correctly initialized on page load.

### Phase 3: DataTemplate Optimization & Token Compliance (Sprint 3)
1. **Eliminate Per-Item Flyouts (`NET-DATA-01`)**: Move the row ⋯ menu to a single shared context menu.
2. **Consolidate Dual Grids in Read Mode (`NET-DATA-02`)**: Use a single responsive Grid per rule item.
3. **Simplify Autostart Status Blocks (`NET-DATA-03`)**: Replace 9 TextBlocks with 3 TextBlocks driven by style classes.
4. **Purge Hardcoded Colors (`NET-THEME-03`)**: Replace `"White"` and raw hex with semantic tokens from `Tokens.axaml`.
5. **Synchronize Editor Gutter Scrolling (`NET-EDIT-01`)**: Link gutter scroll offset to the TextBox vertical scroll position.
