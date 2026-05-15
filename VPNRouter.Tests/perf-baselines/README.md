# Performance baselines

Pinned performance baselines for regression detection per
`plans/android-development-methodology.md` §5.

## Files

| File | Benchmark | Captured |
|---|---|---|
| `android-cold-start.json` | MainActivity → first paint | TBD Phase 1.5 |
| `android-singbox-connect.json` | Connect (config → tunnel up) | TBD Phase 1.5 |
| `desktop-config-generation-500apps.json` | ConfigGenerator.Generate, 500 apps | TBD |

## Format

```json
{
  "benchmark": "android-cold-start",
  "device_class": "mid-range",  // mid-range | low-end | flagship
  "device_model": "Pixel 6a",   // for reference
  "captured_at": "2026-05-12T00:00:00Z",
  "runs": 10,
  "metrics": {
    "p50_ms": 1200,
    "p95_ms": 1420,
    "p99_ms": 1680
  },
  "thresholds": {
    "p95_regression_fail_pct": 20
  },
  "notes": "captured on physical Pixel 6a Android 14"
}
```

## Updating

Only when:
- Hardware class added (new entry, don't overwrite)
- Intentional perf change (PR + reviewer sign-off)
- Upstream lib bump (libbox version comment in JSON)

See methodology §5.3.

## Meta-test #5

`tools/check-methodology.sh #5` flags baselines older than 90 days.
