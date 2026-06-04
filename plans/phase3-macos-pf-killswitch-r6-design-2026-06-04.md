# r6 — macOS pf kill-switch (MacFirewallManager) design + constraints (2026-06-04)

Task #184 / #131. Decision (user, 2026-06-04): **global egress block, full-tunnel
only, default-OFF.** This doc captures the 3 constraints found while reading the
firewall wiring — they make r6 a careful, brick-risk task that must NOT be
rushed, and they drive the implementation shape below.

## Wiring (confirmed)
- `PlatformServices.CreateFirewallFactory` → `() => new FirewallManager(logger)`
  (Windows) / `() => new NullFirewallManager(logger)` (non-Windows). r6 swaps the
  macOS branch to `MacFirewallManager`.
- `StartupPipeline` (line ~975): `if (profile.BlockOnVpnFail)
  firewall.CreateBlockRules(scanResult.ProcessNames)` — created DISABLED.
- `HealthMonitor`: `EnableBlockRules()` on sing-box crash (lines 474/794),
  `DisableBlockRules()` only after a HEALTHY restart (lines 384/580; "defer
  disable until restart healthy", task #171). `DeleteAllRules()` on clean stop.

## Constraint 1 — pf is packet-level, not process-level
Windows netsh blocks the ROUTED app process names and leaves sing-box free. pf
has no process concept → a macOS kill-switch can only block at packet level
(IP/port/interface). Chosen semantics: **global egress block, engaged only in
full-tunnel.** Full-tunnel signal = `CreateBlockRules` called with an EMPTY
process list (the pipeline skips the scan in full-tunnel → empty; split passes
the routed apps → non-empty). Non-empty ⇒ split ⇒ log "macOS kill-switch is
full-tunnel-only" + stay disarmed.

## Constraint 2 — a global block also blocks sing-box's own reconnect (CRITICAL)
HealthMonitor keeps the block ON during the restart window (disable only after a
healthy restart). On Windows that's fine — the block targets the routed apps, not
sing-box. On macOS a global egress block also blocks sing-box's reconnect to the
VPN server → HealthMonitor never sees success → never disables → **permanent
block = bricked connection until manual `pfctl -F all`.**
**Therefore the pf ruleset MUST allow egress to the VPN server IP:port** (so the
tunnel can re-establish), block everything else. → MacFirewallManager needs the
server IP(s). Plumb by reading `AppPaths.CurrentConfigPath` (current.json)
outbounds `server` field(s) at arm time, OR thread the resolved server list from
the pipeline. Reading current.json at CreateBlockRules is the least-invasive.

## Constraint 3 — macOS pf anchor referencing + don't clobber system rules
An anchor's rules are only evaluated if the main pf.conf references it
(`anchor "name"`). macOS ships its own pf.conf (`anchor "com.apple/*"`); ours
isn't referenced by default. Options:
- (a) `pfctl -a com.ninitux.vpnrouter.killswitch -f <rules>` + add a reference to
  the main ruleset — invasive (edits system pf.conf; SIP/upgrade fragility).
- (b) Load a COMPLETE ruleset via `pfctl -f` — clobbers macOS sharing/other rules.
  Must snapshot (`pfctl -sr`) + restore on disable. Heavy + risky.
- (c) Use a token-based ephemeral ruleset (`pfctl -E` ref-count) + a dedicated
  anchor loaded and referenced for the block window only.
Recommended: (a) with a minimal, well-scoped anchor + a fail-safe that ALWAYS
flushes on Disable/Delete/Dispose/process-exit (never leave the Mac blocked).
Needs prototyping on the real Mac (SSH) before trusting the mechanism.

## Implementation shape (MacFirewallManager : IFirewallManager)
- ctor `(ILogger?, IProcessRunner? = ProcessRunner)` — testable.
- `_armed` (full-tunnel), `_loaded` (anchor active), `_serverIps` (allow-list),
  `const Anchor = "com.ninitux.vpnrouter.killswitch"`.
- `CreateBlockRules(names)`: names non-empty → disarm + log full-tunnel-only;
  empty → arm, read server IP(s) from current.json. Do NOT load pf yet.
- `EnableBlockRules()`: armed → build rules (allow lo0 + utun* + RFC1918 + the
  server IP(s); `block out` everything else) → load anchor via sudo pfctl + `-E`.
  Set `_loaded`. Not armed → no-op log.
- `DisableBlockRules()`: `_loaded` → flush anchor (`sudo -n pfctl -a <anchor>
  -F all`) + ref-count `-X` if used. `_loaded=false`.
- `DeleteAllRules()` + `Dispose()`: fail-safe flush (idempotent). Dispose MUST
  never throw and MUST flush if `_loaded` — the anti-brick backstop.
- All sudo calls success-checked (reuse the r5 RunResult pattern); on failure,
  Warning + (for Enable) do NOT claim "blocked".

## sudoers (extend EnsureMacSudoAccess + InstallGuide.html — like r5 #2)
Add: `{user} ALL=(root) NOPASSWD: /sbin/pfctl *`. Bump SudoersFormatMarker →
re-grant (user-marker fast-path means one prompt). Update InstallGuide.html.

## Tests (FakeProcessRunner)
- arm only on empty process list; split (non-empty) → disarmed, Enable = no-op.
- Enable loads anchor with allow-rules incl. the server IP; block-out present.
- Disable/Delete/Dispose flush the anchor (assert the `-F`/`-X` pfctl calls).
- Enable when sudo pfctl fails → no "blocked" claim, Warning.
- Dispose after a failed Disable still attempts a flush (anti-brick).

## MANDATORY live gate (SSH, I own — not the user's regression)
On `slovn@192.168.0.246` (recovery always available: `sudo pfctl -F all && sudo
pfctl -X`):
1. Install r6, full-tunnel + block_on_vpn_fail ON, connect.
2. `sudo kill -9 <sing-box pid>` mid-session → verify: egress blocked
   (curl to a public IP fails) BUT the server IP is reachable, and HealthMonitor
   restarts sing-box and it RECONNECTS (block auto-disables) within the backoff
   window — NO permanent brick.
3. Clean Stop → anchor fully flushed (`pfctl -a <anchor> -sr` empty; general
   connectivity restored).
4. Kill the App while blocked → Dispose/process-exit flush restores connectivity.
Only after all 4 pass is r6 trustworthy. Ship default-OFF regardless.

## Why this is a separate, careful pass (not a tail-of-session add)
Constraints 2 + 3 are real brick vectors. A half-done global block (no server-IP
allow, or an anchor that doesn't flush on failure) bricks the Mac's network. This
must be implemented with full context budget + the live SSH gate, per the
"ship firewall separate from DNS / brick-risk" lesson. r4 (YouTube QUIC) + r5
(DNS robustness) are already shipped & CI-green independently, so nothing is
blocked by deferring r6 to its own focused pass.
