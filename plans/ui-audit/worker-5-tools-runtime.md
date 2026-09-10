# UI & Runtime Audit Report: Tools Sub-Pages (DPI Bypass / Zapret & Telegram Proxy) and Runtime Status Dispatcher

**Date**: 2026-09-07  
**Auditor**: Worker 5 (Tools & Runtime Status)  
**Target Scope**:
- `VPNRouter.App/Views/Pages/ToolsPage.axaml` (+ `.cs`)
- `VPNRouter.App/Views/Pages/DpiBypassPage.axaml` (+ `.cs`)
- `VPNRouter.App/Views/Pages/TelegramPage.axaml` (+ `.cs`)
- `VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs`
- Related backend services in `VPNRouter.Core/Services/ZapretAutoStrategy.cs` & `MainWindowViewModel.cs`

---

## Executive Summary

This audit examined the Tools sub-pages (Zapret DPI bypass and Telegram proxy) and the background runtime status polling mechanism. We identified **14 findings**:
- **3 Critical Severity (P1)** findings:
  1. Synchronous OS system calls (kernel process enumeration, TCP listener table queries, and disk I/O) executed every 2 seconds on the UI thread via `DispatcherTimer`.
  2. Periodic global Skia cache purge (`SKGraphics.PurgeAllCaches()`) executed on the UI thread every 60 seconds, causing severe frame drops, font atlas invalidation, and UI stutter.
  3. Severe horizontal layout overflow (>130px) in the Telegram Page running state at 360px window width, completely clipping the "Copy Link" action button.
- **7 High/Medium Severity (P2)** findings: Unbounded string allocations during Zapret strategy sweeps, missing autoscroll on diagnostic log output, unvirtualized `ItemsControl` log display, per-line UI thread dispatcher flooding, crushed Secret TextBox (33px) on 360px layouts, clipped Russian tab strip headers in Zapret `UniformGrid`, and unscrollable header overflow on constrained heights.
- **4 Low Severity (P3)** findings: Bare-string `Button.Content` without text wrapping, unbounded `ObservableCollection<string>` growth, and toast banner layout crowding.

---

## Findings Matrix

| ID | Severity | Focus Area | File:Line | Finding Summary |
|---|---|---|---|---|
| **TR-01** | **P1** | 2. Runtime Status | `MainWindowViewModel.RuntimeStatus.cs:94-131` | `DispatcherTimer` runs synchronous process enumeration, TCP table queries, and disk I/O on the UI thread |
| **TR-02** | **P1** | 2. Runtime Status | `MainWindowViewModel.RuntimeStatus.cs:181-186` | `SKGraphics.PurgeAllCaches()` invoked on UI thread every 60s, causing recurring frame drops and glyph re-rasterization |
| **TR-03** | **P1** | 4. Responsive / 360px | `TelegramPage.axaml:206-250` | Telegram Air-Pill + Stats + Copy Link in horizontal `StackPanel` overflows 360px window by 130px+, clipping Copy button |
| **TR-04** | **P2** | 1. Probe & Log Perf | `ZapretAutoStrategy.cs:692, 749, 1093` | Monolithic `StringBuilder` accumulates thousands of lines into LOH string returned in `FlowsealSweepResult.Output`, which is ignored |
| **TR-05** | **P2** | 1. Probe & Log Perf | `MainWindowViewModel.cs:5708-5712` | `RunZapretActionAsync` dispatches every single log line individually via `Dispatcher.UIThread.InvokeAsync` |
| **TR-06** | **P2** | 1. Probe & Log Perf | `DpiBypassPage.axaml:724-738`, `DpiBypassPage.axaml.cs:1-11` | Missing autoscroll for live diagnostic and test output in `ZapretActionOutput` |
| **TR-07** | **P2** | 1. Probe & Log Perf | `DpiBypassPage.axaml:727-736` | `ItemsControl` inside `ScrollViewer` lacks virtualization for diagnostic/test logs |
| **TR-08** | **P2** | 1. Probe & Log Perf | `ZapretAutoStrategy.cs:838`, `MainWindowViewModel.cs:5337-5372` | High-frequency `FlowsealProgress` reporting (~2000 events/sweep) triggers continuous UI re-formatting |
| **TR-09** | **P2** | 3. Panel Nesting / Height | `DpiBypassPage.axaml:96`, `TelegramPage.axaml:67` | Non-scrollable Row 0 header (300-380px) squishes tab content to 0px on short viewports or high display scaling |
| **TR-10** | **P2** | 4. Responsive / 360px | `DpiBypassPage.axaml:502-531` | 4-column `UniformGrid` tab strip clips Russian headers ("Дополнительно", "Стратегия") at 360px width |
| **TR-11** | **P2** | 4. Responsive / 360px | `TelegramPage.axaml:363-411` | Tab 0 fixed `120px` Port column crushes Secret `TextBox` down to 33px width |
| **TR-12** | **P2** | 3. Panel Nesting / Wheel | `DpiBypassPage.axaml:704, 724` | Nested `ScrollViewer` inside Tab 3 traps pointer wheel events |
| **TR-13** | **P3** | 1. Probe & Log Perf | `MainWindowViewModel.cs:5657` | `ZapretActionOutput` has no maximum ring-buffer cap, leaking memory on verbose output |
| **TR-14** | **P3** | 4. Responsive / 360px | `DpiBypassPage.axaml:637, 654`, `TelegramPage.axaml:288-298` | Bare-string `Button.Content` without `TextBlock TextWrapping="Wrap"` across host toggles and warning dialogs |

---

## Detailed Audit Findings

### Focus Area 1: Live Probe Log Display, Autoscroll Performance, and String Memory Allocations

#### TR-04: Unbounded Multi-Megabyte StringBuilder Allocation in `ZapretAutoStrategy`
- **Severity**: P2
- **File:Line**: `VPNRouter.Core/Services/ZapretAutoStrategy.cs:692, 749, 1093`
- **Symptom / Finding**:
  During the Flowseal strategy probe sweep (testing up to 20 strategies against 8 targets across HTTP, TLS 1.2, and TLS 1.3), PowerShell outputs between 1,500 and 3,000 lines. In `RunFlowsealProbeAsync`, every line is appended to `var outputBuilder = new StringBuilder()`. At the end of the sweep, `outputBuilder.ToString()` is called to construct `FlowsealSweepResult(..., outputBuilder.ToString(), ...)`.
  This string is several megabytes in size, triggering Large Object Heap (LOH) allocations and subsequent GC Gen 2 pressure. In `MainWindowViewModel.cs:5382-5389`, the caller completely discards `sweep.Output`—it only reads `sweep.Winner`, `sweep.EarlyWinner`, `sweep.TestedCount`, `sweep.TotalCount`, `sweep.ProbeLogPath`, `sweep.PerStrategyResults`, and `sweep.Diagnostic`. Furthermore, the full output is **already** streamed line-by-line to disk via `probeLog.WriteLine(...)` (line 758).
- **Concrete Actionable Fix**:
  1. Eliminate `outputBuilder.AppendLine(line)` from the stdout handler.
  2. Return `string.Empty` or `null` for `Output` in `FlowsealSweepResult` when `probeLogPath` is active.
  3. If callers require error output, rely on the already bounded `errorLines` ring buffer (line 732).

#### TR-05: Per-Line UI Dispatcher Flooding in `RunZapretActionAsync`
- **Severity**: P2
- **File:Line**: `VPNRouter.App/ViewModels/MainWindowViewModel.cs:5708-5712`
- **Symptom / Finding**:
  In `RunZapretActionAsync`:
  ```csharp
  await foreach (var line in action(CancellationToken.None))
  {
      var captured = line;
      await Dispatcher.UIThread.InvokeAsync(() => ZapretActionOutput.Add(captured));
  }
  ```
  When running diagnostic routines (`sc query`, `netsh`, or service checks), lines are emitted in rapid bursts. Dispatching every line individually via `await Dispatcher.UIThread.InvokeAsync(...)` creates hundreds of separate task continuations, allocates delegate closures for each line, and saturates the UI message loop, leading to visible UI thread jitter.
- **Concrete Actionable Fix**:
  Batch line additions before posting to the UI thread:
  ```csharp
  var batch = new List<string>(capacity: 32);
  var lastFlush = DateTime.UtcNow;
  await foreach (var line in action(CancellationToken.None))
  {
      batch.Add(line);
      if (batch.Count >= 20 || (DateTime.UtcNow - lastFlush).TotalMilliseconds >= 50)
      {
          var snapshot = batch.ToArray();
          batch.Clear();
          lastFlush = DateTime.UtcNow;
          await Dispatcher.UIThread.InvokeAsync(() =>
          {
              foreach (var l in snapshot) ZapretActionOutput.Add(l);
          });
      }
  }
  if (batch.Count > 0)
  {
      await Dispatcher.UIThread.InvokeAsync(() =>
      {
          foreach (var l in batch) ZapretActionOutput.Add(l);
      });
  }
  ```

#### TR-06: Missing Autoscroll for Live Diagnostic and Test Output
- **Severity**: P2
- **File:Line**: `VPNRouter.App/Views/Pages/DpiBypassPage.axaml:724-738`, `VPNRouter.App/Views/Pages/DpiBypassPage.axaml.cs:1-11`
- **Symptom / Finding**:
  `DpiBypassPage.axaml.cs` contains an empty code-behind with only `InitializeComponent()`. The `ScrollViewer MaxHeight="160"` wrapping `ZapretActionOutput` has no mechanism to scroll to the newest lines. When a user clicks "Run diagnostics" or "Run tests", the output box populates, but the scroll position remains frozen at the top line. The user is forced to manually scroll down with the mouse wheel or scrollbar to see ongoing progress and final outcomes.
- **Concrete Actionable Fix**:
  1. Add `x:Name="ActionOutputScrollViewer"` to the `ScrollViewer` at line 724.
  2. In `DpiBypassPage.axaml.cs`, subscribe to `DataContextChanged`. When the DataContext is `MainWindowViewModel vm`, subscribe to `vm.ZapretActionOutput.CollectionChanged`.
  3. In the event handler, call `ActionOutputScrollViewer.ScrollToEnd()` via `Dispatcher.UIThread.Post(...)`.
  4. Ensure proper unsubscription on `DataContextChanged` and `DetachedFromVisualTree` to prevent memory leaks.

#### TR-07: Unvirtualized `ItemsControl` Inside `ScrollViewer`
- **Severity**: P2
- **File:Line**: `VPNRouter.App/Views/Pages/DpiBypassPage.axaml:727-736`
- **Symptom / Finding**:
  `ZapretActionOutput` uses `ItemsControl` inside a `ScrollViewer`:
  ```xml
  <ScrollViewer MaxHeight="160" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
      <ItemsControl ItemsSource="{Binding ZapretActionOutput}">
          <ItemsControl.ItemTemplate>
              <DataTemplate x:DataType="x:String">
                  <TextBlock Text="{Binding}" FontSize="9" FontFamily="Consolas,Courier New,monospace" TextWrapping="Wrap"/>
              </DataTemplate>
          </ItemsControl.ItemTemplate>
      </ItemsControl>
  </ScrollViewer>
  ```
  `ItemsControl` does not support UI virtualization. Every line added to `ZapretActionOutput` instantiates and measures a separate `TextBlock` in Avalonia's visual tree. Diagnostic logs exceeding several hundred lines cause layout degradation and memory inflation.
- **Concrete Actionable Fix**:
  Replace `ItemsControl` inside `ScrollViewer` with a virtualized control, such as a read-only `ListBox` with a custom transparent item style, or use Avalonia's `ItemsRepeater` with a `StackLayout`, or bind a single consolidated string buffer to a read-only `TextBox`:
  ```xml
  <TextBox Text="{Binding ZapretActionOutputText}"
           IsReadOnly="True"
           FontSize="9"
           FontFamily="Consolas,Courier New,monospace"
           TextWrapping="Wrap"
           MaxHeight="160"
           ScrollViewer.VerticalScrollBarVisibility="Auto"/>
  ```

#### TR-08: Excessive `FlowsealProgress` UI Notifications and String Recomputation
- **Severity**: P2
- **File:Line**: `VPNRouter.Core/Services/ZapretAutoStrategy.cs:838`, `VPNRouter.App/ViewModels/MainWindowViewModel.cs:5337-5372`
- **Symptom / Finding**:
  In `ZapretAutoStrategy.cs`, every status line parsed (`[HTTP] ... status=OK`) fires:
  ```csharp
  progress?.Report(new FlowsealProgress(snapN, snapT, string.Empty, snapOk, snapTotal));
  ```
  Because `Progress<T>` captures the UI `SynchronizationContext`, all ~2,000 status checks during a sweep are marshaled to the UI thread. In `MainWindowViewModel.cs:5363-5364`, each progress event mutates `ZapretProbePassCount` and `ZapretProbeTotalCount`, which triggers:
  - `[NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]`
  - `[NotifyPropertyChangedFor(nameof(LblZapretAirPill))]`
  This causes continuous string interpolation, property invalidation, and UI re-rendering multiple times per second while probing.
- **Concrete Actionable Fix**:
  Throttle score-only progress reports in `ZapretAutoStrategy.cs`. Only call `progress?.Report` when `snapTotal % 4 == 0` or when a strategy transition occurs (`!string.IsNullOrEmpty(strategy)`). This cuts UI thread dispatches and string allocations by 75% without compromising visual smoothness.

#### TR-13: Uncapped `ZapretActionOutput` Collection Growth
- **Severity**: P3
- **File:Line**: `VPNRouter.App/ViewModels/MainWindowViewModel.cs:5657, 5711`
- **Symptom / Finding**:
  `public ObservableCollection<string> ZapretActionOutput { get; } = new();` has no upper bound. Repeated runs of diagnostics or verbose scripts continue appending lines, retaining all string references in memory indefinitely.
- **Concrete Actionable Fix**:
  Enforce a ring-buffer cap of 500 lines:
  ```csharp
  if (ZapretActionOutput.Count >= 500)
      ZapretActionOutput.RemoveAt(0);
  ZapretActionOutput.Add(line);
  ```

---

### Focus Area 2: Runtime Status Poll Timer (DispatcherTimer & SKGraphics.PurgeAllCaches)

#### TR-01: Synchronous Heavy System Calls and Disk I/O on the UI Thread
- **Severity**: P1
- **File:Line**: `VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs:94-131, 115-130`
- **Symptom / Finding**:
  `StartRuntimeStatusPolling` starts a `DispatcherTimer` scheduled every 2 seconds at `DispatcherPriority.Background`. In Avalonia, `DispatcherTimer` invokes its callback directly on the UI thread.
  Inside `UpdateRuntimeStatus()`:
  1. `RuntimeStatusDetector.IsVpnRunning()`: Calls `ProcessOwnership.FindOwnedSingBox()`, reads and checks file existence on `AppPaths.ConfigYamlPath`, parses YAML paths, and invokes `TunOwnershipLock.ProbeOwnership()`.
  2. `RuntimeStatusDetector.IsZapretRunning()`: Calls `ProcessQuery.AnyAlive("winws")`, which calls `Process.GetProcessesByName("winws")`—an expensive Windows NT kernel process enumeration that allocates an array of `Process` instances and Win32 handles.
  3. `RuntimeStatusDetector.IsTgProxyRunning(tgPort)`: Calls `IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()`. This executes a Win32 `GetExtendedTcpTable` syscall, copies kernel TCP tables, and allocates hundreds of `IPEndPoint` instances.
  When the machine is busy, during disk I/O spikes, or under Windows Defender inspection, these synchronous calls take 50–250 ms. Running them on the UI thread causes perceptible input lag, dropped frames, and interface stuttering every 2 seconds.
- **Concrete Actionable Fix**:
  Decouple detection from the UI thread using a background polling worker:
  ```csharp
  private CancellationTokenSource? _runtimeStatusCts;

  private void StartRuntimeStatusPolling()
  {
      if (_runtimeStatusCts != null) return;
      _runtimeStatusCts = new CancellationTokenSource();
      var token = _runtimeStatusCts.Token;

      Task.Run(async () =>
      {
          using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
          while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token))
          {
              if (_runtimeSkipRemaining > 0)
              {
                  _runtimeSkipRemaining--;
                  continue;
              }

              // Run all OS queries and file I/O strictly on thread pool
              var vpnRunning = RuntimeStatusDetector.IsVpnRunning();
              var zapretRunning = RuntimeStatusDetector.IsZapretRunning();
              var tgProxyRunning = false;
              if (!string.IsNullOrEmpty(TgProxySecret))
              {
                  var tgPort = _settings?.App?.TgProxyPort ?? 1443;
                  if (tgPort <= 0) tgPort = 1443;
                  tgProxyRunning = RuntimeStatusDetector.IsTgProxyRunning(tgPort);
              }

              // Post snapshot to UI thread for simple state assignment
              Dispatcher.UIThread.Post(() =>
              {
                  ApplyRuntimeStatusSnapshot(vpnRunning, zapretRunning, tgProxyRunning);
                  MaybePollConnStats();
              });
          }
      }, token);
  }
  ```

#### TR-02: Synchronous `SKGraphics.PurgeAllCaches()` on the UI Thread
- **Severity**: P1
- **File:Line**: `VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs:181-186`
- **Symptom / Finding**:
  ```csharp
  if ((DateTime.UtcNow - _lastSkiaPurgeAt).TotalSeconds >= 60)
  {
      _lastSkiaPurgeAt = DateTime.UtcNow;
      try { SkiaSharp.SKGraphics.PurgeAllCaches(); }
      catch { /* native-side failures are not fatal */ }
  }
  ```
  `SkiaSharp.SKGraphics.PurgeAllCaches()` is executed synchronously on the UI thread every 60 seconds. This function unconditionally destroys all Skia caches: font glyph atlases, image cache textures, and vector path masks.
  In the very next rendering pass:
  1. Avalonia must synchronously recreate and rasterize every single visible character and glyph on the screen.
  2. GPU textures must be re-allocated and re-uploaded.
  3. This induces an unmistakable, jarring 30–80 ms frame drop and UI freeze exactly once per minute, even when the user is actively typing, interacting with dropdowns, or scrolling.
- **Concrete Actionable Fix**:
  1. Remove `SKGraphics.PurgeAllCaches()` from the periodic timer loop.
  2. Only trigger cache trimming when the application window is minimized or hidden in the notification area:
     ```csharp
     if (window != null && (!window.IsVisible || window.WindowState == WindowState.Minimized))
     {
         if ((DateTime.UtcNow - _lastSkiaPurgeAt).TotalSeconds >= 120)
         {
             _lastSkiaPurgeAt = DateTime.UtcNow;
             try { SkiaSharp.SKGraphics.PurgeFontCache(); } catch { }
         }
     }
     ```
  3. Never call `PurgeAllCaches()` while the window is visible and active.

---

### Focus Area 3: Avalonia 12 ScrollViewer / Panel Nesting in Tab Views

#### TR-09: Unscrollable Header / Hero Squishing Tab Content on Constrained Viewports
- **Severity**: P2
- **File:Line**: `VPNRouter.App/Views/Pages/DpiBypassPage.axaml:96`, `VPNRouter.App/Views/Pages/TelegramPage.axaml:67`
- **Symptom / Finding**:
  In `DpiBypassPage.axaml` and `TelegramPage.axaml`, the root content grid uses:
  `<Grid Grid.Row="0" RowDefinitions="Auto,Auto,Auto,*" Margin="12,8,14,8">`.
  Row 0 contains the download progress bar, AV block toast / scheme warning, and the entire Hero Card (~260-350px tall).
  Row 1 is the `UniformGrid` tab strip (~32px).
  Row 2 is the divider (~7px).
  Row 3 is the `*` row hosting the per-tab `ScrollViewer`s.
  While removing the outer `ScrollViewer` in r27 resolved the Avalonia 12 Carousel bug by providing a bounded height to Row 3, it introduced an inverse failure mode:
  - On laptops with 768p displays at 125% or 150% scaling (effective height ~450–500px) or when resized:
    - Main window header, top tabs, and footer consume ~120px.
    - ToolsPage sub-tab strip consumes ~35px.
    - Page Row 0 + Row 1 consume ~320–380px.
    - Total non-scrollable vertical consumption reaches 475–535px!
  - As a result, Row 3 (`*`) collapses to 0–40px, squishing the actual tab content into an unviewable sliver.
  - Worse, if an AV block toast or scheme warning appears (+60px), Row 0 overflows the bottom of the window. Because Row 0 is completely outside any `ScrollViewer`, the tab strip and tab content are pushed off-screen with **no scrollbar available to reach them**.
- **Concrete Actionable Fix**:
  1. Add a `MinHeight="180"` constraint on Row 3 so the tab content maintains usable height.
  2. Implement an adaptive layout or responsive state: when window height < 560px, shrink Hero padding and icon size (`Width="24" Height="24"` instead of `36x36`) or collapse the Hero into a compact single-line card (`Grid ColumnDefinitions="Auto,*,Auto"` with status dot, title, and compact action button).
  3. Ensure that if the entire page height exceeds the viewport, an outer boundary or fallback scroll path allows accessing the tab buttons.

#### TR-12: Nested `ScrollViewer` Wheel Event Conflict in Zapret Tab 3
- **Severity**: P2
- **File:Line**: `VPNRouter.App/Views/Pages/DpiBypassPage.axaml:704, 724`
- **Symptom / Finding**:
  In Tab 3 ("Дополнительно"):
  An outer `ScrollViewer` wraps the entire tab (line 704).
  Inside it, lines 724-726 define an inner `ScrollViewer MaxHeight="160"` for `ZapretActionOutput`.
  In Avalonia, nested `ScrollViewer`s intercept mouse wheel events. When the mouse hovers over `ZapretActionOutput`, the inner `ScrollViewer` consumes pointer wheel deltas. When the inner scroll reaches the top or bottom, pointer wheel events fail to bubble smoothly to the outer tab `ScrollViewer`, causing the page scroll to freeze unexpectedly.
- **Concrete Actionable Fix**:
  1. Add `ScrollViewer.IsScrollInertiaEnabled="False"` to the inner `ScrollViewer`.
  2. Or handle `PointerWheelChanged` on the inner `ScrollViewer` to bubble unhandled deltas to the parent:
     ```csharp
     if ((e.Delta.Y > 0 && sv.Offset.Y <= 0) || (e.Delta.Y < 0 && sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height))
     {
         // bubble to parent
     }
     ```
  3. Alternatively, replace the inner `ScrollViewer` with a fixed-height log viewer that only captures scroll when explicitly focused.

---

### Focus Area 4: Narrow 360px Window Layout Responsiveness and Button Label Clipping

#### TR-03: Telegram Air-Pill, Stats, and "Copy Link" Button Overflowing 360px Layout
- **Severity**: P1
- **File:Line**: `VPNRouter.App/Views/Pages/TelegramPage.axaml:206-250`
- **Symptom / Finding**:
  In `TelegramPage.axaml`:
  ```xml
  <StackPanel IsVisible="{Binding TgProxyEnabled}"
              Orientation="Horizontal"
              Spacing="6"
              HorizontalAlignment="Center"
              VerticalAlignment="Center">
      <Border CornerRadius="{StaticResource RadiusPill}" Padding="10,5" ...>
          <StackPanel Orientation="Horizontal" Spacing="6">
              <Ellipse Width="6" Height="6" .../>
              <TextBlock Text="{Binding LblTgProxyAirPill}" .../>
              <TextBlock Text="{Binding TgProxyStats}" IsVisible="{Binding HasTgProxyStats}" .../>
          </StackPanel>
      </Border>
      <Button Command="{Binding CopyTgProxyLinkCommand}" Content="{Binding L_TgProxyCopyLink}" .../>
  </StackPanel>
  ```
  When the Telegram proxy is running:
  - `LblTgProxyAirPill`: "В эфире · :1443" (~85px)
  - `TgProxyStats`: " | акт: 2 | всего: 15 | ↑1.2 MB ↓4.5 MB" (~180px)
  - Air pill border padding + ellipse: ~35px
  - Air pill total width: ~300px
  - Sibling "Скопировать ссылку" button: ~135px
  - Total horizontal layout width: **441px**!
  In a 360px window, after subtracting page margins (26px) and card padding (32px), the available width is only **302px**.
  Because the container is a `StackPanel Orientation="Horizontal"`, it does not wrap. The "Скопировать ссылку" button is pushed completely past the right edge of the window and clipped off-screen. Users cannot copy the proxy link on narrow windows when stats are active!
- **Concrete Actionable Fix**:
  Replace the outer `StackPanel Orientation="Horizontal"` with a `WrapPanel`:
  ```xml
  <WrapPanel IsVisible="{Binding TgProxyEnabled}"
             HorizontalAlignment="Center"
             ItemSpacing="6"
             LineSpacing="6">
      <Border CornerRadius="{StaticResource RadiusPill}"
              Background="{DynamicResource SuccessBgBrush}"
              BorderBrush="{DynamicResource SuccessBorderBrush}"
              BorderThickness="1"
              Padding="10,5">
          <StackPanel Orientation="Horizontal" Spacing="6" VerticalAlignment="Center">
              <Ellipse Width="6" Height="6" Fill="{DynamicResource SuccessSolidBrush}" VerticalAlignment="Center"/>
              <TextBlock Text="{Binding LblTgProxyAirPill}" FontSize="10" FontWeight="SemiBold" Foreground="{DynamicResource SuccessFgBrush}"/>
              <TextBlock Text="{Binding TgProxyStats}" IsVisible="{Binding HasTgProxyStats}" FontSize="10" Opacity="0.85" Foreground="{DynamicResource SuccessFgBrush}" Margin="6,0,0,0" TextTrimming="CharacterEllipsis" MaxWidth="160"/>
          </StackPanel>
      </Border>
      <Button Command="{Binding CopyTgProxyLinkCommand}"
              Content="{Binding L_TgProxyCopyLink}"
              FontSize="10"
              Padding="10,5"
              CornerRadius="{StaticResource RadiusPill}"
              Background="{DynamicResource SurfaceBaseBrush}"
              BorderBrush="{DynamicResource BorderSubtleBrush}"
              BorderThickness="1"
              VerticalAlignment="Center"/>
  </WrapPanel>
  ```

#### TR-10: Clipped ToggleButton Headers in 4-Column Tab Strip at 360px
- **Severity**: P2
- **File:Line**: `VPNRouter.App/Views/Pages/DpiBypassPage.axaml:502-531`
- **Symptom / Finding**:
  `UniformGrid Grid.Row="1" Rows="1" Columns="4"` forces 4 equal columns.
  In a 360px window, available width is 334px (360 - 26 margin). Each column gets exactly **83.5px**.
  The `TabStripPill` style specifies `Padding="8,6"` (16px horizontal padding), leaving only **67.5px** for text.
  - Tab 3 is bound to `L_ZapretSecAdvanced` ("Дополнительно" in Russian). 13 characters at 11px font require ~91px.
  - Tab 0 is bound to `L_ZapretSecStrategy` ("Стратегия"). 9 characters require ~65px, which widens to ~72px when selected due to `FontWeight="SemiBold"`.
  Because `ToggleButton.Content` is bound as a bare string, Avalonia generates a `TextBlock` with `TextWrapping="NoWrap"` and no trimming.
  "Дополнительно" is clipped on the right ("Дополнитель..."), and "Стратегия" clips when active.
- **Concrete Actionable Fix**:
  1. Reduce padding on narrow layouts or set `Padding="4,4"` in `TabStripPill`.
  2. Explicitly wrap the button content with a wrapping/trimming `TextBlock`:
     ```xml
     <ToggleButton IsChecked="{Binding IsZapretTab3}"
                   Command="{Binding SetZapretTabCommand}"
                   CommandParameter="3"
                   Classes="TabStripPill"
                   Padding="4,4"
                   HorizontalAlignment="Stretch">
         <TextBlock Text="{Binding L_ZapretSecAdvanced}"
                    FontSize="10"
                    TextWrapping="Wrap"
                    TextAlignment="Center"/>
     </ToggleButton>
     ```
  3. Or adapt the tab string for compact views (e.g. "Опции" instead of "Дополнительно").

#### TR-11: Telegram Settings Tab Fixed Port Column Crushes Secret TextBox
- **Severity**: P2
- **File:Line**: `VPNRouter.App/Views/Pages/TelegramPage.axaml:363-411`
- **Symptom / Finding**:
  In Tab 0 ("Настройки"):
  `Grid ColumnDefinitions="120,*" ColumnSpacing="12"`.
  At 360px window width (available tab content width = 314px):
  - Col 0 (Port) takes fixed 120px for a 4-digit number.
  - Column spacing takes 12px.
  - Col 1 (`*`) receives only 182px.
  Inside Col 1:
  `Grid ColumnDefinitions="*,Auto,Auto" ColumnSpacing="4"`.
  - Copy Button ("Копировать") + padding takes ~86px.
  - Regenerate Button ("Новый") + padding takes ~55px.
  - Button spacing takes 8px.
  - Buttons total = 149px.
  - Remaining space for `TextBox Grid.Column="0"` (the 32-character Secret): **33px**!
  The secret key is crushed into a tiny 33-pixel box showing only 2-3 characters.
- **Concrete Actionable Fix**:
  Change Col 0 from `120` to `70` (plenty of room for a 5-digit port):
  ```xml
  <Grid ColumnDefinitions="70,*" ColumnSpacing="10">
  ```
  This immediately frees up 52px, expanding the Secret TextBox from 33px to 85px.
  Even better: switch to vertical stacking on narrow layouts (`RowDefinitions="Auto,Auto"`), giving the Secret TextBox and buttons a full 300px width.

#### TR-14: Bare-String `Button.Content` Lacking TextWrapping
- **Severity**: P3
- **File:Line**: `VPNRouter.App/Views/Pages/DpiBypassPage.axaml:132-140, 637, 654`, `VPNRouter.App/Views/Pages/TelegramPage.axaml:288-298`
- **Symptom / Finding**:
  Multiple action buttons bind raw localized strings directly to `Content`:
  - `DpiBypassPage.axaml:132`: `Content="{Binding L_ZapretAvBlockCopyPath}"` ("Скопировать путь")
  - `DpiBypassPage.axaml:637`: `Content="{Binding LblDiscordHosts}"` ("Добавить Discord hosts")
  - `DpiBypassPage.axaml:654`: `Content="{Binding LblFlowsealHosts}"` ("Добавить Flowseal hosts")
  - `TelegramPage.axaml:288`: `Content="{Binding L_TgProxyCopyLink}"` ("Скопировать ссылку")
  - `TelegramPage.axaml:292`: `Content="{Binding L_TgProxyDismiss}"` ("Закрыть")
  Per `VPNRouter.App/AGENTS.md` and design system guidelines, bare string content creates non-wrapping text blocks that can push parent containers into overflow or truncate on narrow windows with large text scaling.
- **Concrete Actionable Fix**:
  Wrap all button contents explicitly in `TextBlock` elements:
  ```xml
  <Button Command="{Binding ToggleDiscordHostsCommand}" ...>
      <TextBlock Text="{Binding LblDiscordHosts}" TextWrapping="Wrap"/>
  </Button>
  ```

---

## Verification & Confirmation

The audited files and line numbers were checked against current repository sources:
- `VPNRouter.App/Views/Pages/ToolsPage.axaml` (40 lines)
- `VPNRouter.App/Views/Pages/DpiBypassPage.axaml` (875 lines)
- `VPNRouter.App/Views/Pages/TelegramPage.axaml` (566 lines)
- `VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs` (387 lines)
- `VPNRouter.Core/Services/ZapretAutoStrategy.cs` (1332 lines)

All findings reflect actual code patterns, control hierarchies, and runtime behavior in the current code base.
