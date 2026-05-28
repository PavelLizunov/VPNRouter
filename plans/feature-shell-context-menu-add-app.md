# Feature — Explorer context-menu "Add to VPNRouter (route through VPN)"

**Status**: DESIGN LOCKED (2026-05-28), implementation NOT started.
**Idea origin**: user — "как у 7-Zip: правый клик по ярлыку/exe → сразу
добавить в split-tunnel список приложений".
**Target release**: candidate 2.38/2.39 headline feature (Windows-only).
**Value**: removes the #1 friction in split-tunnel VPNs — "how do I add my
app to the VPN list". One right-click instead of: open app → Applications
tab → browse → find exe → add.

## Locked design decisions

1. **Approach = legacy registry verb (MVP)**, NOT modern IExplorerCommand.
   - Per-user keys under `HKCU\Software\Classes\` (no admin, clean uninstall).
   - Win10: top-level context menu. Win11: under "Show more options" —
     acceptable for MVP. Modern top-level (MSIX/sparse package) deferred.
2. **Behaviour after add = add to list + toast ONLY**, no auto-reconnect.
   - Append exe basename to `app.routing_apps_include`, save config, toast
     "<Name>.exe добавлен в VPN-маршрутизацию · применится при следующем
     подключении". No routing Apply / reconnect (avoids surprise mid-session
     disconnects — user controls when to re-apply).

## Scope

- Verbs registered ONLY on `exefile` + `lnkfile` classes (NOT `*` — avoid
  polluting every file's menu).
- Folders: OUT of MVP (recursive "add all exes in folder" = separate later
  feature).
- Multi-select: Explorer invokes the verb once per selected item; the
  single-instance channel coalesces them. Fine for MVP.

## How it works

### Registration (install.ps1 + first-run, unregister on uninstall)
```
HKCU\Software\Classes\exefile\shell\VPNRouterRoute
   (default)        = "Добавить в VPNRouter (через VPN)"   ; menu label
   Icon             = "<install>\app\VPNRouter.GUI.exe,0"  ; penguin glyph
HKCU\Software\Classes\exefile\shell\VPNRouterRoute\command
   (default)        = "<install>\app\VPNRouter.GUI.exe" --route-app "%1"
```
Same block for `lnkfile`. Register idempotently; remove both on uninstall.

### Invocation handler (`--route-app "<path>"`)
1. Program.cs parses `--route-app <path>` BEFORE heavy DLL load.
2. **Hand off to the running instance via the EXISTING single-instance
   channel** (`SingleInstance.TryAcquireOrSignal` + `ShowWindowRequested`
   in App.axaml.cs:129 — extend the signal payload to carry the path, or
   add a sibling `RouteAppRequested` event). If no instance running → launch,
   process the arg on startup (optionally headless: add + toast + exit without
   showing the window — decide during impl).
3. Resolve target:
   - `.lnk` → `IShellLink`/`IWshShortcut` COM → target `.exe`.
   - `.exe` → use directly.
   - Take **basename** (e.g. `Discord.exe`).
4. **Preserve filesystem casing** — process_name matching is case-sensitive
   (golden rule #7: `Discord.exe` ≠ `discord.exe`). Read the actual file's
   on-disk casing, do NOT lowercase.
5. Dedup against existing `routing_apps_include` using
   `StringComparer.OrdinalIgnoreCase` but store the original-cased name.
6. Save config; toast. If already present → toast "уже в списке".

## Gotchas

- Quote `%1` (paths with spaces).
- `.lnk` resolution needs STA COM (`IShellLink`) — wrap in a tiny helper.
- AV/SmartScreen: per-user HKCU\Software\Classes writes are low-risk but
  note in release notes.
- Uninstall must delete both verb trees (exefile + lnkfile).
- Test on BOTH Win10 (top-level) and Win11 (Show more options) via MCP.

## Verification gate (when built)

- Build clean; new unit test for the .lnk→exe resolver + casing/dedup helper
  (Core, testable without registry).
- Registry register/unregister idempotency test (or manual).
- MCP: right-click a real .exe + a real .lnk in Explorer → menu entry present
  → click → app added to Applications list + toast shown. Screenshot.
- Uninstall removes the verbs (no orphan menu entries).

## NOT in scope (future)
- Modern Win11 top-level menu (IExplorerCommand + sparse package).
- Folder → add-all-exes.
- macOS Finder Quick Action / Linux per-DE `.desktop` actions (separate
  platform-specific briefs; Windows-first).

## Effort
MVP ~1-2 days. Modern Win11 menu +~1 week (deferred).
