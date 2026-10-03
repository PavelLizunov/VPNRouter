# N-1: the night pool (owner, 2026-10-04)

The owner's pool for the night, in the owner's words reduced to tasks. Note from the owner: Zapret and Telegram proxy do not exist on Android (Windows only), so no Android work for them.

## Items

1. **Version choice in Settings.** A list of published versions with "install this version" so that every future version can go back to an older one (from versions that predate the selector one can only move forward; that is expected and acceptable).
   Assessment: a good idea with guard rails. (a) The config schema (`config.yaml` schema=8) is migrated forward only, so a downgrade across a schema bump can make the old app fail to read the file: back up `config.yaml` before the switch and publish the schema number
   per release so the list can warn ("this version uses config schema 7, your file is schema 8, your settings will be reset to a backup"). (b) Auto-update must not undo the choice: choosing a version pins it ("stay on this version", auto-update paused) until the owner unpins. (c) Windows service and driver files are replaced
   by the installer; a downgrade must go through the same installer path as an update with the sha256 check. (d) Prereleases are shown only on the experimental channel. (e) Android: the same through `AndroidUpdater` (APK downgrade needs uninstall unless the APK is newer in versionCode: the list must say so).
2. **A denser UI MCP.** Beyond clicks: a live functional harness against the real Windows app on the worker (every main tab and every inner tab, section, segment; connect/disconnect cycles per mode with timings; connect to each config of the subscription with the time to connected;
   Test all / Deep verify with timings and results; auto-select; mode switching; log scanning for warnings/errors/exceptions after each scenario; screenshots) exposed as `live_*` tools of the MCP server, reports as JSON plus images.
3. **Review and fix ping measurement and automatic server choice.** Everything that measures a ping or chooses a server: `TcpTlsProbe`, `ServerHealthProbe`, `ServerHealthClassifier`, `ConnectionIntentScorer`, Test all / Deep verify in the view model, auto-select ("quick web test"),
   SmartConnect, the failover engine and `HealthMonitor`. Evidence first (the tester's logs: all probes "Implausible" while the tunnel is up, silent UDP counted as alive, "Test all" shows dashes while connected), then characterization tests, then fixes.

## Order

3 first (user-visible faults, and its measurements feed the harness), then 2 (live harness, which verifies 3 on the real machine), then 1 (largest design surface). Each item is split into PRs with a test; a release candidate is cut after a block of related changes, not after every PR.

## Rules for the night

No agents. Everything heavy on the workers. Nothing is published as a stable release. Prereleases may be published (standing mandate); the notes say what was and was not verified.

## Outcome

Pending.
