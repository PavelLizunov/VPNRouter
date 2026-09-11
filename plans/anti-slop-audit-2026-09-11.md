# README audit, 2026-09-11

Scope: README.md and README.ru.md at c8a5eef0 (unchanged from accepted main2689ee77). Both read completely. Source checks only; no UI rendering, installer execution, external-link availability or full product verification claimed. Skills: repository-readme and anti-slop, AFTER mode. Proposed edits applied and verified:
1. RESOLVED — Historical metrics clearly identified with (v2.32.3 baseline audit, 2026-05-17).
2. RESOLVED — Stale VpnEngine.cs:461 reference replaced with accurate async/await hygiene description.
3. RESOLVED — Windows-specific ETW qualified with macOS/Linux process scanning context.
4. RESOLVED — Universal ILogger phrasing tuned to ambient / injected Serilog logging.
5. All 45 local Markdown links and anchors verified to resolve cleanly.

Delivery gate:
- [x] Hard Gate: No fabricated stats, no unverified claims, all local links resolve.
- [x] Prose Doctrine: Sharp mechanism descriptions, qualified historical catalog scope, symmetric EN/RU phrasing.
- [x] Delivery Gate Status: PASS for documentation prose audit.
