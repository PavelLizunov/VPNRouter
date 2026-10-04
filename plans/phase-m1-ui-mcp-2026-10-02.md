# M-1: UI exploration MCP server (render every page in every state, click everything, find crashes)

## Why

The owner asked (2026-10-02, after testing r16) for an MCP server that we build ourselves, so that a model can look at every page, click every
element at a chosen speed to find crashes, and check how every page renders in every possible state. Two real defects show why it is needed:

- r14: the desktop app died when the Applications tab was opened (`Visual was invalidated during the render pass`). Six page screenshot tests had been
  failing for days and nobody connected them to a production crash (D-3).
- r16: pages were restyled by a model that never saw some of them; the owner found a legacy Applications page and others that "look unexamined".

A reviewer cannot be asked to open ~25 surfaces x themes x languages x sizes x states by hand. The server makes that a tool call.

## What

A stdio MCP server (JSON-RPC 2.0, newline-delimited, protocol 2024-11-05, no NuGet dependency for the protocol) on top of Avalonia.Headless + Skia,
so it renders the real XAML with the real view models and needs no display.

Two new projects under `VPNRouter.Tools/`:

1. `UiProbe` (class library): scenario catalog, renderer, layout lint, sweeper (the engine). Reused by tests, so CI gets a permanent
   "every page can be clicked through without an exception" test.
2. `UiMcp` (console exe): the MCP protocol and the tool table over `UiProbe`. Published self-contained for linux-x64 by CI (artifact), so the
   workstation runs a downloaded binary and never needs an SDK.

### Tools (phase M-1)

| Tool | What it does |
|---|---|
| `ui_catalog` | Pages, windows, scenarios, themes, languages, sizes the server knows. |
| `ui_render` | One image (PNG as MCP image content) of a surface for scenario + theme + language + size, plus a lint report. |
| `ui_matrix` | Many combinations in one call: a labelled contact sheet plus a table of lint findings per cell. |
| `ui_tree` | The visual/automation tree of a surface: names, types, bounds, enabled, text; what a sweep would click. |
| `ui_sweep` | Click every interactive control of a surface in order (or `random` with a seed), `speed_ms` between actions (0 = back to back), a rendered frame after each action; reports the first exception with the element path and the action trail, optional image of the failing state. |
| `ui_set_state` / `state` argument | Any public VM property can be set by name (`{"IsConnected":true,"StatusText":"..."}`) before rendering, so a model can reach states no preset knows. |

Lint findings (mechanical, no model needed): clipped or overflowing text, zero-size or off-screen controls, siblings that overlap, contrast under 4.5:1 for
text, symbol/emoji glyphs in text where a Pictogram is expected, unlocalized keys, controls smaller than 32 px, content wider than the viewport.

### Scenarios

A scenario is a named VM configuration built on the same fakes the tests use (`InMemorySettingsStore`, fake sing-box API, fake driver): connected,
connecting, disconnected, error, no config, 200 servers, long names, empty lists, update available, driver not loaded, Russian/English, simple/advanced
mode. Presets live in code; anything else goes through `state`.

### Phases

- **M-1 (this brief):** `UiProbe` + `UiMcp` with the tools above for the desktop app, a CI job that publishes the Linux binary, a permanent sweep test.
- **M-2:** live mode for the real Windows app on the worker (UI Automation through the existing interactive helper): `live_tree`, `live_click`, `live_sweep`
  with speed, crash detection from process exit and the .NET Runtime event log. This is what reproduces window-system problems headless cannot.
- **M-3:** Android through adb (uiautomator dump, input, screencap) with the existing test hook for state.

## Not covered (stated up front)

- Headless Linux uses different fonts than Windows, so pixel positions differ; the lint is relative (overflow, overlap), not a pixel diff against Windows.
- Real cursor hover, drag, native file dialogs, tray, DPI changes: M-2.
- Anything that needs a live VPN tunnel.

## Verification

Tests on the Windows worker at the exact SHA (`UiProbe` tests, the new sweep tests), CI green on the PR, then a smoke session: start the published binary
from a client, `ui_catalog`, `ui_render` of three pages, `ui_sweep` of the Applications page.

## Outcome

In progress.
