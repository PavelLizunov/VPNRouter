# VPNRouter — Roadmap v2.20.x "UI / VPN logic / memory batch"

**Baseline**: v2.19.3 prerelease.

**Goal**: Close the v2.19.3 post-ship feedback batch — four UI defects,
two VPN logic bugs, and a real memory pass with documented savings.

**User-reported issues (2026-04-20, post-v2.19.3)**:
1. Mini icon in OS title bar (chrome "header") — looks wrong, user
   wants it gone entirely.
2. Logo in in-window subheader is still blurry.
3. Telegram Stop button: page flips to "stopped" but header chip stays
   green AND the tg-ws-proxy process keeps serving traffic.
4. "Simple" pill-button in Advanced doesn't match the design.
5. macOS: pasting a subscription URL → UI shows connected momentarily
   then disconnected, even though sing-box is running and the real
   IP has changed.
6. UI uses 200-240 MB; multiple dotnet.exe visible in Task Manager;
   needs an actual memory pass, not just handwaving.

---

## Investigation summary

### Issue 1 — OS title bar icon
`Window.Icon` still points at `penguin_logo.ico`. Title="" (set in
v2.19.2) removed the text but the icon stayed in the chrome.
Fix: drop the Icon attribute so the OS shows its default window icon
(or nothing on platforms that don't show one at all).

### Issue 2 — Subheader logo blur
Source: `penguin_logo.png` 640×640, RGB (no alpha). HighQuality
interpolation (v2.19.3) helps but the output is soft rather than
crisp. We have `b_icon.png` (552×712 RGBA) and `w_icon.png` (555×719
RGBA) in Assets/ — pre-rendered black/white variants with alpha
channel. Switching to those per-theme (no RGB inversion) gives sharper
edges and frees the WriteableBitmap hack.

### Issue 3 — Telegram Stop doesn't kill
`TgProxyManager.Stop()` kills the `_process` it directly launched —
fine when the current app instance is the one that started tg-ws-proxy.
But the ACTUAL process is `python.exe -m proxy.tg_ws_proxy …`.
`TgProxyManager.KillAll()` looks for process names `"tg-ws-proxy"` and
`"TgWsProxy_windows"` — neither exists. So if tg-ws-proxy was started
by the Windows Service, by a previous app session, or by anything
other than the current `_process`, nothing kills it. Header chip stays
green because `IsTgProxyRunning(port)` sees the port still bound.

Fix: port-based kill. When Stop is requested, find whoever is
listening on `TgProxyPort` and kill that PID (`netstat -ano |
findstr :PORT` → `taskkill /PID /F` on Windows; `lsof -iTCP:PORT
-sTCP:LISTEN -t | xargs kill` on Unix).

Also make the Telegram page status TextBlock track the polled
`TgProxyRuntimeStatus` (same source as the header chip) so the page
never lies about state — if the kill somehow fails, both UI surfaces
say "still running" instead of only one.

### Issue 4 — Simple pill button
v2.19.1 added an `AccentBgSubtle + AccentBorder + AccentFg` pill,
which makes it visually compete with the VPN/Zapret/TG status chips.
Per design vocabulary, mode switches aren't "status", they're
navigation — should read as a subtle ghost control, not a chip.
Fix: strip the background + border, keep only the accent text with
a tasteful `◂ Simple` glyph. Hover state adds the subtle background.

### Issue 5 — macOS subscription connect-then-disconnect
`ToggleConnectionAsync` relies on the engine's `StatusChanged`
event (→ `OnEngineStatus` → sets `IsConnected=true` when status
starts with "Connected"). If the event arrives slower than the next
2-second poll tick, the poll sees `IsConnecting=true` (never cleared
because the event hasn't fired) and bails — which is actually fine.
But the real issue: on macOS the event string may not match
"Connected" exactly, or the engine publishes a different status first
("Starting routing", etc.), so `OnEngineStatus` never promotes
`IsConnected`. Eventually IsConnecting times out (via the 30s token)
and the cancellation path fires, setting `IsConnected=false`. User
sees briefly-connected → disconnected.

Fix (defensive, covers every platform):
- After `_engine.StartAsync` returns successfully (no exception),
  explicitly check `_engine.IsRunning`. If true, set `IsConnected=true`
  + `IsConnecting=false` and derive the "Connected [mode]" status
  line directly. Don't assume the event fired.
- Also: `SyncConnectedWithVpnRuntime` should protect a freshly-
  connected session. Record `_lastSuccessfulConnectAt` on promotion
  to Connected; if a poll fires within 5 s of that and `vpnRunning`
  happens to return false, log + skip the demote (platforms differ
  on how fast `Process.GetProcessesByName` picks up new procs).

### Issue 6 — Memory
Static audit confirmed:
- `_allConfigs` eager-loaded from FreeConfigs cache in VM ctor.
  ~6–7 MB for ~25k entries on users who've run the aggregator.
- `FreeConfigsPageViewModel` subscribes to 2 aggregator events,
  never unsubscribes.
- Unbounded growth of `_displayedConfigs` is clamped (it's 1–3k
  typical) — not a leak, just steady state.
- "dotnet.exe" zombies: myth. App is `--self-contained`, so those
  aren't our processes — they're the .NET runtime loaded into
  VPNRouter.App.exe / CLI.exe / Service.exe, or dev-time test
  processes. No action needed.

Fix list (ranked by effort/yield):
- Implement `IDisposable` on FreeConfigsPageViewModel, unsubscribe
  aggregator handlers. [100-200 KB, 2 min]
- Lazy-load `_allConfigs` only when FreeConfigs tab first activates.
  [6-7 MB, 30 min]
- Don't reload pool cache at startup if we've just loaded it for
  the same session. (Already OK per code.)
- Dispose inverted-logo WriteableBitmap temp buffer after copy.
  Already minimal — skip.

---

## Releases

### v2.20.0 — UI + VPN logic fixes (issues 1, 2, 3, 4, 5)

Files:
- `VPNRouter.App/Views/MainWindow.axaml` — drop Window.Icon; Simple
  pill → ghost variant.
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs` —
  `LogoSource` rebuilt around b_icon/w_icon; add IsConnected
  promotion after successful StartAsync; add
  `_lastSuccessfulConnectAt` guard in SyncConnectedWithVpnRuntime.
- `VPNRouter.Core/Services/TgProxyManager.cs` — new `KillByPort`
  helper; `KillAll` falls through to it.
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs` — wire
  `KillByPort` into the Telegram Stop flow.
- `VPNRouter.App/Views/Pages/TelegramPage.axaml` — page status
  TextBlock follows polled `TgProxyRuntimeStatus`.

Acceptance:
- [ ] OS title bar: no icon (or OS default only), no "Virtual Penguin
  Network" text.
- [ ] Subheader logo: sharp at 36 px, both light + dark themes.
- [ ] Telegram Stop → kills the process (verify with Task Manager
  / `lsof -iTCP:1443`), header chip goes grey within 2 s, page
  status also says "Stopped".
- [ ] Simple pill-button: reads as a subtle "◂ Simple" text control
  with hover background, not a chip.
- [ ] macOS subscription: connect → UI stays connected, doesn't
  flicker back to disconnected.

### v2.20.1 — Memory pass (issue 6)

Files:
- `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs`
  — add IDisposable, unsubscribe aggregator handlers, lazy-load
  cache on first tab activation.
- `VPNRouter.App/Views/MainWindow.axaml` — wire the tab-activation
  signal (bind `IsFreeConfigsTabSelected` → trigger load).

Acceptance:
- [ ] Fresh launch with FreeConfigs tab NOT opened → working set
  < 180 MB (down from ~200-240).
- [ ] FreeConfigs tab opened once → cache loads, working set grows
  as expected.
- [ ] FreeConfigs tab closed → no reference leaks on aggregator
  events.

---

## Status tracker

### Issues
- [x] Issue 1 — OS title bar icon (shipped v2.20.0)
- [x] Issue 2 — subheader logo blur (shipped v2.20.0)
- [x] Issue 3 — Telegram Stop incomplete (shipped v2.20.0 + logging polish v2.20.2)
- [x] Issue 4 — Simple pill design (shipped v2.20.0)
- [x] Issue 5 — macOS subscription bounces (shipped v2.20.0)
- [x] Issue 6 — Memory: FreeConfigs lazy-load (shipped v2.20.1)

### Releases
- [x] v2.20.0 — fixes 1–5
- [x] v2.20.1 — fix 6
- [x] v2.20.2 — bug-sweep polish: KillByPort outer catches now log via
  Serilog instead of silently eating the failure; HostsManager ipconfig
  Process wrapped in `using` to prevent handle-leak on WaitForExit
  timeout; OpenHostsEditHelpers guarded by `OperatingSystem.IsWindows()`
  so the notepad/explorer launch is noop-safe on non-Windows.
