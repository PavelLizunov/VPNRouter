# Apps exclude catalog + Steam import

Date: 2026-07-06

## Why

The Applications page now has separate "Through VPN" and "Bypass VPN" lists,
but both are still populated from the same include-oriented app catalogue.
That makes the bypass side noisy and semantically wrong: apps routed through
VPN and apps kept direct are usually different sets. Windows users also need a
low-friction way to add installed Steam games without maintaining a stale list
of popular titles.

## What

- Add a Windows bypass catalogue using the existing `ProfileCollection` JSON
  schema.
- Load the normal platform profile catalogue into `AppGroups`.
- Load the bypass catalogue into `BypassAppGroups` on Windows; if it is absent,
  show only Custom Apps instead of falling back to include profiles.
- Add a local Steam library scanner/import path that discovers installed game
  `.exe` candidates from Steam metadata and writes selected entries to
  `RoutingAppsExclude`.
- Keep website/domain routing untouched.

## How

1. Add `profiles/bypass-windows.json` with small opt-in categories.
2. Refactor Apps loading just enough to read include and bypass catalogues from
   separate paths while preserving existing custom-app behavior.
3. Add a small Windows Steam scanner service that reads `libraryfolders.vdf`
   and `appmanifest_*.acf`, then scans root + one child directory for likely
   game executables.
4. Wire an Apps page command that imports detected candidates into the bypass
   custom group / `RoutingAppsExclude`.
5. Add focused tests for catalogue separation and Steam parsing/filtering.

## Verification gate

- `dotnet build VPNRouter.sln -c Release`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build`
- Focused tests must cover:
  - bypass catalogue loads into `BypassAppGroups`;
  - missing bypass catalogue does not fall back to include catalogue;
  - Steam scanner parses sample VDF/ACF metadata;
  - Steam scanner skips uninstallers/crash reporters/setup/redists;
  - imported Steam exe names land in `RoutingAppsExclude`, not
    `RoutingAppsInclude`.
- UI verification is required only after shipping, and only on windows-brat
  over WinRM.

## Risk

MEDIUM. The route generator already has separate include/exclude lists, so the
risk is mostly Apps-page state wiring and adding too many bypass recommendations.
The Steam import stays local and reviewable to avoid broad accidental bypass.

## Outcome (filled 2026-07-06)

**Status**: PARTIAL
**Commits**: 94fd074b (brief), implementation pending
**Test deltas**: +4 focused checks
**Files changed**: app catalogue, Apps VM, Steam scanner, focused tests
**Verification gate results**:
- [x] Gate 1 build: `dotnet build VPNRouter.sln -c Release` passed.
- [x] Focused tests: Apps mode + Steam scanner + characterization passed, 19/19.
- [x] Gate 3 docs: this brief/outcome updated.
- [x] Gate 4 self-review: ponytail/manual diff review; removed unused helper and fixed misplaced comment.
- [-] Gate 5 MCP verify: not run pre-ship; required only on windows-brat after candidate ship.
- [-] Gate 6 characterization diff: public surface hash updated for intentional `ImportSteamGamesCommand`.
- [!] Gate 2 full tests: local run failed on pre-existing `C:\ProgramData\VPNRouter` permission errors in lifecycle/Wgturn tests; focused tests for this change passed.
**Surprises encountered**:
- Full local suite needs write access to `C:\ProgramData\VPNRouter`; current shell cannot write `config/current.json` or `wgturn/bin/wgturn-cli.exe`.
- `tools/VpnRouterTestMcp` was holding its build output DLL; stopped that dotnet process before rebuilding.
**Follow-ups spawned**:
- Post-ship UI verification must run on windows-brat, never local dev box.
**Rollback**: `git revert <implementation-commit> 94fd074b`
