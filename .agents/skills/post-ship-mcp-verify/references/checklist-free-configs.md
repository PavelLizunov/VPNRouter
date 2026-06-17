# Free Configs page checklist

Use when release notes mention free configs / public pool /
FreeConfigAggregator / FreeConfigDeepVerifier / GeoIP.

## Setup

1. Window already launched.
2. Click "Расширенные настройки" → "Публичные" tab (or "Free Configs"
   in EN).

## Verify page layout

3. Screenshot.
4. Confirm 6 master-detail sections (left tree, right detail pane):
   - Dashboard (counts: Total / Working / Timeout / Unreachable / TLS
     failed / Verified / Fake).
   - Configs table.
   - Saved subset.
   - Deep verify form.
   - Cleanup tools.
   - Sources management.

## Trigger Refresh

5. Click "↻ Обновить список" / "Refresh list".
6. Status text below should update with batched-search progress:
   - "Поиск 50 рабочих конфигов из пула 1500..."
   - "Найдено 5/50 · батч 1/30 · проверено 50/50".
7. Wait 1-2 minutes for batches to complete.

## Verify Deep verify

8. Click "✓✓ Найти рабочие конфиги" / "Find working configs".
9. Status updates per-probe:
   - "Найдено 3/10 · проверяю server.example.com:443 [DE]..."
10. Wait for completion: "Готово: найдено N реально рабочих (✓✓)".

## Connect to a verified config

11. Click on a row with ✓✓ status.
12. Click "Подключить" / "Connect" button at bottom.
13. VPN should connect within 30 seconds.
14. Stop after verification.

## Cleanup tools

15. Click "Убрать мусор" — removes dead entries.
16. Click "Только ✓✓" — keeps only verified.
17. Verify Dashboard counts update.

## Per-feature log checks

| Looking for | Pattern |
|---|---|
| Refresh started | `[FreeConfigAggregator] RefreshAsync started` |
| Pool fetched | `[FreeConfigPoolFetcher] Fetched N entries from server-side pool` |
| Batch progress | `[FreeConfigAggregator] Batch N/M` |
| Deep verify | `[FreeConfigDeepVerifier] Probing server:port` |
| GeoIP enrichment | `[FreeConfigGeoIp] MaxMind lookup` |
| Cache migration heal | `[FreeConfigCache] Healed N sub-5ms entries` |

## Expected log noise

- `[FreeConfigTester] TCP-only probe timeout for server:port` —
  expected during refresh, just means that server is dead.
- `[FreeConfigPoolFetcher] Skip-2-stages: pool >= 1000 entries` —
  expected optimization.

## Pass criteria summary

- Page renders all 6 sections.
- Refresh fetches + tests configs.
- Deep verify finds 1+ verified configs.
- Connect to a verified row works.
- Cleanup tools mutate dashboard counts.

## Screenshots to attach

- `tmp-rN-freeconfigs-dashboard.png` — initial state with counts.
- `tmp-rN-freeconfigs-refreshing.png` — mid-refresh status.
- `tmp-rN-freeconfigs-verified.png` — row with ✓✓ status.
- `tmp-rN-freeconfigs-connected.png` — after Connect.
