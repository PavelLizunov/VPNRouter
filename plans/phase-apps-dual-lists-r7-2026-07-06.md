# r7 Applications Dual Lists Brief

## Why

Applications include and exclude are different user intents. The storage already
has separate `RoutingAppsInclude` and `RoutingAppsExclude`, but the UI presents
one app list whose checked state changes with the current routing mode. That
makes the two sets feel like one mutable list. r7 makes both sets visible and
editable independently.

## What

- Applications page: show separate "Through VPN" and "Bypass VPN" sections or
  tabs.
- ViewModel: expose independent include/exclude app group collections.
- App catalog: add OpenAI Codex using stable process/package matching, not a
  versioned AppX path.
- Explorer context menu: leave existing add/remove behavior as include-only.
- Follow-up plan: future shell submenu for through/bypass/remove.

## How

1. Trace current `ApplicationsPage.axaml`, app group VM, and routing app list
   mutation flow.
2. Reuse the existing app catalog/group builder; build two group views from the
   same catalog with different selection sets.
3. Bind include UI to `RoutingAppsInclude` and exclude UI to
   `RoutingAppsExclude`.
4. Keep `RoutingAppsMode` as the runtime policy only; do not use it to rewrite
   checkbox state in either visible list.
5. Add the smallest VM tests that prove the two lists do not bleed into each
   other.
6. Add a follow-up TZ for the shell submenu instead of expanding Explorer verbs
   in r7.

## Verification Gate

- `dotnet build VPNRouter.sln -c Release`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~MainWindowViewModelAppsModeTests|FullyQualifiedName~AppItemViewModelBridgeTests"`
- Existing AppsMode/AppItem tests stay green.
- Headless page/binding smoke for Applications if available.
- Release candidate `v2.46.0-r7` uses post-ship UI verification on
  `windows-brat` only, never on the dev box.

## Risk

MEDIUM. The storage model is already split, but the Applications UI is user
facing and app list grouping touches many bindings.

## Outcome

## Outcome (filled 2026-07-06)

**Status**: PARTIAL gate, implementation ready.

**Test deltas**: added focused VM/shell tests for separate include/exclude UI
state, shell include-only behavior, mirrored custom-row removal, and updated
the public-surface pin.

**Verification gate results**:

- [x] Build: `dotnet build VPNRouter.sln -c Release` exits 0.
- [x] Focused tests: `MainWindowViewModelAppsModeTests`,
  `ShellVerbRoutingTests`, `ScrubRoutingForAppBridgeTests`,
  `MainWindowViewModelCharacterizationTests` pass 23/23.
- [x] Codex catalog entry uses `Codex.exe` / `Codex*.exe`, not a versioned
  WindowsApps path.
- [x] Explorer shell add/remove remains include-only.
- [x] Follow-up TZ created:
  `plans/tz-codex-shell-menu-dual-app-lists-2026-07-06.md`.
- [!] Full suite: with `ProgramData` redirected to `.tmp-programdata-tests`,
  `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build`
  passed 2345/2352, skipped 2, failed 5 unrelated tests:
  `SingBoxManagerProcessExitLeakTests.DisposedManagers_AreNotRetainedByProcessExitHook`,
  two `SingBoxManagerRestartTunLockTests`, and two `VisualDiffTests`
  baseline diffs.

**Surprises encountered**:

- The local `tools/VpnRouterTestMcp` process locked its own build output; it
  was stopped before the full solution build. VPNRouter itself was not launched
  locally.
- Running the full suite without redirecting `ProgramData` produces unrelated
  access-denied failures against `C:\ProgramData\VPNRouter`.

**Follow-ups spawned**:

- Shell submenu TZ for explicit Through VPN / Bypass VPN / Remove from lists.

**Rollback**:

- Revert the implementation commit for r7 apps dual lists.
