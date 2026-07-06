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

Pending.
