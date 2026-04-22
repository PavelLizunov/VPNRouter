# UI testing workflow

How we drive and regress-check the Avalonia GUI from the headless shell —
no real display required, runs under `dotnet test`. The whole point is to
catch XAML/binding/layout regressions and surface visual bugs without
shipping a build to a user for manual inspection.

---

## TL;DR for "I just want to run it"

```bash
cd C:\Project\VPNRouter
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj --nologo
```

Screenshots land in `VPNRouter.Tests/screenshots/*.png`. Open the folder,
eyeball the PNGs. If any test fails, the failure message points at the
exact page/control/binding.

---

## What's in place

Three test classes under `VPNRouter.Tests/`:

| File | Covers | Count |
|---|---|---|
| `HeadlessGuiTests.cs` | Top-level window smoke + input routing demo | 4 |
| `ViewModelTests.cs` | VM-level regression tests (e.g. Bug B `SmpAutostartChecked`) | 1 |
| `PageScreenshotTests.cs` | Per-page render + PNG capture | 9 |

Plus supporting infra:

- `TestAppBuilder.cs` — registers headless Avalonia (Skia backend enabled
  via `.UseSkia()` + `UseHeadlessDrawing=false` so screenshots work).
- `ScreenshotHelper.cs` — `.Capture(window, name)` and `.CapturePage(control, name)`
  helpers; writes PNGs into `VPNRouter.Tests/screenshots/`.
- `MainWindowViewModelFixture` — shared real `MainWindowViewModel` across
  page tests so bindings resolve to real data.

---

## What you can test with this setup

**Yes:**
- Window/UserControl construction (view tree assembles, no throwing
  templates, no null resource lookups).
- Layout measures (`Bounds.Width/Height > 0` after `Show()`).
- Real data binding — `DataContext = new MainWindowViewModel()` routes
  production bindings, so a renamed VM property breaks immediately.
- Input events — `button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))`,
  `HeadlessWindowExtensions.KeyPress(window, Key.Enter, ...)`,
  `HeadlessWindowExtensions.MouseDown(window, point, MouseButton.Left)`.
- Find controls by `x:Name`: `window.FindControl<Button>("ApplyBtn")`.
- Observe `PropertyChanged` streams for computed-property regressions.
- Capture PNG of rendered frame → inspect visual bugs (misaligned controls,
  theme breakage, overflowed labels, empty bindings).

**No:**
- OS dialogs (UAC prompts, file pickers) — they open on the real desktop
  and the harness has no real desktop.
- Real service install (`sc.exe` calls) — these would hit services.msc and
  leave residue; mock `WindowsServiceHelper` if you need to exercise the
  install flow.
- Real VPN lifecycle — needs admin + sing-box binary + actual TUN adapter.
  Runtime behaviour is tested separately from the CLI surface (see
  batch 2 runtime testing in `plans/vpnrouter-v2.27-service-ux.md`).

---

## Post-release bug-hunt checklist

Run every time after cutting a release (`vX.Y.Z-rN`). Roughly 2 minutes
from start to finish.

1. **Pull + build**
   ```bash
   git pull origin main
   dotnet build VPNRouter.sln --nologo
   ```
   Expected: 0 errors. Warning count should be the same as before (32
   pre-existing on 2026-04-22). Any new warning is worth investigating.

2. **Run the full test suite**
   ```bash
   dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj --nologo
   ```
   Expected: all tests pass (plus any pre-existing skips). If red, the
   failure message names the class + method — fix before shipping.

3. **Eyeball the page screenshots**
   ```bash
   ls -la VPNRouter.Tests/screenshots/
   # open them in an image viewer — each page's PNG matches production
   # first-run visuals.
   ```
   What to look for:
   - Empty regions where a list / banner / card should be populated
     (often a broken binding — `{Binding SomeRenamed}` no longer resolves).
   - Misaligned controls after a theme change (`page-servers.png` is a
     good canary for the master-detail layout).
   - Text overflow in localised strings (Russian tends to be longer than
     English; switch locale via `_settings.App.Language` if investigating).
   - Theme artefacts after `Radius*` / semantic-token changes (Arctic
     theme lives in `Styles/Tokens.axaml`).

4. **Run the doctor + dry-run on the VM shell** (not strictly UI, but
   in the same spirit of "does the new binary work"):
   ```bash
   VPNRouter.CLI/bin/Debug/net8.0-windows/VPNRouter.CLI.exe doctor
   VPNRouter.CLI/bin/Debug/net8.0-windows/VPNRouter.CLI.exe start --profile Discord_Privacy --dry-run
   ```
   Expected:
   - `doctor` → `All checks passed` (assuming sing-box + config in place).
   - `start --dry-run` → resolves subscription, writes `current.json`,
     `sing-box check` exits 0.

5. **Real start/stop cycle** (optional, only if touching runtime code):
   ```bash
   VPNRouter.CLI.exe start --profile Discord_Privacy  # in terminal A
   VPNRouter.CLI.exe status                            # in terminal B
   VPNRouter.CLI.exe stop                              # in terminal B
   ```
   After stop: no `sing-box.exe` in `tasklist`, no `sing-tun` firewall
   rule (use `netsh advfirewall firewall show rule name=all`), DNS
   hardening keys rolled back (or `dns-hardening-state.json` deleted
   so next start self-heals).

---

## Extending the coverage

### Adding a page test

1. Add a `[AvaloniaFact]` method in `PageScreenshotTests.cs`:
   ```csharp
   [AvaloniaFact] public void MyNewPage() => Capture(new MyNewPage(), "page-my-new");
   ```
2. Run the suite. PNG appears in `screenshots/page-my-new.png`.

### Adding a VM regression test

1. Add a method in `ViewModelTests.cs` following the Bug B pattern:
   instantiate the VM, flip inputs, assert on property + `PropertyChanged`
   notifications.
2. Use `[AvaloniaFact]` so the test runs on the Avalonia dispatcher (needed
   whenever the VM touches `Dispatcher.UIThread`).

### Adding an interaction test

Pattern: host the view in a window, dispatch input, assert on VM/UI state.
`HeadlessWindowExtensions` gives you `MouseDown/Up/Move`, `KeyPress`, etc.
If a test needs a real bitmap, call `window.CaptureRenderedFrame()` after
the interaction and compare pixel data.

### Pinning a baseline screenshot

Current setup drops fresh PNGs every run — nothing is pinned. If we want
true visual-regression testing, commit a baseline PNG alongside the test
and compare in `ScreenshotHelper`:
```csharp
var actual = window.CaptureRenderedFrame();
var expected = new Bitmap("baseline/page-subscribe.png");
// pixel-diff, fail if delta > tolerance
```
Not done yet — too early to pin baselines while the UI churns.

---

## Gotchas hit and resolved

- **Empty screenshots** when `DataContext` isn't set. XAML `{Binding …}`
  resolves to nothing and you get a mostly-blank PNG. Always set the
  DataContext (prefer the real VM via `MainWindowViewModelFixture`).
- **`Button.PerformClick()` doesn't exist** — use `button.RaiseEvent(new
  RoutedEventArgs(Button.ClickEvent))` instead. Same route production
  takes.
- **`App` namespace vs `App` class collision** — in `TestAppBuilder.cs`
  we alias `VPNRouterApp = VPNRouter.App.App` to disambiguate when
  calling `AppBuilder.Configure<VPNRouterApp>()`.
- **Screenshots folder location** — resolved relative to the test assembly
  path so `dotnet test` from any CWD puts them in the same place.
- **Layout needs `window.Show()`** before `CaptureRenderedFrame()` — without
  it templates haven't applied and you get an empty bitmap.

---

## Related work

- `plans/vpnrouter-v2.27-service-ux.md` — the v2.27 roadmap these tests
  regress-guard (Bug B already pinned by `SmpAutostartChecked_ReactsToAllThreeInputs`).
- `plans/vpnrouter-release-strategy.md` — where this checklist slots into
  the rolling -rN release flow (run post-release = after every `gh release`
  edit that marks prerelease).
