# TZ: Explorer submenu for dual app lists

Date: 2026-07-06

## Context

v2.46.0-r7 keeps the existing Explorer context menu include-only:

- "Add to VPNRouter" adds the process to `RoutingAppsInclude`.
- "Remove from VPNRouter" removes the process from `RoutingAppsInclude`.

This avoids adding one app to both app lists by accident.

## Goal

Replace the flat Explorer verb with a VPNRouter submenu:

- `VPNRouter -> Through VPN`
- `VPNRouter -> Bypass VPN`
- `VPNRouter -> Remove from lists`

## Requirements

- "Through VPN" writes only to `RoutingAppsInclude`.
- "Bypass VPN" writes only to `RoutingAppsExclude`.
- "Remove from lists" removes the process from both lists and matching custom rows.
- Keep category support for both add flows if the existing category submenu remains.
- Do not depend on the current in-app routing mode.

## Verification

- Shell add through VPN works while routing mode is include.
- Shell add through VPN works while routing mode is exclude.
- Shell bypass works while routing mode is include.
- Shell bypass works while routing mode is exclude.
- Remove from lists clears both include and exclude state.
