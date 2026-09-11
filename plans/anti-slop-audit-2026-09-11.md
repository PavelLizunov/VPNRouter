# README audit, 2026-09-11

Scope: README.md and README.ru.md at c8a5eef0 (unchanged from accepted main2689ee77). Both read completely. Source checks only; no UI rendering, installer execution, external-link availability or full product verification claimed. Skills: repository-readme and anti-slop, AFTER mode. Proposed edits await approval.

1. MEDIUM — Historical metrics presented as current. EN111-127 / RU107-123 state 53 features and complexity percentages. Their cited catalog explicitly describes a 2026-05-17 v2.32.3 baseline (catalog1-13). Numbers are sourced, not fabricated, but are not a current inventory. Replace the duplicated table/counts with a clearly dated historical-catalog link; retain current feature descriptions and platform limitations.
2. MEDIUM — Stale async exception. Both README228 cite VpnEngine.cs:461 as the sole .Result exception. Current Core search finds no such occurrence in VpnEngine, but finds guarded task results in UpdateChecker and platform firewall helpers. Remove the stale location and unsupported exhaustive rule rather than implying those occurrences are runtime defects.
3. MEDIUM — Windows-specific ETW in a general walkthrough. Both README250 describe ETW without a platform qualifier; EtwProcessMonitor.cs1 is guarded by PLATFORM_WINDOWS. Qualify ETW as Windows-only. RU249 says the OS routes all traffic while EN249 specifically says Windows; avoid implying every platform/mode captures all traffic, given the documented routing exceptions.

Pending verification: universal ILogger claim (both229), local links/anchors and build examples. These are not yet confirmed findings. No product changes proposed. Archive/cleanup history belongs in plans, not public README.

Delivery gate: provenance checked for the historical metrics; the three items above remain open, so prose acceptance is not PASS. UI contrast, visual purpose and click-through are not applicable to this source-only prose audit. External links remain untested.
