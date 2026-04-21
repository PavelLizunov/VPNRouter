# VPNRouter — Self-healing roadmap

User wants systemic self-healing after v2.22.3 exposed how fragile
the app is to stale / corrupt state. Rolling out across v2.22.4,
v2.23.0, v2.24.0.

## Why

The app currently has only point-fixes:
- Tolerant profile resolver (v2.22.0)
- Install receipt detecting "update didn't land" (v2.22.0)
- ActiveProfile yaml sanitization (v2.22.0)

No system-wide strategy. Things that can hang / fail silently:
- ProcessScanner on corrupt `ProgramData\profiles\default.json`
  (hit in v2.22.3 — user had to wipe file manually)
- Corrupt `config.yaml` — no migration, crash on parse
- sing-box / firewall orphans from a previous crashed session
- Failed apt / deb / exe updates — user has to figure out recovery
- No "reset" button anywhere in the UI

## Level 1 — v2.22.4 hotfix (~2 hours)

### 1.1 ProcessScanner timeout (30 s)
Wrap `IProcessScanner.ScanForProfile` in `Task.Run` + `Wait(30_000)`.
On timeout: log warning + use empty process list + continue startup
with degraded functionality (split mode won't route anything, but
VPN still starts; user sees "Scan timed out — switch to Full mode
or check logs").

### 1.2 Auto-migrate stale `%ProgramData%\VPNRouter\profiles\default.json`
On startup (before loading profiles):
- If file exists and either fails to parse OR is missing ≥3 of the
  8 standard v2.22 group names (Discord_Privacy, Messengers,
  AI_Tools, Browsers, Work_Suite, Streaming, Gaming, Privacy_Shell),
  rename it to `default.json.migrated-<yyyyMMdd-HHmmss>` and log.
- Fallback is bundled `<appDir>\profiles\default.json` (always current).

### 1.3 WMI child-lookup batching
Today `ProcessScanner.GetChildProcessNamesWmi` fires ONE WMI query
per parent PID. For a split profile with 20 browsers running, that's
20 sequential WMI calls, each 1-3 s = 20-60 s startup lag.

Fix: collect all parent PIDs first, then one WMI query with
`WHERE ParentProcessId IN (p1, p2, ...)`. O(1) WMI calls per scan
instead of O(N).

## Level 2 — v2.23.0 (~5 hours)

### 2.1 Safe mode (`VPNRouter.App --safe`)
CLI flag that forces the app to ignore user overrides:
- Skip `ProfileSources` from yaml (use only bundled).
- Skip `CustomCategories` / `CustomGroupApps` / `CustomApps`.
- Skip `ActiveProfile` (fall back to empty FullTunnel).
- Banner in UI: "Safe mode — configuration disabled".
- Purpose: recover when UI won't start because of bad config.

Discovered via: `VPNRouter.App --help`. Desktop entry gets a
"Start in Safe Mode" variant for Linux (secondary .desktop with
`Exec=...` + `--safe`).

### 2.2 "Reset to defaults" button in Settings UI
Under a new "Troubleshooting" section (or at the bottom of
Settings). Click:
1. Writes current `config.yaml` to `config.yaml.backup-<date>`.
2. Overwrites with `SettingsLoader.CreateDefaults()`.
3. Stops VPN if running.
4. Restarts app (so new settings load cleanly).

Confirmation dialog before wiping.

### 2.3 Lockfile + crash detection
On startup, create `%ProgramData%\VPNRouter\running.lock` with the
current PID. On clean shutdown, delete it.

If on startup the lock file exists AND the PID is either dead or
belongs to a different executable, previous session crashed.
Show a banner: "Previous run didn't shut down cleanly — see
`logs\vpnrouter<date>.log`. [View log] [Reset config] [Ignore]".

## Level 3 — v2.24.0 (~10 hours)

### 3.1 Schema version in config.yaml
Add `schema_version: 1` field at top of `AppSettings`. Increment
when breaking changes happen. On load:

```
var fileVersion = rawYaml.schema_version ?? 0;
var targetVersion = AppSettings.CurrentSchemaVersion;
if (fileVersion < targetVersion)
{
    var migrator = new SettingsMigrator();
    settings = migrator.Migrate(settings, fileVersion, targetVersion);
    settings.SchemaVersion = targetVersion;
    SettingsLoader.Save(settings);
}
```

Migrations live in `VPNRouter.Core/Services/SettingsMigrator.cs`
with a dictionary of migration functions keyed by `(from, to)`
version. Each is pure: takes `AppSettings`, returns modified.

### 3.2 `vpnrouter doctor` CLI command
```
vpnrouter doctor
  [OK]    config.yaml valid
  [OK]    profile catalogue v2.22 schema
  [WARN]  ProgramData\profiles\default.json newer than bundled
  [ERR]   sing-box binary not in expected location
  [OK]    no orphan firewall rules
  [OK]    clash API port free
```

Exit code 0 if all checks pass, non-zero if any errors. Useful
for CI / automated diagnostics / support.

### 3.3 Opt-in error reporting
When an unhandled exception occurs:
- Show dialog: "Error occurred. Send anonymized report?"
- If yes: collect stack trace + last 200 lines of app log (scrubbing
  paths with usernames, VLESS credentials, server IPs), POST to
  a sentry-style endpoint (self-hosted on homelab?).
- Toggle in Settings → Privacy → "Send crash reports" (default: off
  for privacy).

## Release sequencing

- **v2.22.4** — Level 1 items. Ships as patch. Skip prerelease,
  cut stable directly (hotfix path per release strategy).
- **v2.23.0** — Level 2 items. Rolling -rN candidates as usual.
- **v2.24.0** — Level 3. Schedule later once Level 1-2 are stable
  in the wild.

## Status tracker

### Level 1 (v2.22.4)
- [x] 1.1 ProcessScanner timeout
- [x] 1.2 Auto-migrate stale profiles/default.json
- [x] 1.3 WMI child-lookup batching

### Level 2 (v2.23.0 + v2.23.1)
- [x] 2.1 Safe mode (--safe flag)
- [x] 2.2 Reset to defaults (--reset flag + menu button)
- [x] 2.3 Lockfile + crash detection (log warning; UI banner deferred)

### Level 3 (v2.24.0)
- [x] 3.1 Schema version + migrator (baseline schema_version=1, skeleton for future)
- [x] 3.2 vpnrouter doctor CLI
- [x] 3.3 Crash report writer (no upload; writes to %DataDir%/crashes/crash-<stamp>.txt)
